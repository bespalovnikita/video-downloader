import fcntl
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path
from socketserver import StreamRequestHandler, UnixStreamServer
from types import SimpleNamespace
from unittest import mock

from test_server import Browser, SERVICE, TEST_PASSWORD, TEST_USER


@contextmanager
def exclusive_lock(path):
    with path.open("rb") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


class UpdaterSocket:
    def __init__(self, path):
        self.commands = []
        self.replies = {
            b"status\n": {"state": "available", "currentVersion": "v3.0.41", "latestVersion": "v3.0.42"},
            b"check\n": {"state": "checking"},
            b"install\n": {"state": "installing", "latestVersion": "v3.0.42"},
        }
        updater = self

        class Handler(StreamRequestHandler):
            def handle(self):
                command = self.rfile.readline(64)
                updater.commands.append(command)
                self.wfile.write(json.dumps(updater.replies.get(command, {})).encode() + b"\n")

        self.server = UnixStreamServer(str(path), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)


class UpdateBridgeTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="vd-update-bridge-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.control = self.directory / "control"
        self.control.mkdir()
        self.lock_path = self.control / "work.lock"
        self.lock_path.touch()
        self.lock_path.chmod(0o444)
        with mock.patch.object(sys, "path", [str(SERVICE.parent), *sys.path]):
            from queue_worker import JobStore
            from server import DownloadHandler, DownloadServer
            from update_bridge import UpdateBridge
        self.store_class = JobStore
        self.handler_class = DownloadHandler
        self.server_class = DownloadServer
        self.updater_class = UpdateBridge

    def start_browser_server(self, *, updating=True):
        updater = self.updater_class(self.control if updating else None)
        store = self.store_class(self.directory / "data", work_gate=updater.work_gate, max_concurrent=2)
        self.addCleanup(store.close)
        credentials = f"{TEST_USER}:{TEST_PASSWORD}".encode()
        server = self.server_class(("127.0.0.1", 0), store, credentials, updater=updater, version="v3.0.41")
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        thread.start()
        self.addCleanup(self.close_browser_server, server, thread)
        return store, Browser(server.server_port)

    @staticmethod
    def close_browser_server(server, thread):
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)

    def start_updater(self):
        updater = UpdaterSocket(self.control / "control.sock")
        self.addCleanup(updater.close)
        return updater

    def test_authenticated_update_actions_send_only_the_expected_lines(self):
        updater = self.start_updater()
        _, browser = self.start_browser_server()
        for method, path in (("GET", "/api/update"), ("POST", "/api/update/check"),
                             ("POST", "/api/update/install"), ("GET", "/api/update/version")):
            with self.subTest(method=method, path=path):
                self.assertEqual(browser.request(method, path, authenticated=False).status, 401)
        self.assertEqual(browser.request("POST", "/api/update/install", headers={"X-Video-Downloader": ""}).status, 403)
        self.assertEqual(browser.request("POST", "/api/update/publish").status, 404)
        self.assertEqual(updater.commands, [])

        self.assertEqual(browser.request("GET", "/api/update/version").json(), {"version": "v3.0.41"})
        status = browser.request("GET", "/api/update")
        self.assertEqual(status.status, 200)
        self.assertEqual(status.json(), {**updater.replies[b"status\n"], "supported": True})
        for action in ("check", "install"):
            with self.subTest(action=action):
                accepted = browser.request("POST", f"/api/update/{action}")
                self.assertEqual(accepted.status, 202, accepted.body)
                self.assertEqual(accepted.json(), {**updater.replies[(action + "\n").encode()], "supported": True})
        updater.replies[b"install\n"] = {"state": "busy", "detail": "Existing work is active."}
        conflict = browser.request("POST", "/api/update/install")
        self.assertEqual(conflict.status, 409, conflict.body)
        self.assertEqual(conflict.json(), {**updater.replies[b"install\n"], "supported": True})
        self.assertEqual(updater.commands, [b"status\n", b"check\n", b"install\n", b"install\n"])

    def test_unconfigured_update_status_leaves_browser_queue_available(self):
        _, browser = self.start_browser_server(updating=False)
        status = browser.request("GET", "/api/update")
        self.assertEqual(status.status, 200)
        self.assertIs(status.json()["supported"], False)
        self.assertEqual(browser.request("GET", "/api/update/version").json(), {"version": "v3.0.41"})
        self.assertEqual(browser.request("POST", "/api/jobs", body={"urls": ["https://fixture.test/video"]}).status, 201)

    def test_server_process_exposes_version_from_environment(self):
        from test_server import ServerIntegrationTests

        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        env = dict(os.environ, VD_DATA_DIR=str(self.directory / "version-data"), VD_SERVER_HOST="127.0.0.1",
                   VD_SERVER_PORT=str(port), VD_SERVER_USER=TEST_USER, VD_SERVER_PASSWORD=TEST_PASSWORD,
                   VD_SERVER_VERSION="v3.0.42", VD_UPDATER_DIR="")
        log = self.directory / "version-server.log"
        with log.open("wb") as output:
            process = subprocess.Popen([sys.executable, str(SERVICE)], env=env, stdout=output, stderr=subprocess.STDOUT)
        self.addCleanup(ServerIntegrationTests.stop_service, process)
        browser = Browser(port)

        def reads_expected_version():
            if process.poll() is not None:
                self.fail(log.read_text(errors="replace"))
            try:
                response = browser.request("GET", "/api/update/version")
            except OSError:
                return False
            self.assertEqual(response.status, 200, response.body)
            self.assertEqual(response.json(), {"version": "v3.0.42"})
            return True

        self.wait_until(reads_expected_version, "The server did not expose its configured version.")

    def test_exclusive_update_rejects_mutations_and_file_access_without_blocking_reads(self):
        self.start_updater()
        store, browser = self.start_browser_server()
        file_path = self.directory / "file.mp4"
        file_path.write_bytes(b"ready data")
        remote_id = "a" * 32
        remote = "/api/remote/jobs/" + remote_id
        body = {"url": "https://fixture.test/video", "quality": "Best", "container": "Auto",
                "videoCodec": "Auto", "embedMetadata": False, "embedChapters": False}
        with mock.patch.object(store, "get_file", return_value=(file_path, "ready.mp4")) as lookup:
            with exclusive_lock(self.lock_path):
                for method, path, payload in (
                    ("POST", "/api/jobs", {"urls": [body["url"]]}), ("POST", "/api/start", None),
                    ("POST", "/api/jobs/1/retry", None), ("DELETE", "/api/jobs/1", None),
                    ("PUT", remote, body), ("DELETE", remote, None), ("POST", remote + "/received", None),
                    ("GET", "/api/jobs/1/file", None), ("HEAD", "/api/jobs/1/file", None),
                    ("GET", remote + "/file", None), ("HEAD", remote + "/file", None),
                ):
                    with self.subTest(method=method, path=path):
                        response = browser.request(method, path, body=payload)
                        self.assertEqual(response.status, 503, response.body)
                lookup.assert_not_called()
                self.assertEqual(browser.request("GET", "/api/jobs").json()["jobs"], [])
                self.assertEqual(browser.request("GET", remote).status, 404)
                for path in ("/", "/static/app.js", "/api/update/version", "/api/update", "/api/remote/capabilities"):
                    with self.subTest(path=path):
                        self.assertEqual(browser.request("GET", path).status, 200)
            self.assertEqual(browser.request("GET", "/api/jobs/1/file").body, b"ready data")
            self.assertEqual(browser.request("HEAD", remote + "/file").status, 200)
        self.assertEqual(browser.request("POST", "/api/jobs", body={"urls": [body["url"]]}).status, 201)

    def test_active_browser_and_remote_runners_hold_shared_lock_until_finished(self):
        store, browser = self.start_browser_server()
        browser_started = threading.Event()
        remote_started = threading.Event()
        browser_release = threading.Event()
        remote_release = threading.Event()

        def held_job(job_id, url, options):
            if isinstance(job_id, int):
                browser_started.set()
                browser_release.wait(8)
            else:
                remote_started.set()
                remote_release.wait(8)

        with mock.patch.object(store, "_run_job", side_effect=held_job):
            try:
                with exclusive_lock(self.lock_path):
                    self.assertEqual(browser.request("GET", "/api/jobs").json()["jobs"], [])
                self.assertEqual(browser.request("POST", "/api/jobs", body={"urls": ["https://fixture.test/browser"]}).status, 201)
                self.assertEqual(browser.request("POST", "/api/start").status, 202)
                request = {"url": "https://fixture.test/delegated", "quality": "Best", "container": "Auto",
                           "videoCodec": "Auto", "embedMetadata": False, "embedChapters": False}
                self.assertEqual(browser.request("PUT", "/api/remote/jobs/" + "b" * 32, body=request).status, 200)
                self.assertTrue(browser_started.wait(5), "Browser job was not claimed.")
                self.assertTrue(remote_started.wait(5), "Remote job was not claimed.")
                with self.assertRaises(BlockingIOError), exclusive_lock(self.lock_path):
                    self.fail("Update acquired the lock while jobs were active.")
                browser_release.set()
                self.wait_until(lambda: not browser.request("GET", "/api/jobs").json()["running"])
                with self.assertRaises(BlockingIOError), exclusive_lock(self.lock_path):
                    self.fail("Update acquired the lock before the last remote job finished.")
            finally:
                browser_release.set()
                remote_release.set()
            self.wait_until(self.lock_available)

    def test_queued_runner_retries_its_claim_after_host_releases_update_lock(self):
        # Keep the storage monitor quiet so unlocking alone must eventually
        # wake the waiting worker, without unrelated free-space notifications.
        with mock.patch("queue_worker.shutil.disk_usage", return_value=SimpleNamespace(free=10 * 1024 ** 3)):
            store, _ = self.start_browser_server()
            started = threading.Event()
            with mock.patch.object(store, "_run_job", side_effect=lambda *_: started.set()) as runner:
                with exclusive_lock(self.lock_path):
                    store.add(["https://fixture.test/accepted-before-update"])
                    self.assertTrue(store.start())
                    self.assertFalse(started.wait(0.75), "A queued job started during the exclusive update.")
                    self.assertEqual(store.list_jobs()["jobs"][0]["status"], "queued")
                self.assertTrue(started.wait(2), "The worker waited for another request after the lock was released.")
                runner.assert_called_once_with(1, "https://fixture.test/accepted-before-update", None)
                self.wait_until(self.lock_available)

    def test_streaming_holds_shared_lock_until_the_file_response_finishes(self):
        store, browser = self.start_browser_server()
        file_path = self.directory / "file.mp4"
        file_path.write_bytes(b"ready data")
        started = threading.Event()
        release = threading.Event()
        stream_file = self.handler_class.stream_file

        def held_stream(handler, stream, info, filename):
            started.set()
            release.wait(5)
            return stream_file(handler, stream, info, filename)

        with (mock.patch.object(store, "get_file", return_value=(file_path, "ready.mp4")),
              mock.patch.object(self.handler_class, "stream_file", held_stream),
              ThreadPoolExecutor(max_workers=1) as pool):
            future = pool.submit(browser.request, "GET", "/api/jobs/1/file", headers={"Range": "bytes=1-3"})
            try:
                self.assertTrue(started.wait(2), "The file response did not enter its stream.")
                with self.assertRaises(BlockingIOError), exclusive_lock(self.lock_path):
                    self.fail("Update acquired the lock while a file was streaming.")
            finally:
                release.set()
            response = future.result(timeout=4)
            self.assertEqual(response.status, 206, response.body)
            self.assertEqual(response.body, b"ead")
            self.wait_until(self.lock_available)

    def lock_available(self):
        try:
            with exclusive_lock(self.lock_path):
                return True
        except BlockingIOError:
            return False

    def wait_until(self, condition, message="The updater lock did not release after active work finished."):
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if condition():
                return
            time.sleep(0.02)
        self.fail(message)
