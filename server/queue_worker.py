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
import time
from collections import deque
from contextlib import ExitStack, contextmanager
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import BinaryIO
from urllib.parse import urlsplit

from update_bridge import WorkGate


LOGGER = logging.getLogger(__name__)
PROGRESS_MARKER = "__VD_PROGRESS__:"
# yt-dlp interprets a bare identifier as a field name in --print.
TRANSFER_MARKER = "[__VD_TRANSFER__]"
FILE_MARKER = "__VD_FILE__:"
REMOTE_PLAN_MARKER = "__VD_REMOTE_PLAN__:"
REMOTE_JOB_ID = re.compile(r"[0-9a-f]{32}")
MIB = 1024 * 1024
STORAGE_HEADROOM = 64 * MIB
DISK_ERROR = "На сервере осталось мало свободного места."
COLLECTION_ERROR = "Сервер принимает только ссылки на отдельные видео."
REMOTE_RESULT_EXPIRED = "Срок хранения файла на сервере истёк. Запустите скачивание повторно."
REMOTE_RESULT_REMOVED = "Файл удалён из файлового менеджера серверной очереди."
REMOTE_RESULT_TTL = 60 * 60
REMOTE_STEM_UTF16_BYTES = 320
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


class RemoteResultExpired(Exception):
    pass


@dataclass(frozen=True)
class RemoteOptions:
    quality: str
    container: str
    video_codec: str
    embed_metadata: bool
    embed_chapters: bool

    def arguments(self) -> list[str]:
        height = "" if self.quality == "Best" else f"[height<={self.quality}]"
        codec = {"H264": "avc1", "VP9": "vp9", "AV1": "av01"}.get(self.video_codec)
        generic = f"bv*{height}+ba/b{height}"
        preferred = f"[vcodec^={codec}]" if codec else ""
        selector = f"bv*{height}{preferred}+ba/b{height}{preferred}/{generic}" if codec else generic
        args = ["-f", selector]
        if self.container != "Auto":
            container = self.container.lower()
            args.extend(("--merge-output-format", container, "--remux-video", container))
        if self.embed_metadata:
            args.append("--embed-metadata")
        if self.embed_chapters:
            args.append("--embed-chapters")
        return args


def engine_error(line: str, transfer_started: bool) -> str:
    line = line.casefold()
    if "no space left on device" in line:
        return "На сервере закончилось место."
    if "http error 403" in line:
        target = "медиафайла" if transfer_started else "данных о видео"
        return f"Источник отклонил запрос {target} (HTTP 403)."
    if 'impersonate target "chrome" is not available' in line:
        return "В сборке yt-dlp недоступен сетевой профиль Chrome."
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
    def __init__(self, data_dir: Path, *, yt_dlp: str = "yt-dlp", ffprobe: str = "ffprobe",
                 max_concurrent: int = 4, disk_reserve: int = 512 * MIB, work_gate: WorkGate | None = None):
        root = data_dir.resolve()
        self._root = root
        self._staging = root / "staging"
        self._files = root / "files"
        self._yt_dlp = yt_dlp
        self._ffprobe = ffprobe
        self._max_concurrent = max_concurrent
        self._disk_reserve = disk_reserve
        self.work_gate = work_gate if work_gate is not None else WorkGate(None)
        self._lock = threading.Condition()
        self._browser_batch = deque()
        self._active: set[int | str] = set()
        self._processes: dict[int | str, subprocess.Popen[str]] = {}
        self._disk_failures: set[int | str] = set()
        self._stopped = threading.Event()
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
            self._db.execute("""
                CREATE TABLE IF NOT EXISTS remote_jobs (
                    id TEXT NOT NULL PRIMARY KEY,
                    url TEXT,
                    options TEXT,
                    status TEXT NOT NULL DEFAULT 'queued',
                    title TEXT,
                    filename TEXT,
                    size INTEGER,
                    progress INTEGER,
                    error TEXT,
                    expires_at REAL
                )
            """)
            self._db.execute("CREATE INDEX IF NOT EXISTS remote_jobs_status ON remote_jobs(status)")
            with self._db:
                if not any(column["name"] == "expires_at" for column in self._db.execute("PRAGMA table_info(remote_jobs)")):
                    self._db.execute("ALTER TABLE remote_jobs ADD COLUMN expires_at REAL")
                self._db.execute("""
                    CREATE INDEX IF NOT EXISTS remote_jobs_expiry ON remote_jobs(expires_at) WHERE expires_at IS NOT NULL
                """)
            self._recover()
            self._disk_free = shutil.disk_usage(root).free
            self._workers = [threading.Thread(target=self._work, name=f"download-{number + 1}")
                             for number in range(max_concurrent)]
            self._storage_monitor = threading.Thread(target=self._watch_storage, name="download-storage")
            started = []
            try:
                with self._lock:
                    for thread in [*self._workers, self._storage_monitor]:
                        thread.start()
                        started.append(thread)
            except RuntimeError:
                self._stop_threads()
                for thread in started:
                    thread.join()
                raise
        except BaseException:
            if db is not None:
                db.close()
            os.close(self._owner)
            raise

    def _recover(self) -> None:
        for table in ("jobs", "remote_jobs"):
            active_jobs = self._db.execute(f"SELECT id FROM {table} WHERE status = 'downloading'").fetchall()
            for row in active_jobs:
                # The file can exist if the previous process stopped after linking it but before commit.
                self._file_path(row["id"]).unlink(missing_ok=True)
        for row in self._db.execute("SELECT id, size FROM remote_jobs WHERE status = 'received'"):
            try:
                self._remove_remote_file(row["id"], row["size"])
            except OSError:
                LOGGER.exception("Could not finish acknowledged cleanup for job %s", row["id"])
        for stage in self._staging.iterdir():
            if REMOTE_JOB_ID.fullmatch(stage.name):
                job_id = stage.name
            elif re.fullmatch(r"[1-9][0-9]{0,18}", stage.name) and int(stage.name) <= 2**63 - 1:
                job_id = int(stage.name)
            else:
                continue
            # Commit can precede cleanup. Match owned staging by its exact job ID.
            if self._read_job(job_id) is not None and stage.is_dir() and not stage.is_symlink():
                shutil.rmtree(stage)
        with self._db:
            now = time.time()
            for row in self._db.execute("""
                SELECT id FROM remote_jobs WHERE status = 'completed' AND expires_at IS NULL
            """).fetchall():
                # Older databases have no completion timestamp. The published
                # hard link's ctime reflects local publication, unlike media mtime.
                try:
                    expires_at = min(now, self._file_path(row["id"]).lstat().st_ctime) + REMOTE_RESULT_TTL
                except FileNotFoundError:
                    expires_at = now
                self._db.execute("UPDATE remote_jobs SET expires_at = ? WHERE id = ?", (expires_at, row["id"]))
            for table in ("jobs", "remote_jobs"):
                self._db.execute(f"""
                    UPDATE {table} SET status = 'interrupted', progress = NULL,
                        error = 'Загрузка прервана при остановке сервера.'
                    WHERE status = 'downloading'
                """)

    def list_jobs(self) -> dict:
        with self._lock:
            rows = self._db.execute("SELECT * FROM jobs ORDER BY id").fetchall()
            return {"jobs": [dict(row) for row in rows], "running": self._browser_running(),
                    "freeDiskBytes": self._disk_free}

    def add(self, urls: list[str]) -> int:
        with self._lock, self._db:
            return self._db.executemany(
                "INSERT INTO jobs(url) VALUES (?) ON CONFLICT(url) DO NOTHING",
                ((url,) for url in urls),
            ).rowcount

    def start(self) -> bool:
        with self._lock:
            if self._browser_running() or self._stopping:
                return False
            jobs = self._db.execute("SELECT id, url FROM jobs WHERE status = 'queued' ORDER BY id").fetchall()
            if not jobs:
                return False
            self._browser_batch.extend(jobs)
            self._lock.notify_all()
            return True

    def retry(self, job_id: int) -> bool:
        with self._lock, self._db:
            return self._db.execute("""
                UPDATE jobs SET status = 'queued', progress = NULL, error = NULL
                WHERE id = ? AND status IN ('failed', 'interrupted')
            """, (job_id,)).rowcount == 1

    def delete(self, job_id: int) -> bool:
        with self._lock, self._db:
            deleted = self._db.execute("""
                DELETE FROM jobs WHERE id = ? AND status IN ('queued', 'failed', 'interrupted')
            """, (job_id,)).rowcount == 1
            if deleted:
                self._browser_batch = deque(job for job in self._browser_batch if job["id"] != job_id)
            return deleted

    def _browser_running(self) -> bool:
        return bool(self._browser_batch) or any(isinstance(job_id, int) for job_id in self._active)

    @staticmethod
    def _table(job_id: int | str) -> str:
        return "jobs" if isinstance(job_id, int) else "remote_jobs"

    def _read_job(self, job_id: int | str) -> sqlite3.Row | None:
        return self._db.execute(f"SELECT * FROM {self._table(job_id)} WHERE id = ?", (job_id,)).fetchone()

    @staticmethod
    def _remote_expired(row: sqlite3.Row) -> bool:
        return (row["status"] == "failed" and row["error"] == REMOTE_RESULT_EXPIRED) or (
            row["status"] == "completed" and row["expires_at"] is not None and row["expires_at"] <= time.time()
        )

    @staticmethod
    def _remote_summary(row: sqlite3.Row) -> dict:
        summary = {"state": row["status"], **{field: row[field] for field in
                   ("progress", "title", "filename", "size", "error")}}
        if row["status"] == "failed" and row["error"] == COLLECTION_ERROR:
            summary["unsupported"] = True
        if JobStore._remote_expired(row):
            # Older clients still recognize the terminal 'failed' state.
            summary.update(state="failed", expired=True, progress=None, size=None, error=REMOTE_RESULT_EXPIRED)
        return summary

    def remote_capabilities(self) -> dict:
        with self._lock:
            return {"version": 1, "maxConcurrent": self._max_concurrent, "freeDiskBytes": self._disk_free}

    def put_remote(self, job_id: str, url: str, options: RemoteOptions) -> dict | None:
        encoded = json.dumps(asdict(options), sort_keys=True)
        with self._lock:
            row = self._read_job(job_id)
            if row is not None:
                if row["url"] is not None and (row["url"] != url or row["options"] != encoded):
                    return None
                return self._remote_summary(row)
            with self._db:
                self._db.execute("INSERT INTO remote_jobs(id, url, options) VALUES (?, ?, ?)",
                                 (job_id, url, encoded))
            self._lock.notify_all()
            return self._remote_summary(self._read_job(job_id))

    def list_remote(self, after: str) -> dict:
        page_size = 25
        with self._lock:
            now = time.time()
            rows = self._db.execute("""
                SELECT id, url, status, title, filename, size, progress, expires_at
                FROM remote_jobs
                WHERE id > ? AND (status IN ('queued', 'downloading')
                    OR (status = 'completed' AND expires_at > ?))
                ORDER BY id LIMIT ?
            """, (after, now, page_size + 1)).fetchall()
            jobs = [
                {"id": row["id"], "state": row["status"], "size": row["size"],
                 "progress": row["progress"], "expiresAt": row["expires_at"],
                 "name": (row["filename"] or row["title"] or "")[:240],
                 "source": (urlsplit(row["url"]).hostname or "")[:253]}
                for row in rows[:page_size]
            ]
            return {"jobs": jobs, "next": rows[page_size - 1]["id"] if len(rows) > page_size else None,
                    "freeDiskBytes": self._disk_free}

    def remove_remote_result(self, job_id: str) -> bool:
        with self._lock:
            row = self._read_job(job_id)
            if row is None:
                return False
            if row["status"] == "completed" and not self._remote_expired(row):
                with self._db:
                    self._db.execute("""
                        UPDATE remote_jobs SET status = 'failed', progress = NULL, error = ?, expires_at = ?
                        WHERE id = ?
                    """, (REMOTE_RESULT_REMOVED, time.time(), job_id))
                self._lock.notify_all()
            elif row["status"] != "failed" or row["error"] != REMOTE_RESULT_REMOVED:
                return False
            self._cleanup_remote_result(job_id, row["size"])
            return True

    def get_remote(self, job_id: str) -> dict | None:
        with self._lock:
            row = self._read_job(job_id)
            return self._remote_summary(row) if row is not None else None

    def cancel_remote(self, job_id: str) -> None:
        with self._lock:
            with self._db:
                self._db.execute("""
                    INSERT INTO remote_jobs(id, status) VALUES (?, 'cancelled')
                    ON CONFLICT(id) DO UPDATE SET status = 'cancelled', progress = NULL
                    WHERE remote_jobs.status IN ('queued', 'downloading')
                """, (job_id,))
            if self._read_job(job_id)["status"] == "cancelled":
                process = self._processes.get(job_id)
                if process is not None:
                    self._signal_process(process, signal.SIGKILL)
                self._lock.notify_all()

    def receive_remote(self, job_id: str) -> bool:
        with self._lock:
            row = self._read_job(job_id)
            if row is None:
                return False
            if row["status"] == "failed" and row["error"] in {REMOTE_RESULT_EXPIRED, REMOTE_RESULT_REMOVED}:
                # An already open transfer can finish even after its server copy is removed.
                # Keep the terminal state while accepting the late receipt.
                return True
            if row["status"] not in {"completed", "received"}:
                return False
            if row["status"] == "completed":
                # Keep the receipt durable even if cleanup is interrupted. Retrying the
                # same assignment can never start a new download or discard a new result.
                with self._db:
                    self._db.execute("""
                        UPDATE remote_jobs SET status = 'received', expires_at = NULL WHERE id = ?
                    """, (job_id,))
            self._remove_remote_file(job_id, row["size"])
            self._lock.notify_all()
            return True

    def _remove_remote_file(self, job_id: str, size: int) -> None:
        path = self._file_path(job_id)
        try:
            info = path.lstat()
        except FileNotFoundError:
            self._sync_files_directory()
            return
        if not stat.S_ISREG(info.st_mode) or info.st_size != size:
            raise OSError("Remote result does not match the stored file")
        path.unlink(missing_ok=True)
        self._sync_files_directory()

    def _file_path(self, job_id: int | str) -> Path:
        if isinstance(job_id, str) and not REMOTE_JOB_ID.fullmatch(job_id):
            raise ValueError("Invalid delegated job ID")
        return self._files / str(job_id)

    def open_file(self, job_id: int | str) -> tuple[BinaryIO, os.stat_result, str] | None:
        with self._lock:
            row = self._read_job(job_id)
            if isinstance(job_id, str) and row is not None and self._remote_expired(row):
                raise RemoteResultExpired
            if row is None or row["status"] != "completed":
                return None
            # Pin the result before releasing the lock so expiry or a receipt
            # can unlink it without interrupting an already open transfer.
            try:
                descriptor = os.open(self._file_path(job_id), os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            except OSError:
                return None
            try:
                info = os.fstat(descriptor)
                if stat.S_ISREG(info.st_mode) and info.st_size == row["size"]:
                    stream = os.fdopen(descriptor, "rb")
                    descriptor = None
                    return stream, info, row["filename"]
                return None
            finally:
                if descriptor is not None:
                    os.close(descriptor)

    def _cleanup_remote_result(self, job_id: str, size: int) -> None:
        self._remove_remote_file(job_id, size)
        with self._db:
            self._db.execute("UPDATE remote_jobs SET expires_at = NULL WHERE id = ?", (job_id,))

    def _expire_remote_files(self) -> None:
        with self.work_gate.shared() as granted:
            if not granted:
                return
            with self._lock:
                rows = self._db.execute("""
                    SELECT id, size, status FROM remote_jobs
                    WHERE expires_at <= ? AND (status = 'completed' OR (status = 'failed' AND error IN (?, ?)))
                """, (time.time(), REMOTE_RESULT_EXPIRED, REMOTE_RESULT_REMOVED)).fetchall()
                for row in rows:
                    if row["status"] == "completed":
                        # Commit before unlinking so a crash never leaves a live
                        # completion pointing at an already removed result.
                        with self._db:
                            self._db.execute("""
                                UPDATE remote_jobs SET status = 'failed', progress = NULL, error = ? WHERE id = ?
                            """, (REMOTE_RESULT_EXPIRED, row["id"]))
                        self._lock.notify_all()
                    try:
                        self._cleanup_remote_result(row["id"], row["size"])
                    except OSError:
                        LOGGER.exception("Could not remove expired or deleted result for job %s", row["id"])

    def _stop_threads(self) -> bool:
        with self._lock:
            if self._stopping:
                return False
            self._stopping = True
            self._stopped.set()
            for process in self._processes.values():
                self._signal_process(process, signal.SIGTERM)
            self._lock.notify_all()
            return True

    def close(self) -> None:
        if not self._stop_threads():
            return
        self._storage_monitor.join()
        for worker in self._workers:
            worker.join(timeout=1)
        with self._lock:
            for process in self._processes.values():
                self._signal_process(process, signal.SIGKILL)
        for worker in self._workers:
            worker.join()
        with self._lock:
            self._db.close()
            os.close(self._owner)

    def _watch_storage(self) -> None:
        next_expiry_check = time.monotonic()
        while not self._stopped.wait(0.25):
            now = time.monotonic()
            if now >= next_expiry_check:
                next_expiry_check = now + 1
                try:
                    self._expire_remote_files()
                except (OSError, sqlite3.Error):
                    LOGGER.exception("Could not check for expired remote results")
            try:
                free = shutil.disk_usage(self._root).free
            except OSError:
                free = None
            with self._lock:
                changed = self._disk_free != free
                self._disk_free = free
                if free is None or free < self._disk_reserve:
                    self._disk_failures.update(self._active)
                    for process in self._processes.values():
                        self._signal_process(process, signal.SIGKILL)
                if changed:
                    self._lock.notify_all()

    def _next_job(self) -> sqlite3.Row | None:
        if self._disk_free is None or self._disk_free < self._disk_reserve + STORAGE_HEADROOM * (len(self._active) + 1):
            return None
        if not any(isinstance(job_id, int) for job_id in self._active):
            while self._browser_batch:
                job = self._browser_batch[0]
                row = self._read_job(job["id"])
                if row is not None and row["status"] == "queued":
                    return job
                self._browser_batch.popleft()
        outstanding = self._db.execute("""
            SELECT count(*) FROM remote_jobs WHERE status IN ('downloading', 'completed')
        """).fetchone()[0]
        if outstanding >= self._max_concurrent:
            return None
        return self._db.execute("SELECT * FROM remote_jobs WHERE status = 'queued' ORDER BY rowid LIMIT 1").fetchone()

    def _mark_downloading(self, job_id: int | str) -> bool:
        with self._db:
            changed = self._db.execute(f"""
                UPDATE {self._table(job_id)} SET status = 'downloading' WHERE id = ? AND status = 'queued'
            """, (job_id,)).rowcount == 1
        if changed:
            self._active.add(job_id)
        return changed

    def _work(self) -> None:
        while not self._stopped.is_set():
            try:
                with ExitStack() as active_work:
                    job = self._wait_for_job(active_work)
                    if job is None:
                        return
                    job_id = job["id"]
                    try:
                        self._run_job(job_id, job["url"],
                                      RemoteOptions(**json.loads(job["options"])) if isinstance(job_id, str) else None)
                    finally:
                        with self._lock:
                            self._active.remove(job_id)
                            self._disk_failures.discard(job_id)
                            self._lock.notify_all()
            except (OSError, sqlite3.Error):
                LOGGER.exception("Could not update or access the download queue")
                self._stopped.wait(1)

    def _wait_for_job(self, active_work: ExitStack) -> sqlite3.Row | None:
        with self._lock:
            while not self._stopping:
                job = self._next_job()
                if job is None:
                    self._lock.wait()
                    continue
                with ExitStack() as candidate_work:
                    if candidate_work.enter_context(self.work_gate.shared()):
                        if isinstance(job["id"], int):
                            self._browser_batch.popleft()
                        if self._mark_downloading(job["id"]):
                            # Transfer lease ownership to the running job.
                            active_work.enter_context(candidate_work.pop_all())
                            return job
                        continue
                # The host may release its lock without contacting this process.
                self._lock.wait(timeout=0.5)
        return None

    def _run_job(self, job_id: int | str, url: str, options: RemoteOptions | None) -> None:
        stage = self._staging / str(job_id)
        owns_stage = False
        error = None
        try:
            if stage.is_symlink():
                raise OSError("Staging directory is not owned by the job")
            if stage.is_dir():
                # A prior attempt may have been unable to clear its own temporary files.
                shutil.rmtree(stage)
            stage.mkdir(mode=0o700)
            owns_stage = True
            path, title, filename = self._download(job_id, url, stage, options)
            self._verify_media(job_id, path)
            self._publish(job_id, path, title, filename)
        except DownloadError as failure:
            error = str(failure)
        except (OSError, sqlite3.Error):
            LOGGER.exception("Could not process media for job %s", job_id)
            error = "Ошибка сервера при сохранении файла. Проверьте место и доступность хранилища."
        finally:
            if owns_stage:
                try:
                    shutil.rmtree(stage)
                except OSError:
                    LOGGER.exception("Could not clear temporary files for job %s", job_id)
        if error is not None:
            with self._lock, self._db:
                status = "failed"
                if self._stopping:
                    status, error = "interrupted", "Загрузка прервана при остановке сервера."
                elif job_id in self._disk_failures:
                    error = DISK_ERROR
                self._db.execute(f"""
                    UPDATE {self._table(job_id)} SET status = ?, progress = NULL, error = ?
                    WHERE id = ? AND status = 'downloading'
                """, (status, error, job_id))

    def _download(self, job_id: int | str, url: str, stage: Path,
                  options: RemoteOptions | None) -> tuple[Path, str, str]:
        command = [
            self._yt_dlp, "--ignore-config", "--no-plugin-dirs", "--no-cache-dir",
            "--impersonate", "chrome",
            "--no-playlist", "--playlist-items", "1", "--no-overwrites", "--no-simulate",
            "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3",
            "--encoding", "utf-8", "--no-colors", "--newline", "--progress", "--progress-delta", "0.5",
            "--progress-template", f"download:{PROGRESS_MARKER}%(progress._percent_str)s",
            "--print", f"before_dl:{TRANSFER_MARKER}",
            "--print", f"after_move:{FILE_MARKER}%(.{{filepath,title,id}})j",
            "--output", str(stage / "media.%(ext)s"),
        ]
        if options is not None:
            command.extend(("--print", f"before_dl:{REMOTE_PLAN_MARKER}%(.{{playlist_id,playlist,playlist_autonumber,n_entries}})j"))
            command.extend(options.arguments())
        command.extend(("--", url))
        outputs = []
        progress = None
        transfer_started = False
        checked_single_result = False
        last_error = ""
        with self._child(job_id, command) as process:
            for line in iter(lambda: process.stdout.readline(64 * 1024), ""):
                if line.startswith(PROGRESS_MARKER):
                    percent = re.fullmatch(r"\s*(\d{1,3})(?:\.\d+)?%\s*", line[len(PROGRESS_MARKER):])
                    if percent:
                        current = min(99, int(percent[1]))
                        if current != progress:
                            with self._lock, self._db:
                                self._db.execute(f"""
                                    UPDATE {self._table(job_id)} SET progress = ?
                                    WHERE id = ? AND status = 'downloading'
                                """, (current, job_id))
                            progress = current
                elif line.rstrip("\r\n") == TRANSFER_MARKER:
                    transfer_started = True
                elif options is not None and line.startswith(REMOTE_PLAN_MARKER):
                    try:
                        plan = json.loads(line[len(REMOTE_PLAN_MARKER):])
                    except ValueError:
                        plan = None
                    if not isinstance(plan, dict):
                        raise DownloadError("Не удалось подтвердить, что ссылка ведёт на отдельное видео.")
                    if any(plan.get(key) not in (None, "", "NA")
                           for key in ("playlist_id", "playlist", "playlist_autonumber", "n_entries")):
                        raise DownloadError(COLLECTION_ERROR)
                    checked_single_result = True
                elif line.startswith(FILE_MARKER):
                    if outputs:
                        raise DownloadError("Движок вернул больше одного файла для этой ссылки.")
                    try:
                        outputs.append(json.loads(line[len(FILE_MARKER):]))
                    except ValueError:
                        raise DownloadError("Движок вернул неверное имя файла.") from None
                elif line.startswith(("ERROR:", "yt_dlp.utils.YoutubeDLError:")):
                    last_error = line
            if process.wait() != 0:
                raise DownloadError(engine_error(last_error, transfer_started))

        if options is not None and not checked_single_result:
            raise DownloadError("Не удалось подтвердить, что ссылка ведёт на отдельное видео.")

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
        title = download_title(output.get("title"))
        if options is None:
            filename = title + path.suffix
        else:
            source_id = download_title(output.get("id") or "NA").encode("utf-16-le")[:128].decode("utf-16-le", "ignore")
            id_suffix = f" [{source_id}]"
            title_bytes = REMOTE_STEM_UTF16_BYTES - len(id_suffix.encode("utf-16-le"))
            filename_title = title.encode("utf-16-le")[:title_bytes].decode("utf-16-le", "ignore").rstrip()
            filename = f"{filename_title}{id_suffix}{path.suffix}"
        return path, title, filename

    def _verify_media(self, job_id: int | str, path: Path) -> None:
        command = [
            self._ffprobe, "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", MEDIA_FORMATS,
            "-read_intervals", "%+#32", "-show_packets", "-show_streams",
            "-show_entries", "stream=index,codec_type:stream_disposition=attached_pic:packet=stream_index",
            "-of", "json", "-i", str(path),
        ]
        try:
            with self._child(job_id, command) as process:
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

    def _sync_files_directory(self) -> None:
        directory = os.open(self._files, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)

    def _ensure_running(self, job_id: int | str) -> None:
        if self._stopping:
            raise DownloadError("Загрузка прервана при остановке сервера.")
        if job_id in self._disk_failures:
            raise DownloadError(DISK_ERROR)
        if self._read_job(job_id)["status"] != "downloading":
            raise DownloadError("Скачивание отменено.")

    def _publish(self, job_id: int | str, source: Path, title: str, filename: str) -> None:
        with source.open("rb") as media:
            size = os.fstat(media.fileno()).st_size
            os.fsync(media.fileno())
        destination = self._file_path(job_id)
        with self._lock:
            self._ensure_running(job_id)
            # A hard link publishes without replacing an existing file. The queue row gates access.
            os.link(source, destination, follow_symlinks=False)
            try:
                self._sync_files_directory()
                with self._db:
                    self._db.execute(f"""
                        UPDATE {self._table(job_id)} SET status = 'completed', title = ?, filename = ?, size = ?, progress = 100
                        WHERE id = ?
                    """, (title, filename, size, job_id))
                    if isinstance(job_id, str):
                        self._db.execute("UPDATE remote_jobs SET expires_at = ? WHERE id = ?",
                                         (time.time() + REMOTE_RESULT_TTL, job_id))
            except BaseException:
                destination.unlink(missing_ok=True)
                raise

    @staticmethod
    def _signal_process(process: subprocess.Popen[str], sig: signal.Signals) -> None:
        if process.returncode is not None:
            return
        try:
            os.killpg(process.pid, sig)
        except ProcessLookupError:
            pass

    @contextmanager
    def _child(self, job_id: int | str, command: list[str]):
        with self._lock:
            self._ensure_running(job_id)
            process = subprocess.Popen(
                command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                encoding="utf-8", errors="replace", start_new_session=True,
                env={key: os.environ[key] for key in CHILD_ENV_KEYS if key in os.environ},
            )
            self._processes[job_id] = process
        try:
            yield process
        finally:
            with self._lock:
                if process.returncode is None:
                    self._signal_process(process, signal.SIGKILL)
                del self._processes[job_id]
            process.stdout.close()
            process.wait()
