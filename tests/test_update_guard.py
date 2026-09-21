"""Обновление удерживает межпроцессную блокировку до проверки и отката."""
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from contextlib import contextmanager
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import updater


class UpdateGuardTest(unittest.TestCase):
    def test_busy_update_does_not_change_installation(self):
        @contextmanager
        def busy(**kwargs):
            raise OSError("busy")
            yield
        with tempfile.TemporaryDirectory() as root:
            app = pathlib.Path(root, "app")
            app.mkdir()
            (app / "main.py").write_text("old")
            previous = pathlib.Path(root, "previous")
            with mock.patch.multiple(updater, APP_DIR=str(app), PREV_DIR=str(previous)), \
                 mock.patch.object(updater.usb_monitor, "operation_guard", busy, create=True), \
                 mock.patch.object(updater.subprocess, "run", side_effect=AssertionError("external command")):
                self.assertEqual(updater._apply(root, "v1.16"), 0)
            self.assertFalse(previous.exists())
            self.assertEqual((app / "main.py").read_text(), "old")

    @unittest.skipIf(os.name == "nt", "Нужен flock Linux")
    def test_lock_covers_install_healthcheck_and_rollback(self):
        import fcntl
        with tempfile.TemporaryDirectory() as root:
            app = pathlib.Path(root, "app")
            app.mkdir()
            (app / "main.py").write_text("old")
            lock = pathlib.Path(root, "db.operations.lock")
            phases = []
            def check_locked(phase):
                with lock.open("a+") as probe:
                    with self.assertRaises(BlockingIOError):
                        fcntl.flock(probe, fcntl.LOCK_SH | fcntl.LOCK_NB)
                phases.append(phase)
            def run(command, **kwargs):
                check_locked(command[0])
                if command[0] == "bash":
                    fd = int(kwargs["env"]["ASTRA_OPERATIONS_LOCK_FD"])
                    self.assertIn(fd, kwargs["pass_fds"])
                    self.assertEqual(os.fstat(fd).st_ino, lock.stat().st_ino)
                return mock.Mock(returncode=0)
            def unhealthy():
                check_locked("health")
                return False
            with mock.patch.multiple(updater, APP_DIR=str(app), PREV_DIR=str(app)+".prev", FAILED_TAG_FILE=str(app)+".failed"), \
                 mock.patch.object(updater.usb_monitor, "DB_PATH", str(pathlib.Path(root, "db"))), \
                 mock.patch.object(updater.subprocess, "run", side_effect=run), \
                 mock.patch.object(updater.time, "sleep"), \
                 mock.patch.object(updater, "_service_healthy", side_effect=unhealthy):
                self.assertEqual(updater._apply(root, "v1.16"), 1)
            self.assertIn("health", phases)
            self.assertEqual(phases[-1], "systemctl")
            with lock.open("a+") as probe:
                fcntl.flock(probe, fcntl.LOCK_EX | fcntl.LOCK_NB)


@unittest.skipIf(os.name == "nt", "Нужен flock Linux")
class InstallerGuardTest(unittest.TestCase):
    def test_installer_refuses_busy_database_before_packages(self):
        import fcntl
        installer = pathlib.Path(__file__).resolve().parents[1] / "install_native.sh"
        prefix = installer.read_text(encoding="utf-8").split("# --- 1.", 1)[0]
        # Проверяем блокировку в root-ветке установщика, не запуск sudo из теста.
        prefix = prefix.replace('$(id -u)', '0')
        with tempfile.TemporaryDirectory() as root:
            database = pathlib.Path(root, "devices.db")
            script = pathlib.Path(root, "install.sh")
            script.write_text(prefix + '\nprintf started > "$USB_DB_PATH.started"\n')
            with open(str(database) + ".operations.lock", "a+") as lock:
                fcntl.flock(lock, fcntl.LOCK_SH | fcntl.LOCK_NB)
                result = subprocess.run(["sh", str(script)], capture_output=True,
                    env={**os.environ, "USB_DB_PATH": str(database)})
            self.assertEqual(result.returncode, 1)
            self.assertIn("станция занята".encode(), result.stdout)
            self.assertFalse(pathlib.Path(str(database) + ".started").exists())

    def test_installer_reuses_inherited_lock(self):
        import fcntl
        installer = pathlib.Path(__file__).resolve().parents[1] / "install_native.sh"
        prefix = installer.read_text(encoding="utf-8").split("# --- 1.", 1)[0]
        prefix = prefix.replace('$(id -u)', '0')
        with tempfile.TemporaryDirectory() as root:
            database = pathlib.Path(root, "devices.db")
            script = pathlib.Path(root, "install.sh")
            script.write_text(prefix + '\nprintf started > "$USB_DB_PATH.started"\n')
            with open(str(database) + ".operations.lock", "a+") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                result = subprocess.run(["sh", str(script)], capture_output=True,
                    env={**os.environ, "USB_DB_PATH": str(database),
                         "ASTRA_OPERATIONS_LOCK_FD": str(lock.fileno())},
                    pass_fds=(lock.fileno(),))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue(pathlib.Path(str(database) + ".started").exists())
