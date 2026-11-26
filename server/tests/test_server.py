import base64
import http.client
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


SERVICE = Path(__file__).resolve().parents[1] / "server.py"
TEST_USER = "test-user"
TEST_PASSWORD = "test-password-for-local-integration"
AUTHORIZATION = "Basic " + base64.b64encode(f"{TEST_USER}:{TEST_PASSWORD}".encode()).decode()
HAS_MEDIA_TOOLS = all(shutil.which(tool) for tool in ("yt-dlp", "ffmpeg", "ffprobe"))


@dataclass
class Response:
    status: int
    headers: dict[str, str]
    body: bytes

    def json(self):
        return json.loads(self.body)


class Browser:
    def __init__(self, port):
        self.port = port

    def request(self, method, path, *, body=None, headers=None, authenticated=True):
        request_headers = {"X-Video-Downloader": "1"}
        if authenticated:
            request_headers["Authorization"] = AUTHORIZATION
        if body is not None:
            request_headers["Content-Type"] = "application/json"
            body = json.dumps(body).encode()
        request_headers.update(headers or {})
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=3)
        try:
            connection.request(method, path, body=body, headers=request_headers)
            response = connection.getresponse()
            return Response(
                response.status,
                {key.lower(): value for key, value in response.getheaders()},
                response.read(),
            )
        finally:
            connection.close()


class VideoSource:
    def __init__(self, data):
        self.transfer_requested = threading.Event()
        self.release_transfer = threading.Event()
        source = self

        class Handler(BaseHTTPRequestHandler):
            def media_bytes(self):
                if urlsplit(self.path).path == "/broken.mp4":
                    return b"invalid media fixture"
                return data

            def do_HEAD(self):
                self.send_response(200)
                self.send_header("Content-Type", "video/mp4")
                self.send_header("Content-Length", str(len(self.media_bytes())))
                self.end_headers()

            def do_GET(self):
                self.do_HEAD()
                if urlsplit(self.path).path == "/slow.mp4":
                    source.transfer_requested.set()
                    if not source.release_transfer.wait(15):
                        return
                try:
                    self.wfile.write(self.media_bytes())
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def log_message(self, *_):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def url(self, path):
        return f"http://127.0.0.1:{self.server.server_port}/{path}"

    def close(self):
        self.release_transfer.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)


class ServerIntegrationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="video-downloader-test-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.server_log = self.directory / "server.log"

    def start_service(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        env = dict(
            os.environ,
            VD_DATA_DIR=str(self.directory / "data"),
            VD_SERVER_HOST="127.0.0.1",
            VD_SERVER_PORT=str(port),
            VD_SERVER_USER=TEST_USER,
            VD_SERVER_PASSWORD=TEST_PASSWORD,
            NO_PROXY="127.0.0.1,localhost",
            no_proxy="127.0.0.1,localhost",
        )
        with self.server_log.open("ab") as output:
            process = subprocess.Popen(
                [sys.executable, "-u", str(SERVICE)],
                env=env,
                stdout=output,
                stderr=subprocess.STDOUT,
            )
        self.addCleanup(self.stop_service, process)
        browser = Browser(port)
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and process.poll() is None:
            try:
                if browser.request("GET", "/api/jobs").status == 200:
                    return process, browser
            except OSError:
                pass
            time.sleep(0.05)
        self.fail(f"Service did not start:\n{self.server_log.read_text(errors='replace')}")

    @staticmethod
    def stop_service(process):
        if process.poll() is not None:
            return
        process.terminate()
        try:
            process.wait(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)
            raise

    def make_video_source(self):
        fixture = self.directory / "fixture.mp4"
        subprocess.run(
            [
                "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=80x64:r=4:d=1",
                "-c:v", "mpeg4", "-movflags", "+faststart", str(fixture),
            ],
            check=True,
            timeout=12,
        )
        source = VideoSource(fixture.read_bytes())
        self.addCleanup(source.close)
        return source

    def queue(self, browser):
        response = browser.request("GET", "/api/jobs")
        self.assertEqual(response.status, 200, response.body)
        return response.json()

    def start_jobs(self, browser):
        response = browser.request("POST", "/api/start")
        self.assertLess(response.status, 300, response.body)

    def wait_for_queue(self, browser, condition, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            queue = self.queue(browser)
            if condition(queue):
                return queue
            time.sleep(0.1)
        self.fail(f"Queue did not reach expected state: {queue!r}\n{self.server_log.read_text(errors='replace')}")

    def test_browser_queue_is_shared_persistent_and_explicit(self):
        process, pc = self.start_service()
        second_browser = Browser(pc.port)
        for path in ("/", "/api/jobs", "/api/jobs/1/file"):
            with self.subTest(path=path):
                response = pc.request("GET", path, authenticated=False)
                self.assertEqual(response.status, 401)
                self.assertTrue(response.headers["www-authenticate"].startswith("Basic "))

        home = pc.request("GET", "/")
        self.assertEqual(home.status, 200)
        self.assertIn("text/html", home.headers["content-type"])
        self.assertEqual(self.queue(pc), {"jobs": [], "running": False})

        first = "https://example.com/video.mp4?key=A"
        second = "https://example.com/video.mp4?key=a"
        invalid = pc.request("POST", "/api/jobs", body={"urls": [first, "file:///movie.mp4"]})
        self.assertEqual(invalid.status, 400, invalid.body)
        invalid = pc.request("POST", "/api/jobs", body={"urls": ["https://example.com/\ud800"]})
        self.assertEqual(invalid.status, 400, invalid.body)
        invalid_header = pc.request("POST", "/api/jobs", body={"urls": [first]}, headers={"X-Video-Downloader": "0"})
        self.assertEqual(invalid_header.status, 403, invalid_header.body)
        self.assertEqual(self.queue(pc)["jobs"], [])

        result = pc.request("POST", "/api/jobs", body={"urls": [first, first, second]})
        self.assertLess(result.status, 300, result.body)
        self.assertEqual(result.json()["added"], 2)
        original = self.queue(second_browser)
        self.assertFalse(original["running"])
        self.assertEqual([(job["url"], job["status"]) for job in original["jobs"]], [
            (first, "queued"),
            (second, "queued"),
        ])
        queued_file = f"/api/jobs/{original['jobs'][0]['id']}/file"
        self.assertEqual(pc.request("GET", queued_file).status, 404)

        self.stop_service(process)
        _, reopened = self.start_service()
        self.assertEqual(self.queue(reopened), original)
        removed = reopened.request("DELETE", f"/api/jobs/{original['jobs'][0]['id']}")
        self.assertLess(removed.status, 300, removed.body)
        self.assertEqual(self.queue(reopened)["jobs"], original["jobs"][1:])

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_background_download_is_retrievable_and_supports_resume(self):
        source = self.make_video_source()
        process, pc = self.start_service()
        urls = [source.url("video.mp4"), source.url("broken.mp4")]
        added = pc.request("POST", "/api/jobs", body={"urls": urls})
        self.assertLess(added.status, 300, added.body)
        self.start_jobs(pc)

        # Every request ends its connection. The service finishes with the original client gone.
        reopened = Browser(pc.port)
        completed = self.wait_for_queue(reopened, lambda queue: not queue["running"])
        job, broken = completed["jobs"]
        self.assertEqual(job["status"], "completed", job)
        self.assertEqual(broken["status"], "failed", broken)
        self.assertEqual(reopened.request("GET", f"/api/jobs/{broken['id']}/file").status, 404)
        path = f"/api/jobs/{job['id']}/file"
        self.assertEqual(reopened.request("GET", path, authenticated=False).status, 401)

        full = reopened.request("GET", path)
        self.assertEqual(full.status, 200)
        self.assertTrue(full.headers["content-disposition"].startswith("attachment;"))
        self.assertEqual(full.headers["accept-ranges"], "bytes")
        self.assertEqual(len(full.body), job["size"])
        subprocess.run(["ffprobe", "-v", "error", "-i", "pipe:0"], input=full.body, check=True, timeout=8)
        head = reopened.request("HEAD", path)
        self.assertEqual(head.status, 200)
        self.assertEqual(head.body, b"")
        self.assertEqual(int(head.headers["content-length"]), len(full.body))

        resume = reopened.request("GET", path, headers={"Range": "bytes=16-47"})
        self.assertEqual(resume.status, 206)
        self.assertEqual(resume.headers["content-range"], f"bytes 16-47/{len(full.body)}")
        self.assertEqual(resume.body, full.body[16:48])
        suffix = reopened.request("GET", path, headers={"Range": "bytes=-23"})
        self.assertEqual(suffix.status, 206)
        self.assertEqual(suffix.body, full.body[-23:])
        beyond = reopened.request("GET", path, headers={"Range": f"bytes={len(full.body)}-"})
        self.assertEqual(beyond.status, 416)
        self.assertEqual(beyond.headers["content-range"], f"bytes */{len(full.body)}")

        self.stop_service(process)
        _, after_restart = self.start_service()
        self.assertEqual(self.queue(after_restart), completed)
        self.assertEqual(after_restart.request("GET", path).body, full.body)

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_interrupted_download_requires_retry_and_another_start(self):
        source = self.make_video_source()
        process, pc = self.start_service()
        added = pc.request("POST", "/api/jobs", body={"urls": [source.url("slow.mp4")]})
        self.assertLess(added.status, 300, added.body)
        self.start_jobs(pc)
        self.assertTrue(source.transfer_requested.wait(12), "The real downloader did not reach the video source")
        active = self.queue(pc)["jobs"][0]
        self.assertEqual(active["status"], "downloading")
        self.assertEqual(pc.request("GET", f"/api/jobs/{active['id']}/file").status, 404)

        self.stop_service(process)
        source.release_transfer.set()
        _, reopened = self.start_service()
        restored = self.queue(reopened)
        self.assertFalse(restored["running"])
        self.assertEqual(restored["jobs"][0]["status"], "interrupted")

        retry = reopened.request("POST", f"/api/jobs/{active['id']}/retry")
        self.assertLess(retry.status, 300, retry.body)
        queued = self.queue(reopened)
        self.assertFalse(queued["running"])
        self.assertEqual(queued["jobs"][0]["status"], "queued")
        self.start_jobs(reopened)
        finished = self.wait_for_queue(reopened, lambda queue: not queue["running"])
        self.assertEqual(finished["jobs"][0]["status"], "completed", finished)


if __name__ == "__main__":
    unittest.main()
