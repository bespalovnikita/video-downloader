"""Opt-in host service for installing the latest official server release."""

import argparse
import base64
import fcntl
import hashlib
import http.client
import json
import logging
import os
import re
import shutil
import socketserver
import stat
import subprocess
import tarfile
import tempfile
import threading
import time
import urllib.error
import urllib.request
from dataclasses import asdict, dataclass, replace
from pathlib import Path, PurePosixPath


LOGGER = logging.getLogger(__name__)
RELEASE_URL = "https://api.github.com/repos/bespalovnikita/video-downloader/releases/latest"
REPOSITORY_URL = "https://github.com/bespalovnikita/video-downloader.git"
CONTROL_DIR = Path("/run/video-downloader-updater")
STATE_DIR = Path("/var/lib/video-downloader-updater")
DOWNLOADER_GID = 10001
VERSION = re.compile(r"v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})")
SHA = re.compile(r"[0-9a-f]{40}")
IMAGE_ID = re.compile(r"sha256:[0-9a-f]{64}")
CONTAINER_ID = re.compile(r"[0-9a-f]{12,64}")
MAX_JSON = 1024 * 1024
MAX_SOURCE = 16 * 1024 * 1024
MAX_MEMBERS = 512
SUBPROCESS_ENV = {
    "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "LANG": "C.UTF-8",
}


class UpdateError(RuntimeError):
    """An expected failure safe to show in the web interface."""


def version_key(value):
    match = VERSION.fullmatch(value) if isinstance(value, str) else None
    if match is None:
        raise ValueError("Invalid stable version")
    return tuple(map(int, match.groups()))


def read_json(path: Path, maximum: int = MAX_JSON):
    with path.open("rb") as source:
        return load_json(source, maximum)


def load_json(source, maximum):
    data = source.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("JSON input exceeds the size limit")
    return json.loads(data)


def write_json(path: Path, value) -> None:
    with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".write-", delete=False) as output:
        temp = Path(output.name)
        try:
            output.write(json.dumps(value, ensure_ascii=False).encode("utf-8"))
            output.flush()
            os.fsync(output.fileno())
            os.replace(temp, path)
            directory = os.open(path.parent, os.O_DIRECTORY | os.O_RDONLY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
        finally:
            temp.unlink(missing_ok=True)


@dataclass(frozen=True)
class Config:
    server_dir: Path
    https_enabled: bool
    initial_version: str | None

    @classmethod
    def load(cls, path: Path):
        info = path.lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise UpdateError("Файл настроек должен принадлежать root и быть защищён от записи.")
        data = read_json(path)
        if not isinstance(data, dict) or set(data) != {"serverDirectory", "httpsEnabled", "currentVersion"}:
            raise UpdateError("Некорректный файл настроек обновления.")
        directory, https, version = data["serverDirectory"], data["httpsEnabled"], data["currentVersion"]
        if not isinstance(directory, str) or not Path(directory).is_absolute() or type(https) is not bool:
            raise UpdateError("Укажите абсолютный каталог сервера и режим HTTPS.")
        if version is not None:
            version_key(version)
        server_dir = Path(directory).resolve(strict=True)
        if not server_dir.is_dir() or not (server_dir / ".env").is_file():
            raise UpdateError("В исходном каталоге сервера не найден файл .env.")
        return cls(server_dir, https, version)


@dataclass(frozen=True)
class Deployment:
    version: str | None
    managed: bool
    image_tag: str

    @classmethod
    def load(cls, raw):
        if not isinstance(raw, dict) or set(raw) != {"version", "managed", "image_tag"}:
            raise ValueError("Invalid deployment")
        version, managed, image_tag = raw["version"], raw["managed"], raw["image_tag"]
        if type(managed) is not bool or not isinstance(image_tag, str):
            raise ValueError("Invalid deployment fields")
        if version is not None or managed:
            version_key(version)
        if managed and image_tag != version:
            raise ValueError("Invalid managed image tag")
        if not managed and image_tag != version and not re.fullmatch(r"base-[0-9a-f]{16}", image_tag):
            raise ValueError("Invalid initial image tag")
        return cls(version, managed, image_tag)


@dataclass(frozen=True)
class UpdateState:
    current: Deployment | None = None
    previous: Deployment | None = None
    pending: Deployment | None = None
    switching: bool = False
    retired: tuple[Deployment, ...] = ()

    @classmethod
    def load(cls, raw):
        if not isinstance(raw, dict) or set(raw) != {"current", "previous", "pending", "switching", "retired"}:
            raise ValueError("Invalid update state")
        current, previous, pending = [Deployment.load(raw[key]) if raw[key] is not None else None
                                      for key in ("current", "previous", "pending")]
        switching, retired = raw["switching"], raw["retired"]
        if (type(switching) is not bool or switching and pending is None or
                pending is not None and (current is None or not pending.managed) or
                not isinstance(retired, list) or len(retired) > 20):
            raise ValueError("Invalid update state fields")
        retired = tuple(map(Deployment.load, retired))
        protected = {value.image_tag for value in (current, previous, pending) if value is not None}
        protected_versions = {value.version for value in (current, previous, pending) if value is not None}
        if any(value.image_tag in protected or value.version in protected_versions for value in retired):
            raise ValueError("Invalid deployment cleanup")
        return cls(current, previous, pending, switching, retired)


def run_command(args, *, timeout, message, env=None, output=None, read_limit=None):
    # A Compose error or resolved configuration can contain .env credentials.
    # Neither is written to logs or returned to the socket client.
    with tempfile.TemporaryFile() as capture:
        target = capture if read_limit is not None else output if output is not None else subprocess.DEVNULL
        try:
            subprocess.run(args, stdin=subprocess.DEVNULL, stdout=target, stderr=subprocess.DEVNULL,
                           cwd="/", env=env or SUBPROCESS_ENV, timeout=timeout, check=True)
        except (OSError, subprocess.SubprocessError):
            raise UpdateError(message) from None
        if read_limit is not None:
            capture.seek(0)
            result = capture.read(read_limit + 1)
            if len(result) > read_limit:
                raise UpdateError(message)
            return result


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kwargs):
        return None


def read_stable_release():
    request = urllib.request.Request(RELEASE_URL, headers={
        "User-Agent": "VideoDownloader-HostUpdater/1",
        "Accept": "application/vnd.github+json",
    })
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=15) as response:
            release = load_json(response, MAX_JSON)
        if not isinstance(release, dict) or release.get("draft") is not False or release.get("prerelease") is not False:
            raise ValueError("Release is not stable")
        version, commit = release.get("tag_name"), release.get("target_commitish")
        version_key(version)
        if not isinstance(commit, str) or SHA.fullmatch(commit) is None:
            raise ValueError("Official release does not identify its commit")
        return version, commit
    except (OSError, TimeoutError, ValueError):
        raise UpdateError("Не удалось получить сведения об официальном стабильном выпуске.") from None


def extract_server(archive: Path, destination: Path) -> None:
    if archive.stat().st_size > MAX_SOURCE:
        raise UpdateError("Архив серверной части превышает допустимый размер.")
    seen = set()
    total = 0
    with tarfile.open(archive, mode="r:") as tar:
        for member in tar:
            name = PurePosixPath(member.name)
            if (name.is_absolute() or not name.parts or name.parts[0] != "server" or
                    ".." in name.parts or "\\" in member.name or name in seen or
                    ".env" in name.parts or not (member.isdir() or member.isfile())):
                raise UpdateError("Архив содержит недопустимый файл сервера.")
            seen.add(name)
            total += member.size
            if len(seen) > MAX_MEMBERS or total > MAX_SOURCE:
                raise UpdateError("Архив серверной части превышает допустимый размер.")
            target = destination.joinpath(*name.parts)
            if member.isdir():
                target.mkdir(mode=0o755, parents=True, exist_ok=True)
            else:
                target.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
                source = tar.extractfile(member)
                if source is None:
                    raise UpdateError("Файл сервера в архиве повреждён.")
                with source, target.open("xb") as output:
                    shutil.copyfileobj(source, output, 64 * 1024)
                target.chmod(0o755 if member.mode & 0o111 else 0o644)


class ReleaseSource:
    def __init__(self, state_dir: Path):
        self.state_dir = state_dir
        self.target = None

    def latest(self):
        self.target = read_stable_release()
        return self.target[0]

    @staticmethod
    def git(args, *, read_limit=None, output=None):
        environment = {**SUBPROCESS_ENV, "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
                       "GIT_TERMINAL_PROMPT": "0"}
        return run_command(["git", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                            "-c", "http.followRedirects=false", *args],
                           env=environment, timeout=120, read_limit=read_limit, output=output,
                           message="Не удалось получить исходники официального тега.")

    def stage(self, version: str) -> None:
        version_key(version)
        official = f"refs/tags/{version}"
        output = self.git(["ls-remote", "--exit-code", "--refs", REPOSITORY_URL, official], read_limit=512)
        lines = output.decode("ascii").splitlines()
        fields = lines[0].split("\t") if len(lines) == 1 else []
        if len(fields) != 2 or SHA.fullmatch(fields[0]) is None or fields[1] != official:
            raise UpdateError("GitHub не подтвердил точный тег выпуска.")
        tag_sha = fields[0]
        releases = self.state_dir / "releases"
        releases.mkdir(mode=0o700, exist_ok=True)
        destination = releases / version
        expected_commit = self.target[1] if self.target and self.target[0] == version else None
        manifest = {"tag": version, "tagObject": tag_sha}
        if destination.exists():
            cached = read_json(destination / "source.json", 512)
            commit = cached.get("commit") if isinstance(cached, dict) else None
            if (not isinstance(commit, str) or SHA.fullmatch(commit) is None or
                    cached != {**manifest, "commit": commit} or expected_commit and commit != expected_commit):
                raise UpdateError("Сохранённая версия не совпадает с официальным тегом.")
            return
        with tempfile.TemporaryDirectory(dir=releases, prefix=".stage-") as scratch:
            temporary = Path(scratch)
            repository = temporary / "repository"
            self.git(["init", "--bare", str(repository)])
            git_directory = ["--git-dir", str(repository)]
            self.git([*git_directory, "fetch", "--depth=1", "--no-tags", "--", REPOSITORY_URL, official])
            fetched = self.git([*git_directory, "rev-parse", "--verify", "FETCH_HEAD"], read_limit=128)
            if fetched.decode("ascii").strip() != tag_sha:
                raise UpdateError("Полученный Git-объект не совпадает с официальным тегом.")
            commit = self.git([*git_directory, "rev-parse", "--verify", "FETCH_HEAD^{commit}"], read_limit=128)
            commit = commit.decode("ascii").strip()
            if SHA.fullmatch(commit) is None or expected_commit and commit != expected_commit:
                raise UpdateError("Тег не совпадает с Git-коммитом официального выпуска.")
            archive = temporary / "server.tar"
            with archive.open("wb") as output:
                self.git([*git_directory, "archive", "--format=tar", commit, "server"], output=output)
            extracted = temporary / "release"
            extracted.mkdir(mode=0o700)
            extract_server(archive, extracted)
            write_json(extracted / "source.json", {**manifest, "commit": commit})
            os.rename(extracted, destination)
            directory = os.open(releases, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)

    def retire(self, deployment):
        if deployment.version is None:
            return
        destination = self.state_dir / "releases" / deployment.version
        if not destination.exists():
            return
        if destination.is_symlink():
            raise UpdateError("Не подтверждено владение сохранёнными исходниками версии.")
        manifest = read_json(destination / "source.json", 512)
        tag_object = manifest.get("tagObject") if isinstance(manifest, dict) else None
        if (not isinstance(manifest, dict) or manifest.get("tag") != deployment.version or
                not isinstance(tag_object, str) or SHA.fullmatch(tag_object) is None):
            raise UpdateError("Не подтверждено владение сохранёнными исходниками версии.")
        shutil.rmtree(destination)


class ComposeProject:
    def __init__(self, config: Config, state_dir: Path):
        self.config = config
        self.state_dir = state_dir
        self.services = ("downloader", "https") if config.https_enabled else ("downloader",)

    @staticmethod
    def image(service, tag):
        return f"video-downloader-{service}:{tag}"

    def compose(self, deployment: Deployment | None, args, *, read_limit=None, timeout=120, message):
        version = self.config.initial_version if deployment is None else deployment.version
        if version is None:
            raise UpdateError("Для обновления нужна начальная версия из официального тега.")
        root = self.state_dir / "releases" / version / "server"
        files = ["compose.yaml"]
        if self.config.https_enabled:
            files.append("compose.https.yaml")
        files.append("compose.updater.yaml")
        paths = [root / filename for filename in files]
        if not all(path.is_file() for path in paths):
            raise UpdateError("В выбранной версии отсутствует конфигурация Compose для обновления.")
        if deployment is not None:
            override = self.state_dir / f"images-{deployment.image_tag}.yaml"
            write_json(override, {"services": {service: {"image": self.image(service, deployment.image_tag)}
                                                for service in self.services}})
            paths.append(override)
        command = ["docker", "compose", "--project-name", "video-downloader",
                   "--project-directory", str(root), "--env-file", str(self.config.server_dir / ".env")]
        for path in paths:
            command.extend(("-f", str(path)))
        environment = {**SUBPROCESS_ENV, "VD_SERVER_VERSION": version or ""}
        return run_command([*command, *args], timeout=timeout, message=message,
                           env=environment, read_limit=read_limit)

    def capture_original(self):
        images = {}
        for service in self.services:
            row = self.compose(None, ["ps", "-q", service], read_limit=1024,
                               message="Не удалось прочитать текущие контейнеры.")
            container = row.decode("ascii").strip()
            if CONTAINER_ID.fullmatch(container) is None:
                raise UpdateError("Текущий контейнер сервера не найден. Переключение не начато.")
            image = run_command(["docker", "inspect", "--format", "{{.Image}}", container], timeout=15,
                                read_limit=1024, message="Не удалось прочитать текущий образ контейнера.")
            image = image.decode("ascii").strip()
            if IMAGE_ID.fullmatch(image) is None:
                raise UpdateError("Не удалось подтвердить текущий образ для отката.")
            images[service] = image
        image_hash = hashlib.sha256("\n".join(images.values()).encode("ascii")).hexdigest()[:16]
        tag = f"base-{image_hash}"
        for service, image in images.items():
            run_command(["docker", "image", "tag", image, self.image(service, tag)], timeout=30,
                        message="Не удалось сохранить образы для отката.")
        return Deployment(self.config.initial_version, False, tag)

    def build(self, deployment):
        self.compose(deployment, ["build", *self.services], timeout=1800,
                     message="Сборка серверного образа завершилась ошибкой.")

    def switch(self, deployment):
        self.compose(deployment, ["up", "--detach", "--no-build", "--pull", "never", "--force-recreate",
                                  "--wait", "--wait-timeout", "45", *self.services], timeout=120,
                     message="Не удалось запустить контейнеры выбранной версии.")

    def retire(self, deployment):
        for service in self.services:
            image = self.image(service, deployment.image_tag)
            rows = run_command(["docker", "image", "ls", "--filter", f"reference={image}",
                                "--format", "{{.Repository}}:{{.Tag}}"], timeout=15, read_limit=1024,
                               message="Не удалось проверить старый образ сервера.")
            if image.encode("ascii") in rows.splitlines():
                run_command(["docker", "image", "rm", image], timeout=45,
                            message="Не удалось удалить старый образ сервера.")
        override = self.state_dir / f"images-{deployment.image_tag}.yaml"
        override.unlink(missing_ok=True)

    def readiness(self, deployment):
        credentials = self.compose(deployment, ["config", "--format", "json"], read_limit=MAX_JSON,
                                   message="Не удалось прочитать настройки локальной проверки Compose.")
        try:
            downloader = json.loads(credentials)["services"]["downloader"]
            ports = [int(port["published"]) for port in downloader["ports"]
                     if port.get("host_ip") == "127.0.0.1" and port.get("target") == 8080]
            port, = ports
            if not 0 < port < 65536:
                raise ValueError("Invalid loopback port")
            environment = downloader["environment"]
            user, password = environment["VD_SERVER_USER"], environment["VD_SERVER_PASSWORD"]
            if not isinstance(user, str) or not isinstance(password, str) or not user or not password:
                raise ValueError("Invalid credentials")
            authorization = "Basic " + base64.b64encode(f"{user}:{password}".encode("utf-8")).decode("ascii")
        except (KeyError, TypeError, ValueError, UnicodeError):
            raise UpdateError("В Compose не подтверждены настройки локальной проверки сервера.") from None
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=2)
            try:
                connection.request("GET", "/api/update/version", headers={"Authorization": authorization})
                response = connection.getresponse()
                if response.status == 200 and load_json(response, 4096) == {"version": deployment.version or ""}:
                    return
            except (OSError, http.client.HTTPException, ValueError):
                pass
            finally:
                connection.close()
            time.sleep(0.5)
        raise UpdateError("Локальная проверка не подтвердила работу выбранной версии.")


class HostUpdater:
    def __init__(self, config, state_dir, work_lock, *, source=None, project=None):
        self.config = config
        self.state_path = state_dir / "state.json"
        self.work_lock = work_lock
        self.source = source if source is not None else ReleaseSource(state_dir)
        self.project = project if project is not None else ComposeProject(config, state_dir)
        self.lock = threading.Lock()
        self.worker = None
        self.record = UpdateState.load(read_json(self.state_path, 8192)) if self.state_path.exists() else UpdateState()
        self.latest = None
        self.state = "idle"
        self.message = ""

    def _save(self, record):
        write_json(self.state_path, asdict(record))
        with self.lock:
            self.record = record

    def snapshot(self, state=None, message=None):
        return {
            "state": self.state if state is None else state,
            "currentVersion": self.record.current.version if self.record.current else self.config.initial_version,
            "latestVersion": self.latest,
            "message": self.message if message is None else message,
        }

    def _work_lock(self):
        descriptor = os.open(self.work_lock, os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BaseException:
            os.close(descriptor)
            raise
        return descriptor

    def dispatch(self, action):
        with self.lock:
            if action == "status":
                return self.snapshot()
            if action not in {"check", "install"}:
                return self.snapshot("error", "Неизвестная команда службы обновления.")
            if self.worker is not None:
                return self.snapshot("busy", "Служба уже обрабатывает другую команду.")
            if self.record.pending is not None and action == "check":
                self._start_recovery()
                return self.snapshot()
            descriptor = None
            if action == "install":
                try:
                    descriptor = self._work_lock()
                except BlockingIOError:
                    return self.snapshot("busy", "Дождитесь завершения серверных загрузок и передач файлов.")
                except OSError:
                    return self.snapshot("error", "Не удалось заблокировать сервер для обновления.")
            self.state = "checking" if action == "check" else "installing"
            self.message = "Проверяем новые версии…" if action == "check" else "Готовим обновление сервера…"
            self.worker = threading.Thread(target=self._run, args=(action, descriptor), daemon=True)
            try:
                self.worker.start()
            except RuntimeError:
                if descriptor is not None:
                    os.close(descriptor)
                self.worker = None
                self.state, self.message = "error", "Не удалось запустить службу обновления."
            return self.snapshot()

    def resume_interrupted_update(self):
        with self.lock:
            if self.record.pending is not None or self.record.retired:
                self._start_recovery()

    def _start_recovery(self):
        self.state = "busy"
        self.message = "Ждём завершения работы очереди для восстановления после обновления."
        self.worker = threading.Thread(target=self._wait_for_recovery, daemon=True)
        try:
            self.worker.start()
        except RuntimeError:
            self.worker = None
            self.state, self.message = "error", "Не удалось запустить восстановление."

    def _wait_for_recovery(self):
        while True:
            try:
                descriptor = self._work_lock()
                break
            except BlockingIOError:
                time.sleep(0.5)
            except OSError:
                with self.lock:
                    self.state, self.message = "error", "Не удалось заблокировать сервер для восстановления."
                    self.worker = None
                return
        with self.lock:
            self.state, self.message = "installing", "Восстанавливаем сервер после прерванного обновления…"
        self._run("recover", descriptor)

    def _run(self, action, descriptor):
        outcome = ("error", "Обновление прервано.")
        try:
            if self.record.pending is not None:
                switched = self.record.switching
                self._rollback()
                message = ("Прерванное обновление отменено. Предыдущая версия восстановлена." if switched else
                           "Прерванная сборка очищена. Установленная версия доступна.")
                outcome = ("error", message)
            elif action == "recover":
                self._collect_retired()
                outcome = ("current", "Установленная версия доступна.")
            else:
                outcome = self._latest_action(action)
        except UpdateError as error:
            outcome = ("error", str(error))
        except Exception as error:
            LOGGER.error("Server update failed (%s)", type(error).__name__)
            outcome = ("error", "Не удалось выполнить операцию обновления сервера.")
        finally:
            if descriptor is not None:
                os.close(descriptor)
            with self.lock:
                self.state, self.message = outcome
                self.worker = None

    def _latest_action(self, action):
        latest = self.source.latest()
        with self.lock:
            self.latest = latest
            current = self.snapshot()["currentVersion"]
        if current is not None and version_key(latest) <= version_key(current):
            return "current", "Уже установлена актуальная версия."
        if action == "check":
            return "available", "Доступна новая стабильная версия."
        self._collect_retired()
        if self.record.retired:
            raise UpdateError("Не удалось освободить место после предыдущего обновления.")
        candidate = Deployment(latest, True, latest)
        previous = self.record.current
        if previous is None:
            if current is None:
                raise UpdateError("Для обновления нужна начальная версия из официального тега.")
            self.source.stage(current)
            previous = self.project.capture_original()
        self.project.readiness(previous)
        self._save(replace(self.record, current=previous, pending=candidate))
        try:
            self.source.stage(latest)
            self.project.build(candidate)
            self._save(replace(self.record, switching=True))
            self.project.switch(candidate)
            self.project.readiness(candidate)
            retired = (self.record.previous,) if self.record.previous else ()
            self._save(UpdateState(current=candidate, previous=previous, retired=retired))
        except Exception:
            switched = self.record.switching
            try:
                self._rollback()
            except (OSError, UpdateError):
                raise UpdateError("Обновление прервано. Требуется повторная попытка отката.") from None
            if switched:
                raise UpdateError("Новая версия не запустилась. Предыдущая версия восстановлена.") from None
            raise
        self._collect_retired()
        return "current", "Новая версия установлена и прошла проверку."

    def _rollback(self):
        if self.record.switching:
            self.project.switch(self.record.current)
            self.project.readiness(self.record.current)
        self._save(replace(self.record, pending=None, switching=False, retired=(self.record.pending,)))
        self._collect_retired()

    def _collect_retired(self):
        remaining = []
        for deployment in self.record.retired:
            try:
                self.project.retire(deployment)
                self.source.retire(deployment)
            except (OSError, UpdateError, ValueError) as error:
                LOGGER.warning("Could not remove previous updater artifacts (%s)", type(error).__name__)
                remaining.append(deployment)
        if tuple(remaining) != self.record.retired:
            try:
                self._save(replace(self.record, retired=tuple(remaining)))
            except OSError:
                LOGGER.warning("Could not save updater cleanup progress")


class SocketHandler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            deadline = time.monotonic() + 0.75
            request = bytearray()
            while b"\n" not in request and len(request) < 16:
                self.request.settimeout(max(0.01, deadline - time.monotonic()))
                chunk = self.request.recv(16 - len(request))
                if not chunk or time.monotonic() > deadline:
                    return
                request.extend(chunk)
            action = {b"status\n": "status", b"check\n": "check", b"install\n": "install"}.get(bytes(request))
            result = self.server.updater.dispatch(action)
            self.request.settimeout(0.75)
            self.request.sendall(json.dumps(result, ensure_ascii=False).encode("utf-8") + b"\n")
        except OSError:
            pass


class ControlServer(socketserver.UnixStreamServer):
    request_queue_size = 16

    def __init__(self, path, updater):
        self.updater = updater
        super().__init__(str(path), SocketHandler)


def owned_directory(path, mode, group=None):
    path.mkdir(mode=mode, exist_ok=True)
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        raise UpdateError("Каталог службы обновления должен принадлежать root и быть защищён от записи.")
    if group is not None:
        os.chown(path, 0, group)
    path.chmod(mode)


def owned_lock(path, mode, group=None):
    descriptor = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, mode)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise UpdateError("Файл блокировки должен принадлежать root и быть защищён от записи.")
        if group is not None:
            os.fchown(descriptor, 0, group)
        os.fchmod(descriptor, mode)
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", type=Path, default=Path("/etc/video-downloader-updater.json"))
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise SystemExit("Служба обновления должна работать от root.")
    os.umask(0o077)
    try:
        config = Config.load(args.config)
        owned_directory(STATE_DIR, 0o700)
        owned_directory(CONTROL_DIR, 0o750, DOWNLOADER_GID)
        service_lock = owned_lock(STATE_DIR / "daemon.lock", 0o600)
        try:
            fcntl.flock(service_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            work_lock = CONTROL_DIR / "work.lock"
            os.close(owned_lock(work_lock, 0o640, DOWNLOADER_GID))
            updater = HostUpdater(config, STATE_DIR, work_lock)
            socket_path = CONTROL_DIR / "control.sock"
            if socket_path.exists() or socket_path.is_symlink():
                info = socket_path.lstat()
                if not stat.S_ISSOCK(info.st_mode) or info.st_uid != 0:
                    raise UpdateError("Путь управляющего сокета занят другим файлом.")
                socket_path.unlink()
            with ControlServer(socket_path, updater) as server:
                os.chown(socket_path, 0, DOWNLOADER_GID)
                socket_path.chmod(0o660)
                updater.resume_interrupted_update()
                server.serve_forever()
        finally:
            os.close(service_lock)
    except (OSError, UpdateError, ValueError) as error:
        message = str(error) if isinstance(error, UpdateError) else "Не удалось запустить службу обновления."
        raise SystemExit(message) from None


if __name__ == "__main__":
    main()
