import base64
import hmac
import json
import logging
import os
import re
import signal
import stat
from contextlib import contextmanager
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import quote, urlsplit

from queue_worker import JobStore, MIB, RemoteOptions
from update_bridge import UpdateBridge


STATIC_ROOT = Path(__file__).resolve().parent / "static"
STATIC_FILES = {
    "/": ("index.html", "text/html; charset=utf-8"),
    "/status": ("index.html", "text/html; charset=utf-8"),
    "/static/app.js": ("app.js", "text/javascript; charset=utf-8"),
    "/static/styles.css": ("styles.css", "text/css; charset=utf-8"),
}
JOB_PATH = re.compile(r"/api/jobs/([1-9][0-9]{0,18})(/retry|/file)?")
REMOTE_JOB_PATH = re.compile(r"/api/remote/jobs/([0-9a-f]{32})(/file|/received)?")
BYTE_RANGE = re.compile(r"bytes=([0-9]{0,19})-([0-9]{0,19})", re.IGNORECASE)
MAX_REQUEST_BYTES = 2 * 1024 * 1024
MAX_URLS = 100
MAX_URL_LENGTH = 4096


class RequestError(Exception):
    def __init__(self, status, detail):
        super().__init__(detail)
        self.status = status
        self.detail = detail


def valid_url(url):
    if not isinstance(url, str) or not 1 <= len(url) <= MAX_URL_LENGTH:
        return False
    if any(char.isspace() or ord(char) < 32 or ord(char) == 127 for char in url):
        return False
    if "\\" in url:
        return False
    try:
        url.encode("utf-8")
        parts = urlsplit(url)
        return (
            parts.scheme in {"http", "https"}
            and bool(parts.hostname)
            and parts.username is None
            and parts.password is None
            and (parts.port is None or parts.port > 0)
            and "%" not in parts.hostname
        )
    except ValueError:
        return False


def attachment_header(filename):
    fallback = re.sub(r"[^A-Za-z0-9._ -]", "_", filename).strip(" .")
    return f"attachment; filename=\"{fallback or 'video'}\"; filename*=UTF-8''{quote(filename, safe='')}"


def file_range(value, size):
    match = BYTE_RANGE.fullmatch(value)
    if match is None or size == 0:
        return None
    start_text, end_text = match.groups()
    if not start_text:
        suffix = int(end_text or "0")
        return (max(0, size - suffix), size - 1) if suffix else None
    start = int(start_text)
    end = min(int(end_text), size - 1) if end_text else size - 1
    return (start, end) if start <= end else None


class DownloadHandler(BaseHTTPRequestHandler):
    server_version = "VideoDownloader"
    sys_version = ""

    def setup(self):
        self.request.settimeout(15)
        super().setup()

    def log_message(self, format, *args):
        # The default access log can include private URLs or authorization input.
        pass

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'none'; script-src 'self'; style-src 'self'; "
            "img-src 'self'; connect-src 'self'; base-uri 'none'; "
            "frame-ancestors 'none'; form-action 'self'",
        )
        super().end_headers()

    def do_GET(self):
        self.handle_request()

    def do_HEAD(self):
        self.handle_request()

    def do_POST(self):
        self.handle_request()

    def do_PUT(self):
        self.handle_request()

    def do_DELETE(self):
        self.handle_request()

    def handle_request(self):
        try:
            if not self.authorized():
                self.send_json(
                    HTTPStatus.UNAUTHORIZED,
                    {"detail": "Войдите, чтобы открыть очередь."},
                    headers={"WWW-Authenticate": 'Basic realm="Video Downloader", charset="UTF-8"'},
                )
                return
            try:
                if self.command in {"POST", "PUT", "DELETE"} and self.headers.get("X-Video-Downloader") != "1":
                    raise RequestError(HTTPStatus.FORBIDDEN, "Запрос на изменение отклонён.")
                self.dispatch()
            except RequestError as error:
                self.send_json(error.status, {"detail": error.detail})
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            return

    def authorized(self):
        scheme, _, encoded = self.headers.get("Authorization", "").partition(" ")
        if scheme.lower() != "basic" or len(encoded) > 4096:
            return False
        try:
            credentials = base64.b64decode(encoded, validate=True)
        except ValueError:
            return False
        return hmac.compare_digest(credentials, self.server.credentials)

    def dispatch(self):
        path = self.path.split("?", 1)[0]
        method = self.command
        if path == "/api/update" or path.startswith("/api/update/"):
            self.dispatch_update(method, path)
            return
        if method in {"POST", "PUT", "DELETE"}:
            with self.queue_work():
                self.dispatch_queue(method, path)
        else:
            self.dispatch_queue(method, path)

    @contextmanager
    def queue_work(self):
        with self.server.store.work_gate.shared() as granted:
            if not granted:
                raise RequestError(HTTPStatus.SERVICE_UNAVAILABLE,
                                   "Очередь временно недоступна во время обновления сервера.")
            yield

    def dispatch_update(self, method, path):
        if method in {"GET", "HEAD"} and path == "/api/update/version":
            self.send_json(HTTPStatus.OK, {"version": self.server.version})
            return
        if method in {"GET", "HEAD"} and path == "/api/update":
            action = "status"
        elif method == "POST" and path in {"/api/update/check", "/api/update/install"}:
            action = path.rsplit("/", 1)[1]
        else:
            raise RequestError(HTTPStatus.NOT_FOUND, "Страница не найдена.")
        updater = self.server.updater
        if not updater.supported:
            if action == "status":
                self.send_json(HTTPStatus.OK, {"supported": False})
                return
            raise RequestError(HTTPStatus.SERVICE_UNAVAILABLE, "Обновление через браузер не настроено.")
        try:
            snapshot = updater.request(action)
        except (OSError, ValueError, RecursionError):
            raise RequestError(HTTPStatus.SERVICE_UNAVAILABLE, "Служба обновлений сейчас недоступна.") from None
        if action == "status":
            status = HTTPStatus.OK
        elif snapshot.get("state") == "busy":
            status = HTTPStatus.CONFLICT
        else:
            status = HTTPStatus.ACCEPTED
        self.send_json(status, {**snapshot, "supported": True})

    def dispatch_queue(self, method, path):
        store = self.server.store
        if path == "/api/remote/capabilities" and method in {"GET", "HEAD"}:
            self.send_json(HTTPStatus.OK, store.remote_capabilities())
            return
        remote = REMOTE_JOB_PATH.fullmatch(path)
        if remote is not None:
            self.dispatch_remote_job(method, remote[1], remote[2])
            return
        if method in {"GET", "HEAD"}:
            if path in STATIC_FILES:
                filename, content_type = STATIC_FILES[path]
                self.send_bytes(HTTPStatus.OK, (STATIC_ROOT / filename).read_bytes(), content_type)
                return
            if path == "/api/jobs":
                self.send_json(HTTPStatus.OK, store.list_jobs())
                return
        if method == "POST":
            if path == "/api/jobs":
                self.send_json(HTTPStatus.CREATED, {"added": store.add(self.read_urls())})
                return
            if path == "/api/start":
                if not store.start():
                    raise RequestError(HTTPStatus.CONFLICT, "Загрузка уже запущена или очередь пуста.")
                self.send_json(HTTPStatus.ACCEPTED, {"running": True})
                return
        match = JOB_PATH.fullmatch(path)
        if match is not None:
            job_id = int(match[1])
            if job_id <= 2**63 - 1:
                self.dispatch_job(method, job_id, match[2])
                return
        raise RequestError(HTTPStatus.NOT_FOUND, "Страница не найдена.")

    def dispatch_remote_job(self, method, job_id, suffix):
        store = self.server.store
        if method in {"GET", "HEAD"} and suffix == "/file":
            self.send_file(job_id)
            return
        if method in {"GET", "HEAD"} and suffix is None:
            job = store.get_remote(job_id)
            if job is None:
                raise RequestError(HTTPStatus.NOT_FOUND, "Задание сервера не найдено.")
            self.send_json(HTTPStatus.OK, job)
            return
        if method == "PUT" and suffix is None:
            url, options = self.read_remote_options()
            job = store.put_remote(job_id, url, options)
            if job is None:
                raise RequestError(HTTPStatus.CONFLICT, "Этот идентификатор уже принадлежит другому заданию.")
            self.send_json(HTTPStatus.OK, job)
            return
        if method == "DELETE" and suffix is None:
            store.cancel_remote(job_id)
            self.send_empty(HTTPStatus.NO_CONTENT)
            return
        if method == "POST" and suffix == "/received":
            try:
                if not store.receive_remote(job_id):
                    raise RequestError(HTTPStatus.CONFLICT, "Для этого задания ещё нет готового файла.")
            except OSError:
                logging.exception("Could not remove acknowledged result for job %s", job_id)
                raise RequestError(HTTPStatus.INTERNAL_SERVER_ERROR, "Не удалось удалить подтверждённый файл.") from None
            self.send_empty(HTTPStatus.NO_CONTENT)
            return
        raise RequestError(HTTPStatus.NOT_FOUND, "Страница не найдена.")

    def dispatch_job(self, method, job_id, suffix):
        store = self.server.store
        if method in {"GET", "HEAD"} and suffix == "/file":
            self.send_file(job_id)
            return
        if method == "POST" and suffix == "/retry":
            if not store.retry(job_id):
                raise RequestError(HTTPStatus.CONFLICT, "Повтор доступен для прерванного задания или задания с ошибкой.")
            self.send_json(HTTPStatus.OK, {"updated": True})
            return
        if method == "DELETE" and suffix is None:
            if not store.delete(job_id):
                raise RequestError(HTTPStatus.CONFLICT, "Это задание нельзя удалить из очереди.")
            self.send_json(HTTPStatus.OK, {"deleted": True})
            return
        raise RequestError(HTTPStatus.NOT_FOUND, "Страница не найдена.")

    def read_json(self):
        if self.headers.get_content_type() != "application/json":
            raise RequestError(HTTPStatus.UNSUPPORTED_MEDIA_TYPE, "Ожидается запрос в формате JSON.")
        lengths = self.headers.get_all("Content-Length") or []
        if self.headers.get("Transfer-Encoding") or len(lengths) != 1:
            raise RequestError(HTTPStatus.BAD_REQUEST, "Нужна корректная длина запроса.")
        length = lengths[0]
        if not re.fullmatch(r"[0-9]{1,10}", length):
            raise RequestError(HTTPStatus.BAD_REQUEST, "Некорректная длина запроса.")
        length = int(length)
        if length > MAX_REQUEST_BYTES:
            raise RequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "Запрос слишком большой.")
        body = self.rfile.read(length)
        if len(body) != length:
            raise RequestError(HTTPStatus.BAD_REQUEST, "Запрос получен не полностью.")
        try:
            return json.loads(body.decode("utf-8"))
        except (ValueError, RecursionError):
            raise RequestError(HTTPStatus.BAD_REQUEST, "Некорректный JSON.") from None

    def read_urls(self):
        data = self.read_json()
        urls = data.get("urls") if isinstance(data, dict) else None
        if not isinstance(urls, list) or not 1 <= len(urls) <= MAX_URLS:
            raise RequestError(HTTPStatus.BAD_REQUEST, f"Добавьте от 1 до {MAX_URLS} ссылок.")
        if not all(valid_url(url) for url in urls):
            raise RequestError(HTTPStatus.BAD_REQUEST, "Нужны ссылки HTTP или HTTPS без логина и пароля в адресе.")
        return urls

    def read_remote_options(self):
        data = self.read_json()
        if not isinstance(data, dict) or not valid_url(data.get("url")):
            raise RequestError(HTTPStatus.BAD_REQUEST, "Нужна ссылка HTTP или HTTPS без логина и пароля в адресе.")

        def choice(name, supported):
            value = data.get(name)
            if isinstance(value, str):
                for option in supported:
                    if value.casefold() == option.casefold():
                        return option
            raise RequestError(HTTPStatus.BAD_REQUEST, "Параметры задания сервером не поддерживаются.")

        metadata, chapters = data.get("embedMetadata"), data.get("embedChapters")
        if not isinstance(metadata, bool) or not isinstance(chapters, bool):
            raise RequestError(HTTPStatus.BAD_REQUEST, "Параметры задания сервером не поддерживаются.")
        return data["url"], RemoteOptions(choice("quality", ("Best", "2160", "1440", "1080", "720")),
                                          choice("container", ("Auto", "MP4", "MKV", "WebM")),
                                          choice("videoCodec", ("Auto", "H264", "VP9", "AV1")),
                                          metadata, chapters)

    def send_empty(self, status):
        self.send_response(status)
        self.end_headers()

    def send_json(self, status, data, headers=None):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_bytes(status, body, "application/json; charset=utf-8", headers)

    def send_bytes(self, status, data, content_type, headers=None):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def send_file(self, job_id):
        with self.queue_work():
            self._send_file(job_id)

    def _send_file(self, job_id):
        result = self.server.store.get_file(job_id)
        if result is None:
            raise RequestError(HTTPStatus.NOT_FOUND, "Готовый файл не найден.")
        path, filename = result
        try:
            descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        except OSError:
            raise RequestError(HTTPStatus.NOT_FOUND, "Готовый файл не найден.") from None
        with os.fdopen(descriptor, "rb") as stream:
            info = os.fstat(stream.fileno())
            if not stat.S_ISREG(info.st_mode):
                raise RequestError(HTTPStatus.NOT_FOUND, "Готовый файл не найден.")
            self.stream_file(stream, info, filename)

    def stream_file(self, stream, info, filename):
        size = info.st_size
        etag = f'"{size:x}-{info.st_mtime_ns:x}"'
        requested_range = self.headers.get("Range")
        if_range = self.headers.get("If-Range")
        bounds = None
        if requested_range and (if_range is None or if_range == etag):
            bounds = file_range(requested_range, size)
            if bounds is None:
                self.send_json(
                    HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE,
                    {"detail": "Диапазон байтов недоступен."},
                    headers={"Content-Range": f"bytes */{size}"},
                )
                return
        start, end = bounds if bounds is not None else (0, size - 1)
        length = end - start + 1
        self.send_response(HTTPStatus.PARTIAL_CONTENT if bounds is not None else HTTPStatus.OK)
        if bounds is not None:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Disposition", attachment_header(filename))
        self.send_header("Content-Length", str(length))
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("ETag", etag)
        self.end_headers()
        if self.command != "HEAD" and length:
            self.connection.sendfile(stream, offset=start, count=length)


class DownloadServer(ThreadingHTTPServer):
    def __init__(self, address, store, credentials, *, updater=None, version=""):
        self.store = store
        self.credentials = credentials
        self.updater = updater if updater is not None else UpdateBridge(None)
        self.version = version
        super().__init__(address, DownloadHandler)


def stop_server(_signal, _frame):
    raise KeyboardInterrupt


def main():
    user = os.environ.get("VD_SERVER_USER", "downloader")
    password = os.environ.get("VD_SERVER_PASSWORD", "")
    credentials = f"{user}:{password}".encode("utf-8")
    if not user or ":" in user or not password or len(credentials) > 3072:
        raise SystemExit("Укажите VD_SERVER_USER и VD_SERVER_PASSWORD для входа.")
    if any(char < 32 or char == 127 for char in credentials):
        raise SystemExit("Логин и пароль не должны содержать управляющие символы.")
    data_dir = Path(os.environ.get("VD_DATA_DIR", "data")).resolve()
    host = os.environ.get("VD_SERVER_HOST", "127.0.0.1")
    port = int(os.environ.get("VD_SERVER_PORT", "8080"))
    try:
        max_concurrent = int(os.environ.get("VD_SERVER_MAX_CONCURRENT", "4"))
        disk_reserve = int(os.environ.get("VD_SERVER_FREE_RESERVE_MIB", "512"))
    except ValueError:
        raise SystemExit("Лимит загрузок и резерв диска должны быть целыми числами.") from None
    if not 1 <= max_concurrent <= 4 or disk_reserve < 64:
        raise SystemExit("Укажите от 1 до 4 загрузок и не менее 64 МиБ резерва диска.")
    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")
    signal.signal(signal.SIGTERM, stop_server)
    os.umask(0o077)
    updater_dir = os.environ.get("VD_UPDATER_DIR")
    updater = UpdateBridge(Path(updater_dir) if updater_dir else None)
    store = JobStore(data_dir, max_concurrent=max_concurrent, disk_reserve=disk_reserve * MIB,
                     work_gate=updater.work_gate)
    try:
        with DownloadServer((host, port), store, credentials, updater=updater,
                            version=os.environ.get("VD_SERVER_VERSION", "")) as server:
            logging.info("Video Downloader listening on %s:%d", *server.server_address)
            server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        store.close()


if __name__ == "__main__":
    main()
