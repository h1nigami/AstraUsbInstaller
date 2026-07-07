"""Integration-style tests for the copy_task orchestration layer: device
resolution + scan + copy + DB persistence wired together, and the thin
platform-specific wrappers (copy_task_linux/_windows, _make_submit_fn)."""

import json
import os
import sys
import sqlite3
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class CopyTaskEndToEndTest(unittest.TestCase):
    def _run(self, src, dest, progress_queue=None, label="MYUSB", serial="SERIAL123"):
        with mock.patch.object(um, "_get_drive_label_linux", return_value=label), \
             mock.patch.object(um, "_get_device_serial_linux", return_value=serial), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(dest, "no_config.json")), \
             mock.patch.object(um, "get_dest_base", return_value=dest):
            return um.copy_task(src, src, "sda1", None, None, progress_queue=progress_queue)

    def test_copies_files_and_persists_device_and_backup_rows(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "photo.jpg"), "wb") as f:
                f.write(b"abc")
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                device_id, copied_files, copied_bytes = self._run(src, dest)

                conn = sqlite3.connect(db_path)
                try:
                    row = conn.execute(
                        "SELECT serial, label FROM devices WHERE id=?", (device_id,)).fetchone()
                    self.assertEqual(row, ("SERIAL123", "MYUSB"))
                    backup = conn.execute(
                        "SELECT total_files, total_bytes FROM backups WHERE device_id=?",
                        (device_id,)).fetchone()
                    self.assertEqual(backup, (1, 3))
                finally:
                    conn.close()

            self.assertEqual(copied_files, 1)
            self.assertEqual(copied_bytes, 3)
            self.assertTrue(os.path.exists(os.path.join(dest, "Device%s" % device_id, "photo.jpg")))

    def test_empty_drive_skips_backup_row_but_still_registers_device(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                device_id, copied_files, copied_bytes = self._run(src, dest)

                conn = sqlite3.connect(db_path)
                try:
                    device_row = conn.execute(
                        "SELECT id FROM devices WHERE id=?", (device_id,)).fetchone()
                    self.assertIsNotNone(device_row)
                    backup_row = conn.execute(
                        "SELECT 1 FROM backups WHERE device_id=?", (device_id,)).fetchone()
                    self.assertIsNone(backup_row, "empty drive must not create a backup record")
                finally:
                    conn.close()

            self.assertEqual((copied_files, copied_bytes), (0, 0))

    def test_emits_progress_states_in_order(self):
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "video.mp4"), "wb") as f:
                f.write(b"x" * 10)
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                self._run(src, dest, progress_queue=pq)

        states = []
        while not pq.empty():
            states.append(pq.get_nowait()[2])
        self.assertEqual(states[0], "scanning")
        self.assertEqual(states[-1], "done")
        self.assertIn("copying", states)

    def test_refuses_when_configured_dest_has_no_marker(self):
        """Regression: destination disk unmounted -> its path is a plain shadow
        directory without the marker. copy_task must fail loudly (error state),
        write nothing there and never auto-delete the source videos."""
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as cfgdir, \
             tempfile.TemporaryDirectory() as data_dir:
            shadow_dest = os.path.join(cfgdir, "unmounted_disk")
            os.makedirs(shadow_dest)  # exists, but no marker => not the real disk
            cfg_path = os.path.join(cfgdir, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({"backup_dest": shadow_dest}, f)

            video = os.path.join(src, "clip.mp4")
            with open(video, "wb") as f:
                f.write(b"x" * 10)

            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "_get_drive_label_linux", return_value="L"), \
                 mock.patch.object(um, "_get_device_serial_linux", return_value="S"):
                um._init_db().close()
                device_id, copied_files, copied_bytes = um.copy_task(
                    src, src, "sda1", None, None, progress_queue=pq)

                conn = sqlite3.connect(db_path)
                try:
                    backup_row = conn.execute(
                        "SELECT 1 FROM backups WHERE device_id=?", (device_id,)).fetchone()
                finally:
                    conn.close()

            self.assertEqual((copied_files, copied_bytes), (0, 0))
            self.assertTrue(os.path.exists(video), "source video must never be deleted")
            self.assertEqual(os.listdir(shadow_dest), [],
                             "nothing may be written into the shadow directory")
            self.assertIsNone(backup_row, "a refused backup must not be recorded")

            states = []
            while not pq.empty():
                states.append(pq.get_nowait()[2])
            self.assertEqual(states, ["error"])

    def test_should_unmount_triggers_unmount(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "_get_drive_label_linux", return_value="L"), \
                 mock.patch.object(um, "_get_device_serial_linux", return_value="S"), \
                 mock.patch.object(um, "_CONFIG_PATH", os.path.join(dest, "no_config.json")), \
                 mock.patch.object(um, "get_dest_base", return_value=dest), \
                 mock.patch.object(um, "_unmount") as unmount_mock:
                um._init_db().close()
                um.copy_task(src, src, "sda1", None, None, should_unmount=True)
            unmount_mock.assert_called_once_with(src)


class CopyTaskLinuxWrapperTest(unittest.TestCase):
    def test_mounts_when_not_already_mounted(self):
        with mock.patch.object(um.os.path, "ismount", return_value=False), \
             mock.patch.object(um, "_mount_device", return_value="/mnt/usb_backup/sda1") as mount_mock, \
             mock.patch.object(um, "copy_task", return_value=(1, 2, 3)) as copy_mock:
            result = um.copy_task_linux("sda1", None, None, None)
        self.assertEqual(result, (1, 2, 3))
        mount_mock.assert_called_once_with("sda1")
        args, _kwargs = copy_mock.call_args
        self.assertEqual(args[1], "/mnt/usb_backup/sda1")  # mountpoint
        self.assertEqual(args[2], "sda1")  # devname
        self.assertTrue(args[5])  # should_unmount

    def test_mount_failure_returns_zero_tuple(self):
        with mock.patch.object(um.os.path, "ismount", return_value=False), \
             mock.patch.object(um, "_mount_device", return_value=None):
            result = um.copy_task_linux("sda1", None, None, None)
        self.assertEqual(result, (0, 0, 0))

    def test_already_mounted_path_used_directly_without_unmount_flag(self):
        with mock.patch.object(um.os.path, "ismount", return_value=True), \
             mock.patch.object(um, "copy_task", return_value=(5, 6, 7)) as copy_mock:
            result = um.copy_task_linux("sda1", "/mnt/usb_backup/sda1", None, None)
        self.assertEqual(result, (5, 6, 7))
        args, _kwargs = copy_mock.call_args
        self.assertFalse(args[5])  # should_unmount False: system already owns the mount

    def test_destination_drive_is_skipped_and_kept_mounted(self):
        """Regression: the drive hosting the backup destination used to be
        treated as a source — backed up and then unmounted, after which every
        real backup silently went into a shadow directory on the root FS."""
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as mnt:
            dest = os.path.join(mnt, "backups")
            with mock.patch.object(um.os.path, "ismount", return_value=False), \
                 mock.patch.object(um, "_mount_device", return_value=mnt), \
                 mock.patch.object(um, "get_dest_base", return_value=dest), \
                 mock.patch.object(um, "_CONFIG_PATH", os.path.join(mnt, "no_config.json")), \
                 mock.patch.object(um, "_unmount") as unmount_mock, \
                 mock.patch.object(um, "copy_task") as copy_mock:
                result = um.copy_task_linux("sdb1", None, None, None, progress_queue=pq)
        self.assertEqual(result, (0, 0, 0))
        copy_mock.assert_not_called()
        unmount_mock.assert_not_called()
        status = pq.get_nowait()
        self.assertEqual(status[0], "_status_")

    def test_destination_drive_heals_marker_on_connect(self):
        with tempfile.TemporaryDirectory() as mnt:
            dest = os.path.join(mnt, "backups")
            os.makedirs(dest)
            cfg_path = os.path.join(mnt, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({"backup_dest": dest}, f)
            with mock.patch.object(um.os.path, "ismount", return_value=False), \
                 mock.patch.object(um, "_mount_device", return_value=mnt), \
                 mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "copy_task") as copy_mock:
                um.copy_task_linux("sdb1", None, None, None)
            copy_mock.assert_not_called()
            self.assertTrue(os.path.isfile(os.path.join(dest, um.DEST_MARKER_FILE)),
                            "reconnecting the dest drive must (re)stamp the marker")


class CopyTaskWindowsWrapperTest(unittest.TestCase):
    def test_builds_drive_path_and_delegates(self):
        with mock.patch.object(um, "copy_task", return_value=(1, 1, 1)) as copy_mock:
            result = um.copy_task_windows("E", None, None)
        self.assertEqual(result, (1, 1, 1))
        args, _kwargs = copy_mock.call_args
        self.assertEqual(args[0], "E:\\")
        self.assertEqual(args[1], "E:\\")
        self.assertEqual(args[2], "E")


class MakeSubmitFnTest(unittest.TestCase):
    class _FakeExecutor:
        def __init__(self):
            self.calls = []

        def submit(self, fn, *args):
            self.calls.append((fn, args))
            return "future"

    def test_selects_windows_impl_on_windows(self):
        executor = self._FakeExecutor()
        with mock.patch.object(um.platform, "system", return_value="Windows"):
            submit = um._make_submit_fn(None)
            submit(executor, "E", None, None, None)
        self.assertIs(executor.calls[0][0], um.copy_task_windows)

    def test_selects_linux_impl_off_windows(self):
        executor = self._FakeExecutor()
        with mock.patch.object(um.platform, "system", return_value="Linux"):
            submit = um._make_submit_fn(None)
            submit(executor, "sda1", None, None, None)
        self.assertIs(executor.calls[0][0], um.copy_task_linux)


if __name__ == "__main__":
    unittest.main(verbosity=2)
