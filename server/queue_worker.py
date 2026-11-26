import fcntl
import json
import logging
import os
import re
import shutil
import signal
import sqlite3
import stat
import subprocess
import threading
from contextlib import contextmanager
from pathlib import Path


LOGGER = logging.getLogger(__name__)
PROGRESS_MARKER = "__VD_PROGRESS__:"
FILE_MARKER = "__VD_FILE__:"
MEDIA_FORMATS = (
    "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,mpegts,avi,flv,asf,mp3,wav,ogg,"
    "aac,mpeg,flac,aiff,ac3,eac3,m4v"
)
MEDIA_EXTENSIONS = frozenset(
    "mov mp4 m4a m4v 3gp 3g2 mj2 mkv mka webm ts m2t m2ts avi flv f4v asf wmv wma mp3 "
    "wav ogg ogv oga opus aac mpeg mpg vob f4a adts flac aiff aif ac3 eac3".split()
)
CHILD_ENV_KEYS = frozenset({
    "PATH", "HOME", "LANG", "LC_ALL", "LC_CTYPE", "DENO_DIR", "SSL_CERT_FILE", "SSL_CERT_DIR",
    "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
    "http_proxy", "https_proxy", "all_proxy", "no_proxy",
})


class DownloadError(Exception):
    pass


def engine_error(line: str) -> str:
    line = line.casefold()
    if "no space left on device" in line:
        return "На сервере закончилось место."
    if "http error 403" in line:
        return "Источник отклонил запрос (HTTP 403)."
    if "http error 429" in line:
        return "Источник ограничил частоту запросов (HTTP 429)."
    if "unsupported url" in line:
        return "Этот источник или тип ссылки не поддерживается."
    if "sign in" in line or "private video" in line:
        return "Источник требует авторизацию."
    return "Не удалось скачать видео. Проверьте ссылку и доступность источника."


def download_title(value: object) -> str:
    if not isinstance(value, str):
        return "Видео"
    title = re.sub(r'[\x00-\x1f\x7f-\x9f<>:"/\\|?*\ud800-\udfff\u202a-\u202e\u2066-\u2069]', "_", value)
    title = title[:120].encode("utf-8")[:220].decode("utf-8", errors="ignore").strip(" .") or "Видео"
    if re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])", title.partition(".")[0]):
        title = f"_{title}"
    return title


class JobStore:
    def __init__(self, data_dir: Path, *, yt_dlp: str = "yt-dlp", ffprobe: str = "ffprobe"):
        root = data_dir.resolve()
        self._staging = root / "staging"
        self._files = root / "files"
        self._yt_dlp = yt_dlp
        self._ffprobe = ffprobe
        self._lock = threading.Lock()
        self._worker: threading.Thread | None = None
        self._process: subprocess.Popen[str] | None = None
        self._stopping = False

        root.mkdir(mode=0o700, parents=True, exist_ok=True)
        self._owner = os.open(root / "queue.lock", os.O_CREAT | os.O_RDONLY | os.O_NOFOLLOW, 0o600)
        db = None
        try:
            fcntl.flock(self._owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
            for directory in (self._staging, self._files):
                if directory.is_symlink():
                    raise OSError("Queue data directories must be local directories")
                directory.mkdir(mode=0o700, exist_ok=True)
            database = root / "queue.sqlite3"
            os.close(os.open(database, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600))
            db = sqlite3.connect(database, check_same_thread=False)
            db.row_factory = sqlite3.Row
            self._db = db
            self._db.execute("""
                CREATE TABLE IF NOT EXISTS jobs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL DEFAULT 'queued',
                    title TEXT,
                    filename TEXT,
                    size INTEGER,
                    progress INTEGER,
                    error TEXT
                )
            """)
            self._recover()
        except BaseException:
            if db is not None:
                db.close()
            os.close(self._owner)
            raise

    def _recover(self) -> None:
        active_jobs = self._db.execute("SELECT id FROM jobs WHERE status = 'downloading'").fetchall()
        for row in active_jobs:
            # The file can exist if the previous process stopped after linking it but before commit.
            self._file_path(row["id"]).unlink(missing_ok=True)
        for stage in self._staging.iterdir():
            row = self._db.execute("""
                SELECT id FROM jobs WHERE id = ? AND status IN ('downloading', 'completed')
            """, (stage.name,)).fetchone()
            # Completion is committed before temporary files are cleaned. Only clear owned jobs.
            if row is not None and stage.name == str(row["id"]) and stage.is_dir():
                shutil.rmtree(stage)
        with self._db:
            self._db.execute("""
                UPDATE jobs SET status = 'interrupted', progress = NULL,
                    error = 'Загрузка прервана при остановке сервера.'
                WHERE status = 'downloading'
            """)

    def list_jobs(self) -> dict:
        with self._lock:
            rows = self._db.execute("SELECT * FROM jobs ORDER BY id").fetchall()
            return {"jobs": [dict(row) for row in rows], "running": self._worker is not None}

    def add(self, urls: list[str]) -> int:
        with self._lock, self._db:
            return self._db.executemany(
                "INSERT INTO jobs(url) VALUES (?) ON CONFLICT(url) DO NOTHING",
                ((url,) for url in urls),
            ).rowcount

    def start(self) -> bool:
        with self._lock:
            if self._worker is not None or self._stopping:
                return False
            jobs = self._db.execute("SELECT id, url FROM jobs WHERE status = 'queued' ORDER BY id").fetchall()
            if not jobs:
                return False
            worker = threading.Thread(target=self._run_session, args=(jobs,), name="download-queue")
            self._worker = worker
            try:
                worker.start()
            except RuntimeError:
                self._worker = None
                raise
            return True

    def retry(self, job_id: int) -> bool:
        with self._lock, self._db:
            return self._db.execute("""
                UPDATE jobs SET status = 'queued', progress = NULL, error = NULL
                WHERE id = ? AND status IN ('failed', 'interrupted')
            """, (job_id,)).rowcount == 1

    def delete(self, job_id: int) -> bool:
        with self._lock, self._db:
            return self._db.execute("""
                DELETE FROM jobs WHERE id = ? AND status IN ('queued', 'failed', 'interrupted')
            """, (job_id,)).rowcount == 1

    def _file_path(self, job_id: int) -> Path:
        return self._files / str(job_id)

    def get_file(self, job_id: int) -> tuple[Path, str] | None:
        with self._lock:
            row = self._db.execute("""
                SELECT filename, size FROM jobs WHERE id = ? AND status = 'completed'
            """, (job_id,)).fetchone()
            if row is None:
                return None
            path = self._file_path(job_id)
            try:
                info = path.lstat()
            except OSError:
                return None
            if not stat.S_ISREG(info.st_mode) or info.st_size != row["size"]:
                return None
            return path, row["filename"]

    def close(self) -> None:
        with self._lock:
            if self._stopping:
                return
            self._stopping = True
            worker, process = self._worker, self._process
            if process is not None:
                self._signal_process(process, signal.SIGTERM)
        if worker is not None:
            worker.join(timeout=3)
            if worker.is_alive() and process is not None:
                with self._lock:
                    if self._process is process:
                        self._signal_process(process, signal.SIGKILL)
            worker.join()
        with self._lock:
            self._db.close()
            os.close(self._owner)

    def _run_session(self, jobs: list[sqlite3.Row]) -> None:
        try:
            for job in jobs:
                job_id = job["id"]
                with self._lock, self._db:
                    if self._stopping:
                        break
                    changed = self._db.execute("""
                        UPDATE jobs SET status = 'downloading' WHERE id = ? AND status = 'queued'
                    """, (job_id,)).rowcount
                if changed:
                    self._run_job(job_id, job["url"])
        except (OSError, sqlite3.Error):
            LOGGER.exception("Could not update or access the download queue")
        finally:
            with self._lock:
                self._worker = None

    def _run_job(self, job_id: int, url: str) -> None:
        stage = self._staging / str(job_id)
        owns_stage = False
        error = None
        try:
            if stage.is_dir():
                # A prior attempt may have been unable to clear its own temporary files.
                shutil.rmtree(stage)
            stage.mkdir(mode=0o700)
            owns_stage = True
            path, title = self._download(job_id, url, stage)
            self._verify_media(path)
            self._publish(job_id, path, title)
        except DownloadError as failure:
            error = str(failure)
        except (OSError, sqlite3.Error):
            LOGGER.exception("Could not process media for job %d", job_id)
            error = "Ошибка сервера при сохранении файла. Проверьте место и доступность хранилища."
        finally:
            if owns_stage:
                try:
                    shutil.rmtree(stage)
                except OSError:
                    LOGGER.exception("Could not clear temporary files for job %d", job_id)
        if error is not None:
            with self._lock, self._db:
                status = "failed"
                if self._stopping:
                    status, error = "interrupted", "Загрузка прервана при остановке сервера."
                self._db.execute("""
                    UPDATE jobs SET status = ?, progress = NULL, error = ? WHERE id = ?
                """, (status, error, job_id))

    def _download(self, job_id: int, url: str, stage: Path) -> tuple[Path, str]:
        command = [
            self._yt_dlp, "--ignore-config", "--no-plugin-dirs", "--no-cache-dir",
            "--no-playlist", "--playlist-items", "1", "--no-overwrites", "--no-simulate",
            "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3",
            "--encoding", "utf-8", "--no-colors", "--newline", "--progress", "--progress-delta", "0.5",
            "--progress-template", f"download:{PROGRESS_MARKER}%(progress._percent_str)s",
            "--print", f"after_move:{FILE_MARKER}%(.{{filepath,title}})j",
            "--output", str(stage / "media.%(ext)s"), "--", url,
        ]
        outputs = []
        progress = None
        last_error = ""
        with self._child(command) as process:
            for line in iter(lambda: process.stdout.readline(64 * 1024), ""):
                if line.startswith(PROGRESS_MARKER):
                    percent = re.fullmatch(r"\s*(\d{1,3})(?:\.\d+)?%\s*", line[len(PROGRESS_MARKER):])
                    if percent:
                        current = min(99, int(percent[1]))
                        if current != progress:
                            with self._lock, self._db:
                                self._db.execute("UPDATE jobs SET progress = ? WHERE id = ?", (current, job_id))
                            progress = current
                elif line.startswith(FILE_MARKER):
                    if outputs:
                        raise DownloadError("Движок вернул больше одного файла для этой ссылки.")
                    try:
                        outputs.append(json.loads(line[len(FILE_MARKER):]))
                    except ValueError:
                        raise DownloadError("Движок вернул неверное имя файла.") from None
                elif line.startswith("ERROR:"):
                    last_error = line
            if process.wait() != 0:
                raise DownloadError(engine_error(last_error))

        output = outputs[0] if outputs else None
        if not isinstance(output, dict) or not isinstance(output.get("filepath"), str):
            raise DownloadError("Движок не вернул готовый медиафайл.")
        path = Path(output["filepath"])
        if path.parent != stage or path.stem != "media":
            raise DownloadError("Движок вернул неверный путь к медиафайлу.")
        if path.suffix[1:].lower() not in MEDIA_EXTENSIONS:
            raise DownloadError("Расширение результата не соответствует поддерживаемому медиафайлу.")
        if not stat.S_ISREG(path.lstat().st_mode):
            raise DownloadError("Движок не вернул обычный медиафайл.")
        return path, download_title(output.get("title"))

    def _verify_media(self, path: Path) -> None:
        command = [
            self._ffprobe, "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MEDIA_FORMATS,
            "-read_intervals", "%+#32", "-show_packets", "-show_streams",
            "-show_entries", "stream=index,codec_type:stream_disposition=attached_pic:packet=stream_index",
            "-of", "json", "-i", str(path),
        ]
        try:
            with self._child(command) as process:
                output, _ = process.communicate(timeout=30)
                if process.returncode == 0:
                    media = json.loads(output)
                    playable = {
                        stream["index"] for stream in media.get("streams", [])
                        if stream.get("codec_type") in ("audio", "video")
                        and not stream.get("disposition", {}).get("attached_pic")
                    }
                    if any(packet.get("stream_index") in playable for packet in media.get("packets", [])):
                        return
        except (subprocess.TimeoutExpired, ValueError):
            pass
        raise DownloadError("Сервер не смог подтвердить, что файл содержит воспроизводимое медиа.")

    def _publish(self, job_id: int, source: Path, title: str) -> None:
        with source.open("rb") as media:
            size = os.fstat(media.fileno()).st_size
            os.fsync(media.fileno())
        destination = self._file_path(job_id)
        with self._lock:
            if self._stopping:
                raise DownloadError("Загрузка прервана при остановке сервера.")
            # A hard link publishes without replacing an existing file. The queue row gates access.
            os.link(source, destination, follow_symlinks=False)
            try:
                directory = os.open(self._files, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
                with self._db:
                    self._db.execute("""
                        UPDATE jobs SET status = 'completed', title = ?, filename = ?, size = ?, progress = 100
                        WHERE id = ?
                    """, (title, title + source.suffix, size, job_id))
            except BaseException:
                destination.unlink(missing_ok=True)
                raise

    @staticmethod
    def _signal_process(process: subprocess.Popen[str], sig: signal.Signals) -> None:
        try:
            os.killpg(process.pid, sig)
        except ProcessLookupError:
            pass

    @contextmanager
    def _child(self, command: list[str]):
        with self._lock:
            if self._stopping:
                raise DownloadError("Загрузка прервана при остановке сервера.")
            process = subprocess.Popen(
                command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                encoding="utf-8", errors="replace", start_new_session=True,
                env={key: os.environ[key] for key in CHILD_ENV_KEYS if key in os.environ},
            )
            self._process = process
        try:
            yield process
        finally:
            with self._lock:
                if process.returncode is None:
                    self._signal_process(process, signal.SIGKILL)
                self._process = None
            process.stdout.close()
            process.wait()
