import fcntl
import io
import json
import os
import socket
import sys
import tarfile
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from host_updater import (  # noqa: E402
    Config, ComposeProject, ControlServer, Deployment, HostUpdater, RELEASE_URL, REPOSITORY_URL,
    ReleaseSource, UpdateError, UpdateState, extract_server, read_json, read_stable_release, write_json,
)


class Source:
    def __init__(self):
        self.version = "v3.0.10"
        self.staged = []
        self.retired = []

    def latest(self):
        return self.version

    def stage(self, version):
        self.staged.append(version)

    def retire(self, deployment):
        self.retired.append(deployment)


class Project:
    def __init__(self):
        self.running = Deployment("v3.0.9", False, "base-0123456789abcdef")
        self.operations = []
        self.failed = set()
        self.building = threading.Event()
        self.finish_build = threading.Event()
        self.finish_build.set()

    def capture_original(self):
        self.operations.append(("snapshot", self.running))
        return self.running

    def readiness(self, deployment):
        self.operations.append(("readiness", deployment))
        if deployment != self.running or deployment.version in self.failed:
            raise UpdateError("failed readiness")

    def build(self, deployment):
        self.operations.append(("build", deployment))
        self.building.set()
        if not self.finish_build.wait(5):
            raise UpdateError("fixture build timeout")

    def switch(self, deployment):
        self.operations.append(("switch", deployment))
        self.running = deployment

    def retire(self, deployment):
        if deployment == self.running:
            raise AssertionError("Retired a running deployment")
        self.operations.append(("retire", deployment))


class HostUpdaterTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="vd-updater-test-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.lock = self.directory / "work.lock"
        self.lock.touch()
        self.origin = self.directory / "server"
        self.origin.mkdir()
        (self.origin / ".env").touch()
        self.config = Config(self.origin, True, "v3.0.9")
        self.source, self.project = Source(), Project()

    def updater(self):
        return HostUpdater(self.config, self.directory, self.lock, source=self.source, project=self.project)

    def wait_idle(self, updater):
        for _attempt in range(100):
            value = updater.dispatch("status")
            if value["state"] not in {"busy", "checking", "installing"}:
                return value
            time.sleep(0.02)
        self.fail(f"Updater did not settle: {value!r}")

    def test_official_release_lookup_rejects_prerelease_and_malformed_tag(self):
        for release, accepted in (
            ({"tag_name": "v3.0.10", "draft": False, "prerelease": False}, True),
            ({"tag_name": "v3.0.10", "draft": True, "prerelease": False}, False),
            ({"tag_name": "v3.0.10", "draft": False, "prerelease": True}, False),
            ({"tag_name": "v3.0.10-rc1", "draft": False, "prerelease": False}, False),
            ({"tag_name": "refs/tags/v3.0.10", "draft": False, "prerelease": False}, False),
        ):
            with self.subTest(release=release):
                with mock.patch("host_updater.urllib.request.build_opener") as opener:
                    release = {**release, "target_commitish": "a" * 40}
                    opener.return_value.open.return_value = io.BytesIO(json.dumps(release).encode())
                    if accepted:
                        self.assertEqual(read_stable_release(), ("v3.0.10", "a" * 40))
                    else:
                        with self.assertRaises(UpdateError):
                            read_stable_release()
                    self.assertEqual(opener.return_value.open.call_args.args[0].full_url, RELEASE_URL)

    def test_stage_verifies_exact_official_ref_and_archive_scope(self):
        source = ReleaseSource(self.directory)
        archive, tag, commit = io.BytesIO(), "1" * 40, "2" * 40
        with tarfile.open(fileobj=archive, mode="w") as tar:
            body = b"services: {}"
            member = tarfile.TarInfo("server/compose.yaml")
            member.size = len(body)
            tar.addfile(member, io.BytesIO(body))
        official_ref = "refs/tags/v3.0.10"

        def git(arguments, *, read_limit=None, output=None):
            if arguments[0] == "ls-remote":
                self.assertEqual(arguments[3:], [REPOSITORY_URL, official_ref])
                return f"{tag}\t{official_ref}\n".encode()
            if "fetch" in arguments:
                self.assertEqual(arguments[3:], ["--depth=1", "--no-tags", "--", REPOSITORY_URL, official_ref])
            if "rev-parse" in arguments:
                return (commit if arguments[-1] == "FETCH_HEAD^{commit}" else tag).encode()
            if "archive" in arguments:
                self.assertEqual(arguments[-2:], [commit, "server"])
                output.write(archive.getvalue())
            return None

        with mock.patch.object(source, "git", side_effect=git):
            source.target = "v3.0.10", commit
            source.stage("v3.0.10")
        release = self.directory / "releases" / "v3.0.10"
        self.assertEqual((release / "server" / "compose.yaml").read_bytes(), b"services: {}")
        self.assertEqual(read_json(release / "source.json"), {"tag": "v3.0.10", "tagObject": tag, "commit": commit})

        def changed_git(arguments, **options):
            if "rev-parse" in arguments:
                return b"4" * 40
            return git(arguments, **options)

        # Reject a tag moved between its official lookup and fetching.
        source.retire(Deployment("v3.0.10", True, "v3.0.10"))
        with mock.patch.object(source, "git", side_effect=changed_git), self.assertRaises(UpdateError):
            source.stage("v3.0.10")
        self.assertFalse(release.exists())

        with mock.patch.object(source, "git", side_effect=git), self.assertRaises(UpdateError):
            source.target = "v3.0.10", "5" * 40
            source.stage("v3.0.10")
        self.assertFalse(release.exists())

        for filename, filetype in (("server/../escaped", tarfile.REGTYPE), ("server/.env", tarfile.REGTYPE),
                                   ("server/link", tarfile.SYMTYPE), ("server/link", tarfile.LNKTYPE)):
            with self.subTest(filename=filename, filetype=filetype):
                fixture = self.directory / "unsafe.tar"
                with tarfile.open(fixture, mode="w") as tar:
                    member = tarfile.TarInfo(filename)
                    member.type = filetype
                    member.linkname = "/opt/server/.env"
                    tar.addfile(member)
                with self.assertRaises(UpdateError):
                    extract_server(fixture, self.directory / "extract")
        self.assertFalse((self.directory / "escaped").exists())

    def test_install_socket_is_asynchronous_and_does_not_cross_work_gate(self):
        updater = self.updater()
        socket_path = self.directory / "control.sock"
        server = ControlServer(socket_path, updater)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        def request(command):
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(1)
                client.connect(str(socket_path))
                client.sendall(command)
                with client.makefile("rb") as response:
                    return json.loads(response.readline())

        try:
            self.assertEqual(request(b"status\n")["state"], "idle")
            self.assertEqual(request(b"install v3.5\n")["state"], "error")
            with self.lock.open("rb") as active:
                fcntl.flock(active, fcntl.LOCK_SH)
                self.assertEqual(request(b"install\n")["state"], "busy")
                self.assertEqual(request(b"check\n")["state"], "checking")
                self.assertEqual(self.wait_idle(updater)["state"], "available")
                self.assertEqual(self.project.operations, [])
            self.project.finish_build.clear()
            self.assertEqual(request(b"install\n")["state"], "installing")
            self.assertTrue(self.project.building.wait(2))
            self.assertEqual(request(b"status\n")["state"], "installing")
            self.assertEqual(request(b"check\n")["state"], "busy")
            with self.lock.open("rb") as active, self.assertRaises(BlockingIOError):
                fcntl.flock(active, fcntl.LOCK_SH | fcntl.LOCK_NB)
            self.project.finish_build.set()
            self.assertEqual(self.wait_idle(updater)["currentVersion"], "v3.0.10")
        finally:
            self.project.finish_build.set()
            server.shutdown()
            server.server_close()
            thread.join()

    def test_success_retains_one_previous_version_and_refuses_downgrade(self):
        updater = self.updater()
        original = self.project.running
        updater.dispatch("install")
        self.assertEqual(self.wait_idle(updater)["currentVersion"], "v3.0.10")
        first = self.project.running
        self.source.version = "v3.0.11"
        updater.dispatch("install")
        self.assertEqual(self.wait_idle(updater)["currentVersion"], "v3.0.11")
        self.assertEqual(updater.record.previous, first)
        self.assertIn(("retire", original), self.project.operations)
        self.assertNotIn(("retire", first), self.project.operations)
        self.assertEqual(self.source.retired, [original])

        restored = self.updater()
        self.source.version = "v3.0.9"
        restored.dispatch("install")
        self.assertEqual(self.wait_idle(restored)["currentVersion"], "v3.0.11")
        self.assertEqual(self.source.staged, ["v3.0.9", "v3.0.10", "v3.0.11"])
        self.assertEqual(restored.record.previous, first)

    def test_failed_readiness_rolls_back_instead_of_advancing_current(self):
        updater = self.updater()
        original = self.project.running
        self.project.failed.add("v3.0.10")
        updater.dispatch("install")
        result = self.wait_idle(updater)
        self.assertEqual(result["state"], "error")
        self.assertEqual(result["currentVersion"], "v3.0.9")
        self.assertEqual(self.project.running, original)
        self.assertIsNone(updater.record.pending)
        self.assertIn(("retire", Deployment("v3.0.10", True, "v3.0.10")), self.project.operations)

    def test_restart_waits_for_active_transfers_and_restores_pending_switch(self):
        original = self.project.running
        candidate = Deployment("v3.0.10", True, "v3.0.10")
        self.project.running = candidate
        state = UpdateState(current=original, pending=candidate, switching=True)
        write_json(self.directory / "state.json", {
            "current": original.__dict__, "previous": None, "pending": candidate.__dict__,
            "switching": state.switching, "retired": [],
        })
        with self.lock.open("rb") as active:
            fcntl.flock(active, fcntl.LOCK_SH)
            restored = self.updater()
            restored.resume_interrupted_update()
            self.assertEqual(restored.dispatch("status")["state"], "busy")
            self.assertEqual(restored.dispatch("install")["state"], "busy")
            self.assertEqual(self.project.running, candidate)
            self.assertEqual(self.project.operations, [])
        self.assertEqual(self.wait_idle(restored)["currentVersion"], "v3.0.9")
        self.assertEqual(self.project.running, original)
        self.assertIsNone(restored.record.pending)

    def test_compose_uses_original_env_and_mode_with_private_image_overrides(self):
        deployment = Deployment("v3.0.10", True, "v3.0.10")
        revision = self.directory / "releases" / deployment.version / "server"
        revision.mkdir(parents=True)
        for name in ("compose.yaml", "compose.https.yaml", "compose.updater.yaml"):
            (revision / name).touch()
        with mock.patch("host_updater.run_command") as run:
            ComposeProject(self.config, self.directory).switch(deployment)
        command = run.call_args.args[0]
        self.assertEqual(command[:8], ["docker", "compose", "--project-name", "video-downloader",
                                      "--project-directory", str(revision), "--env-file", str(self.origin / ".env")])
        self.assertIn(str(revision / "compose.https.yaml"), command)
        self.assertIn("--no-build", command)
        self.assertNotIn("--remove-orphans", command)
        self.assertEqual(command[-2:], ["downloader", "https"])
        self.assertEqual(run.call_args.kwargs["env"]["VD_SERVER_VERSION"], deployment.version)
        override = self.directory / "images-v3.0.10.yaml"
        self.assertEqual(read_json(override), {"services": {
            "downloader": {"image": "video-downloader-downloader:v3.0.10"},
            "https": {"image": "video-downloader-https:v3.0.10"},
        }})
        initial = self.directory / "releases" / "v3.0.9" / "server"
        initial.mkdir(parents=True)
        for name in ("compose.yaml", "compose.https.yaml", "compose.updater.yaml"):
            (initial / name).touch()
        with mock.patch("host_updater.run_command") as run:
            project = ComposeProject(self.config, self.directory)
            project.compose(None, ["ps", "-q", "downloader"], read_limit=1024, message="test")
            capture = run.call_args.args[0]
            project.switch(self.project.running)
            rollback = run.call_args.args[0]
        for command in (capture, rollback):
            self.assertEqual(command[4:8], ["--project-directory", str(initial),
                                           "--env-file", str(self.origin / ".env")])


if __name__ == "__main__":
    unittest.main()
