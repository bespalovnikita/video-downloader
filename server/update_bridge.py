"""Contact the host updater and coordinate its exclusive deployment lock."""

import fcntl
import json
import os
import socket
import stat
import time
from contextlib import contextmanager
from pathlib import Path


UPDATER_TIMEOUT = 1.5
MAX_SNAPSHOT_BYTES = 16 * 1024


class WorkGate:
    def __init__(self, control_directory: Path | None):
        self._lock_path = control_directory / "work.lock" if control_directory is not None else None

    @contextmanager
    def shared(self):
        if self._lock_path is None:
            yield True
            return
        try:
            descriptor = os.open(self._lock_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        except OSError:
            yield False
            return
        try:
            granted = False
            try:
                if stat.S_ISREG(os.fstat(descriptor).st_mode):
                    fcntl.flock(descriptor, fcntl.LOCK_SH | fcntl.LOCK_NB)
                    granted = True
            except OSError:
                pass
            yield granted
        finally:
            os.close(descriptor)


class UpdateBridge:
    def __init__(self, control_directory: Path | None):
        self.work_gate = WorkGate(control_directory)
        self._socket_path = control_directory / "control.sock" if control_directory is not None else None

    @property
    def supported(self):
        return self._socket_path is not None

    def request(self, action: str) -> dict:
        if self._socket_path is None or action not in {"status", "check", "install"}:
            raise ValueError("Unknown or unavailable updater action")
        response = bytearray()
        deadline = time.monotonic() + UPDATER_TIMEOUT
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(UPDATER_TIMEOUT)
            connection.connect(str(self._socket_path))
            self._set_remaining_timeout(connection, deadline)
            connection.sendall((action + "\n").encode("ascii"))
            while len(response) <= MAX_SNAPSHOT_BYTES:
                self._set_remaining_timeout(connection, deadline)
                data = connection.recv(min(4096, MAX_SNAPSHOT_BYTES + 1 - len(response)))
                if not data:
                    break
                line, separator, _ = data.partition(b"\n")
                response.extend(line)
                if separator and len(response) <= MAX_SNAPSHOT_BYTES:
                    snapshot = json.loads(response.decode("utf-8"))
                    if isinstance(snapshot, dict):
                        return snapshot
                    break
        raise ValueError("Host updater returned an invalid snapshot")

    @staticmethod
    def _set_remaining_timeout(connection: socket.socket, deadline: float):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Host updater request timed out")
        connection.settimeout(remaining)
