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
from types import SimpleNamespace
from unittest import mock
from urllib.parse import urlsplit


SERVICE = Path(__file__).resolve().parents[1] / "server.py"
TEST_USER = "test-user"
TEST_PASSWORD = "test-password-for-local-integration"
AUTHORIZATION = "Basic " + base64.b64encode(f"{TEST_USER}:{TEST_PASSWORD}".encode()).decode()
HAS_MEDIA_TOOLS = all(shutil.which(tool) for tool in ("yt-dlp", "ffmpeg", "ffprobe"))
PAGE_TOKEN = "private-page-fixture-token"
MEDIA_TOKEN = "private-media-fixture-token"
DENIAL_DETAIL = "Private fixture denial detail"


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
        self.paused_transfers = set()
        self.transfers = threading.Condition()
        self.client_hints = {}
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
                path = urlsplit(self.path).path
                source.client_hints[path] = self.headers.get("Sec-CH-UA", "")
                if path in {"/forbidden-page", "/forbidden-media.mp4"}:
                    self.send_error(403, DENIAL_DETAIL)
                    return
                if path == "/playable-page":
                    html = (
                        '<html><title>Video fixture</title>'
                        f'<video src="/forbidden-media.mp4?token={MEDIA_TOKEN}"></video></html>'
                    ).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(html)))
                    self.end_headers()
                    self.wfile.write(html)
                    return
                if path == "/collection":
                    html = ('<html><title>Fixture collection</title>'
                            '<video src="/first.mp4"></video><video src="/second.mp4"></video></html>').encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html")
                    self.send_header("Content-Length", str(len(html)))
                    self.end_headers()
                    self.wfile.write(html)
                    return
                self.do_HEAD()
                if path.startswith("/slow"):
                    source.transfer_requested.set()
                    with source.transfers:
                        source.paused_transfers.add(path)
                        source.transfers.notify_all()
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

    def start_service(self, **settings):
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
        env.update(settings)
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

    @staticmethod
    def remote_body(url, **options):
        return dict({"url": url, "quality": "Best", "container": "Auto", "videoCodec": "Auto",
                     "embedMetadata": False, "embedChapters": False}, **options)

    def wait_for_remote(self, browser, path, state, timeout=20):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            response = browser.request("GET", path)
            self.assertEqual(response.status, 200, response.body)
            job = response.json()
            if job["state"] == state:
                return job
            if job["state"] in {"failed", "interrupted", "received", "cancelled"}:
                break
            time.sleep(0.05)
        self.fail(f"Remote job did not reach {state}: {job!r}\n{self.server_log.read_text(errors='replace')}")

    def test_browser_queue_is_shared_persistent_and_explicit(self):
        process, pc = self.start_service()
        second_browser = Browser(pc.port)
        for path in ("/", "/status", "/api/jobs", "/api/jobs/1/file"):
            with self.subTest(path=path):
                response = pc.request("GET", path, authenticated=False)
                self.assertEqual(response.status, 401)
                self.assertTrue(response.headers["www-authenticate"].startswith("Basic "))

        home = pc.request("GET", "/")
        self.assertEqual(home.status, 200)
        self.assertIn("text/html", home.headers["content-type"])
        status = pc.request("GET", "/status")
        self.assertEqual(status.status, 200)
        self.assertEqual(status.headers["cache-control"], "no-store")
        self.assertEqual(status.body, home.body)
        self.assertIn(b'id="server-storage"', status.body)
        empty = self.queue(pc)
        self.assertEqual(empty["jobs"], [])
        self.assertFalse(empty["running"])
        self.assertGreaterEqual(empty["freeDiskBytes"], 0)
        self.assertLessEqual(empty["freeDiskBytes"], shutil.disk_usage(self.directory / "data").total)

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
        restored = self.queue(reopened)
        self.assertEqual(restored["jobs"], original["jobs"])
        self.assertEqual(restored["running"], original["running"])
        removed = reopened.request("DELETE", f"/api/jobs/{original['jobs'][0]['id']}")
        self.assertLess(removed.status, 300, removed.body)
        self.assertEqual(self.queue(reopened)["jobs"], original["jobs"][1:])

    def test_remote_auth_idempotency_and_real_free_space_gate(self):
        # Set a reserve larger than any real test volume without filling the host disk.
        process, client = self.start_service(VD_SERVER_MAX_CONCURRENT="2", VD_SERVER_FREE_RESERVE_MIB="1000000000")
        capabilities = "/api/remote/capabilities"
        self.assertEqual(client.request("GET", capabilities, authenticated=False).status, 401)
        probe = client.request("GET", capabilities).json()
        self.assertEqual(probe["version"], 1)
        self.assertEqual(probe["maxConcurrent"], 2)
        self.assertGreaterEqual(probe["freeDiskBytes"], 0)
        self.assertLessEqual(probe["freeDiskBytes"], shutil.disk_usage(self.directory / "data").total)
        path = "/api/remote/jobs/" + "a" * 32
        url = "https://example.invalid/video.mp4"
        body = self.remote_body(url, container="WEBM")
        self.assertEqual(client.request("PUT", path, body=body, authenticated=False).status, 401)
        self.assertEqual(client.request("PUT", path, body=body, headers={"X-Video-Downloader": "0"}).status, 403)
        self.assertEqual(client.request("PUT", "/api/remote/jobs/" + "A" * 32, body=body).status, 404)
        self.assertEqual(client.request("PUT", path, body=self.remote_body(url, quality=["720"])).status, 400)
        self.assertEqual(client.request("PUT", path, body=self.remote_body(url, embedChapters=1)).status, 400)
        self.assertEqual(client.request("PUT", path, body=body).json()["state"], "queued")
        self.assertEqual(client.request("PUT", path, body=dict(body, container="WebM")).json()["state"], "queued")
        self.assertEqual(client.request("PUT", path, body=dict(body, url=url + "?different=1")).status, 409)
        self.assertEqual(client.request("POST", path + "/received").status, 409)
        browser_queue = self.queue(client)
        self.assertEqual(browser_queue["jobs"], [])
        self.assertFalse(browser_queue["running"])

        second = "/api/remote/jobs/" + "b" * 32
        self.assertEqual(client.request("PUT", second, body=body).json()["state"], "queued")
        self.assertEqual(client.request("DELETE", second).status, 204)
        self.assertEqual(client.request("DELETE", second).status, 204)
        tombstone = "/api/remote/jobs/" + "c" * 32
        self.assertEqual(client.request("DELETE", tombstone).status, 204)
        self.assertEqual(client.request("PUT", tombstone, body=body).json()["state"], "cancelled")

        self.stop_service(process)
        _, reopened = self.start_service(VD_SERVER_MAX_CONCURRENT="2", VD_SERVER_FREE_RESERVE_MIB="1000000000")
        self.assertEqual(reopened.request("GET", path).json()["state"], "queued")
        self.assertEqual(reopened.request("GET", second).json()["state"], "cancelled")

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_four_delegated_transfers_and_receipt_backpressure(self):
        source = self.make_video_source()
        process, client = self.start_service()
        assignments = [f"/api/remote/jobs/{index:032x}" for index in range(1, 6)]
        urls = [source.url(f"slow-{index}.mp4") for index in range(1, 6)]
        self.assertEqual(client.request("POST", "/api/jobs", body={"urls": [urls[0]]}).json()["added"], 1)
        for assignment, url in zip(assignments, urls):
            response = client.request("PUT", assignment, body=self.remote_body(url))
            self.assertEqual(response.status, 200, response.body)
        with source.transfers:
            self.assertTrue(source.transfers.wait_for(lambda: len(source.paused_transfers) >= 4, timeout=12),
                            f"Four real downloads did not reach their source: {source.paused_transfers}")
            self.assertEqual(len(source.paused_transfers), 4)
        self.assertEqual(client.request("GET", assignments[-1]).json()["state"], "queued")
        self.assertFalse(self.queue(client)["running"])

        source.release_transfer.set()
        for assignment in assignments[:4]:
            self.wait_for_remote(client, assignment, "completed")
        self.assertEqual(client.request("GET", assignments[-1]).json()["state"], "queued")
        first = assignments[0]
        result = client.request("GET", first).json()
        self.assertTrue(result["filename"].endswith(" [slow-1].mp4"), result)
        self.assertEqual(client.request("DELETE", first).status, 204)
        self.assertEqual(client.request("GET", first).json()["state"], "completed")
        initial = client.request("GET", first + "/file", headers={"Range": "bytes=0-15"})
        self.assertEqual(initial.status, 206, initial.body)
        remaining = client.request("GET", first + "/file", headers={"Range": "bytes=16-", "If-Range": initial.headers["etag"]})
        self.assertEqual(remaining.status, 206, remaining.body)
        content = initial.body + remaining.body
        self.assertEqual(len(content), result["size"])
        result_path = self.directory / "data" / "files" / first.rsplit("/", 1)[1]
        self.assertTrue(result_path.exists())
        self.assertEqual(client.request("POST", first + "/received", authenticated=False).status, 401)
        self.assertEqual(client.request("POST", first + "/received", headers={"X-Video-Downloader": "0"}).status, 403)
        self.assertEqual(client.request("POST", first + "/received").status, 204)
        self.assertFalse(result_path.exists())
        self.assertEqual(client.request("GET", first + "/file").status, 404)
        self.assertEqual(client.request("POST", first + "/received").status, 204)
        self.assertEqual(client.request("PUT", first, body=self.remote_body(urls[0])).json()["state"], "received")
        self.wait_for_remote(client, assignments[-1], "completed")
        self.assertEqual(self.queue(client)["jobs"][0]["status"], "queued")

        # Model a crash after the receipt commit and before its physical cleanup.
        self.stop_service(process)
        result_path.write_bytes(content)
        _, reopened = self.start_service()
        self.assertEqual(reopened.request("GET", first).json()["state"], "received")
        self.assertFalse(result_path.exists())
        self.assertEqual(reopened.request("POST", first + "/received").status, 204)

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_storage_guard_covers_shared_slots_without_removing_ready_files(self):
        with mock.patch.object(sys, "path", [str(SERVICE.parent), *sys.path]):
            from queue_worker import DISK_ERROR, MIB, JobStore, RemoteOptions

        source = self.make_video_source()
        disk = SimpleNamespace(free=1024 * MIB)
        with mock.patch("queue_worker.shutil.disk_usage", return_value=disk):
            store = JobStore(self.directory / "data", max_concurrent=2, disk_reserve=64 * MIB)
            try:
                options = RemoteOptions("Best", "MP4", "H264", True, True)
                ready, active, pending = ("b" * 32, "c" * 32, "d" * 32)

                def wait_for(job_id, state):
                    deadline = time.monotonic() + 12
                    while time.monotonic() < deadline:
                        job = store.get_remote(job_id)
                        if job["state"] == state:
                            return job
                        time.sleep(0.05)
                    self.fail(f"Expected {state}, got {job!r}")

                store.put_remote(ready, source.url("direct.mp4"), options)
                wait_for(ready, "completed")
                store.add([source.url("slow-browser.mp4")])
                self.assertTrue(store.start())
                store.put_remote(active, source.url("slow-remote.mp4"), options)
                store.put_remote(pending, source.url("slow-pending.mp4"), options)
                with source.transfers:
                    self.assertTrue(source.transfers.wait_for(lambda: len(source.paused_transfers) >= 2, timeout=12))
                    self.assertEqual(source.paused_transfers, {"/slow-browser.mp4", "/slow-remote.mp4"})
                self.assertEqual(store.get_remote(pending)["state"], "queued")

                disk.free = 32 * MIB
                self.assertEqual(wait_for(active, "failed")["error"], DISK_ERROR)
                self.assertEqual(store.list_jobs()["freeDiskBytes"], 32 * MIB)
                self.assertEqual(store.remote_capabilities()["freeDiskBytes"], 32 * MIB)
                self.assertEqual(store.get_remote(pending)["state"], "queued")
                self.assertIsNotNone(store.get_file(ready))
                source.release_transfer.set()
                disk.free = 1024 * MIB
                wait_for(pending, "completed")
                self.assertEqual(store.list_jobs()["jobs"][0]["error"], DISK_ERROR)
            finally:
                store.close()

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_remote_collection_fails_without_publishing_its_first_video(self):
        source = self.make_video_source()
        _, client = self.start_service()
        path = "/api/remote/jobs/" + "d" * 32
        response = client.request("PUT", path, body=self.remote_body(source.url("collection")))
        self.assertEqual(response.status, 200, response.body)
        job = self.wait_for_remote(client, path, "failed")
        self.assertIs(job.get("unsupported"), True, job)
        self.assertEqual(client.request("GET", path + "/file").status, 404)

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_cancelling_remote_child_keeps_other_workers_alive(self):
        source = self.make_video_source()
        _, client = self.start_service()
        first, second = [f"/api/remote/jobs/{index:032x}" for index in (6, 7)]
        for path, name in ((first, "slow-6.mp4"), (second, "slow-7.mp4")):
            self.assertEqual(client.request("PUT", path, body=self.remote_body(source.url(name))).status, 200)
        with source.transfers:
            self.assertTrue(source.transfers.wait_for(lambda: len(source.paused_transfers) == 2, timeout=12))
        self.assertEqual(client.request("DELETE", first).status, 204)
        self.assertEqual(client.request("GET", first).json()["state"], "cancelled")
        self.assertEqual(client.request("GET", second).json()["state"], "downloading")
        source.release_transfer.set()
        self.wait_for_remote(client, second, "completed")
        self.assertEqual(client.request("GET", first + "/file").status, 404)

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
        restored = self.queue(after_restart)
        self.assertEqual(restored["jobs"], completed["jobs"])
        self.assertEqual(restored["running"], completed["running"])
        self.assertEqual(after_restart.request("GET", path).body, full.body)

    @unittest.skipUnless(HAS_MEDIA_TOOLS, "requires yt-dlp, ffmpeg and ffprobe on PATH")
    def test_http_403_is_reported_at_the_failed_phase_without_source_details(self):
        source = VideoSource(b"")
        self.addCleanup(source.close)
        _, pc = self.start_service()
        urls = [source.url(f"{path}?token={PAGE_TOKEN}") for path in ("forbidden-page", "playable-page")]
        added = pc.request("POST", "/api/jobs", body={"urls": urls})
        self.assertLess(added.status, 300, added.body)
        self.start_jobs(pc)

        failed = self.wait_for_queue(pc, lambda queue: not queue["running"])["jobs"]
        self.assertEqual([job["url"] for job in failed], urls)
        self.assertEqual([job["status"] for job in failed], ["failed", "failed"])
        errors = [job["error"] for job in failed]
        self.assertEqual(errors, [
            "Источник отклонил запрос данных о видео (HTTP 403).",
            "Источник отклонил запрос медиафайла (HTTP 403).",
        ])
        diagnostics = "\n".join([*errors, self.server_log.read_text(errors="replace")])
        for private_value in [PAGE_TOKEN, MEDIA_TOKEN, DENIAL_DETAIL, *urls]:
            self.assertNotIn(private_value, diagnostics)
        for path in ("/forbidden-page", "/playable-page", "/forbidden-media.mp4"):
            with self.subTest(path=path):
                self.assertIn("Chromium", source.client_hints.get(path, ""))

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
