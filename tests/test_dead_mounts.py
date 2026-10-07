"""Камера отвалилась посреди работы — её монтирование не должно висеть.

На .41 с 21 сентября висели /mnt/usb_backup/sdb и sdc без устройств: umount
пропавшего носителя не прошёл, а после перезапуска службы о них никто не помнил.
"""

import contextlib
import io
import os
import queue
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um

BASE = um.MOUNT_BASE


def _umount_runner(plain_fails=True):
    calls = []

    def run(cmd, *args, **kwargs):
        calls.append(cmd)
        if cmd[:1] == ["umount"] and "-l" not in cmd and plain_fails:
            raise subprocess.CalledProcessError(32, cmd, stderr=b"target is busy")
        return subprocess.CompletedProcess(cmd, 0)
    return run, calls


class UnmountTest(unittest.TestCase):
    def setUp(self):
        um._failed_unmounts.clear()

    def _unmount(self, mounts, existing, plain_fails=True, mounted=True):
        run, calls = _umount_runner(plain_fails)
        self.out = io.StringIO()
        with mock.patch.object(um.subprocess, "run", side_effect=run), \
             mock.patch.object(um, "_iter_mounts", return_value=iter(mounts)), \
             mock.patch.object(um.os.path, "ismount", return_value=mounted), \
             mock.patch.object(um.os.path, "exists", side_effect=lambda p: p in existing), \
             mock.patch.object(um.os, "rmdir"), contextlib.redirect_stdout(self.out):
            result = um._unmount(f"{BASE}/sdb")
        return result, calls

    def test_mount_of_vanished_device_is_detached_lazily(self):
        result, calls = self._unmount([("/dev/sdb", f"{BASE}/sdb")], existing=set())
        self.assertTrue(result)
        self.assertIn(["umount", "-l", f"{BASE}/sdb"], calls)
        self.assertNotIn(f"{BASE}/sdb", um._failed_unmounts)
        self.assertIn("/dev/sdb", self.out.getvalue())
        self.assertIn("снято", self.out.getvalue())

    def test_live_device_is_never_detached_lazily(self):
        # Носитель на месте, но занят: насильно не отцепляем, извлечение запрещено.
        result, calls = self._unmount([("/dev/sdb", f"{BASE}/sdb")], existing={"/dev/sdb"})
        self.assertFalse(result)
        self.assertNotIn(["umount", "-l", f"{BASE}/sdb"], calls)
        self.assertIn(f"{BASE}/sdb", um._failed_unmounts)
        self.assertIn("Не удалось размонтировать", self.out.getvalue())

    def test_unknown_source_is_never_detached_lazily(self):
        result, calls = self._unmount([], existing=set())
        self.assertFalse(result)
        self.assertNotIn(["umount", "-l", f"{BASE}/sdb"], calls)

    def test_already_unmounted_is_success(self):
        # Точку уже сняла уборка, пока воркер ждал возврата карты.
        result, calls = self._unmount([], existing=set(), mounted=False)
        self.assertTrue(result)
        self.assertEqual(calls, [])


class CleanupDeadMountsTest(unittest.TestCase):
    def test_only_own_mounts_without_device_are_removed(self):
        mounts = [
            ("/dev/sdb", f"{BASE}/sdb"),                 # своя, устройства нет — снять
            ("/dev/sdd", f"{BASE}/sdd"),                 # своя, устройство есть — не трогать
            ("/dev/sdc", "/media/best/CAM"),             # чужая точка — не трогать
            ("/dev/sda1", "/home/best/Desktops/Desktop1/docs"),  # диск архива
            ("tmpfs", f"{BASE}/tmp"),                    # не устройство
        ]
        out = io.StringIO()
        with mock.patch.object(um, "_iter_mounts", return_value=iter(mounts)), \
             mock.patch.object(um.os.path, "exists", side_effect=lambda p: p in {"/dev/sdd", "/dev/sda1"}), \
             mock.patch.object(um, "_unmount", return_value=True) as unmount, \
             contextlib.redirect_stdout(out):
            um._cleanup_dead_mounts()
        unmount.assert_called_once_with(f"{BASE}/sdb")
        self.assertIn(f"{BASE}/sdb", out.getvalue())


class MonitorCallsCleanupTest(unittest.TestCase):
    def test_cleanup_runs_at_start_and_after_confirmed_removal(self):
        polls = iter([{"sdd": None}, {}, {}, {}, {}])
        stop = um.threading.Event()

        def poll():
            item = next(polls, None)
            if item is None:
                stop.set()
                return {}
            return item

        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
             mock.patch.object(um, "MOUNT_BASE", os.path.join(tmp, "mnt")), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "c.json")), \
             mock.patch.object(um, "_get_linux_partitions", side_effect=poll), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value=None), \
             mock.patch.object(um, "_repair_archive_ownership"), \
             mock.patch.object(um, "copy_task_linux", return_value=(None, 0, 0)), \
             mock.patch.object(um, "_cleanup_dead_mounts") as cleanup:
            um.monitor_usb(interval=0.01, stop_event=stop, progress_queue=queue.Queue())
        # Один раз при запуске и ещё раз после подтверждённого отключения sdd.
        self.assertGreaterEqual(cleanup.call_count, 2)


if __name__ == "__main__":
    unittest.main()
