"""Integration-style tests for the copy_task orchestration layer: device
resolution + scan + copy + DB persistence wired together, and the thin
platform-specific wrappers (copy_task_linux/_windows, _make_submit_fn)."""

import queue
import json
import os
import sys
import sqlite3
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class CopyTaskEndToEndTest(unittest.TestCase):
    @staticmethod
    def _id_log(src, device_id=1234567):
        os.makedirs(os.path.join(src, "LOG"), exist_ok=True)
        with open(os.path.join(src, "LOG", "boot.txt"), "wb") as out:
            out.write(f"#ID:{device_id}\n".encode("ascii"))

    @mock.patch.object(um, "_require_archive_device", new=lambda device: None)
    def _run(self, src, dest, progress_queue=None, label="MYUSB", serial="SERIAL123"):
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value=label), \
             mock.patch.object(um, "_get_device_serial_linux", return_value=serial), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(dest, "no_config.json")), \
             mock.patch.object(um, "get_dest_base", return_value=dest), \
             mock.patch.object(um, "_repair_archive_ownership"):
            return um.copy_task(src, src, "sda1", None, None, progress_queue=progress_queue)

    def test_copies_files_and_persists_device_and_backup_rows(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "photo.jpg"), "wb") as f:
                f.write(b"abc")
            self._id_log(src)
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                device_id, copied_files, copied_bytes = self._run(src, dest)

                conn = sqlite3.connect(db_path)
                try:
                    row = conn.execute(
                        "SELECT serial, label, id_source FROM devices WHERE id=?", (device_id,)).fetchone()
                    self.assertEqual(row, ("SERIAL123", "MYUSB", "device"))
                    backup = conn.execute(
                        "SELECT total_files, total_bytes FROM backups WHERE device_id=?",
                        (device_id,)).fetchone()
                    self.assertEqual(backup, (2, 15))
                finally:
                    conn.close()

            self.assertEqual(device_id, 1234567)
            self.assertEqual(copied_files, 2)
            self.assertEqual(copied_bytes, 15)
            self.assertTrue(os.path.exists(os.path.join(dest, "Device1234567", "photo.jpg")))
            self.assertFalse(os.path.exists(os.path.join(src, ".astra_id")))

    def test_empty_drive_without_id_is_refused_without_backup_row(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                device_id, copied_files, copied_bytes = self._run(src, dest)

                conn = sqlite3.connect(db_path)
                try:
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 0)
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 0)
                finally:
                    conn.close()

            self.assertEqual((device_id, copied_files, copied_bytes), (None, 0, 0))

    def test_emits_progress_states_in_order(self):
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "video.mp4"), "wb") as f:
                f.write(b"x" * 10)
            self._id_log(src)
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                self._run(src, dest, progress_queue=pq)

        states = []
        while not pq.empty():
            states.append(pq.get_nowait()[2])
        self.assertEqual(states[:2], ["identifying", "scanning"])
        self.assertEqual(states[-1], "done")
        self.assertIn("copying", states)

    def test_operator_stop_is_not_reported_as_done(self):
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            self._id_log(src)
            for index in range(3):
                with open(os.path.join(src, f"clip{index}.mp4"), "wb") as out:
                    out.write(b"recording")
            original_copy = um._copy_archive_file

            def copy_then_stop(source, target):
                original_copy(source, target)
                um.set_safe_removal(True)

            try:
                with mock.patch.object(um, "DB_PATH", os.path.join(data_dir, "d.db")), \
                     mock.patch.object(um, "_copy_archive_file", copy_then_stop):
                    um._init_db().close()
                    self._run(src, dest, progress_queue=pq)
            finally:
                um.set_safe_removal(False)
            events = list(pq.queue)
            self.assertEqual(events[-1][2], "stopped")
            self.assertNotIn("done", [event[2] for event in events])
            self.assertIn("Копирование остановлено", events[-1][5])
            self.assertLess(events[-1][3], events[-1][4])
            for index in range(3):
                self.assertTrue(os.path.exists(os.path.join(src, f"clip{index}.mp4")))

    def test_card_replaced_during_copy_keeps_source_videos(self):
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            self._id_log(src)
            clip = os.path.join(src, "clip.mp4")
            with open(clip, "wb") as out:
                out.write(b"recording")
            original_copy = um._copy_archive_file

            def copy_then_swap(source, target):
                original_copy(source, target)
                # Карту подменили посреди копирования: у новой другой ID.
                self._id_log(src, device_id=7654321)

            with mock.patch.object(um, "DB_PATH", os.path.join(data_dir, "d.db")), \
                 mock.patch.object(um, "_copy_archive_file", copy_then_swap):
                um._init_db().close()
                self._run(src, dest, progress_queue=pq)

            states = [event[2] for event in pq.queue]
            self.assertNotIn("done", states)
            self.assertEqual(states[-1], "error")
            self.assertTrue(os.path.exists(clip), "исходное видео не должно удаляться")

    def test_backup_row_failure_is_reported(self):
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            self._id_log(src)
            with open(os.path.join(src, "photo.jpg"), "wb") as f:
                f.write(b"abc")
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                conn = sqlite3.connect(db_path)
                conn.execute("DROP TABLE backups")
                conn.commit()
                conn.close()
                self._run(src, dest, progress_queue=pq)

            last = list(pq.queue)[-1]
            self.assertEqual(last[2], "error")
            self.assertIn("не записана в базу", last[5])

    def test_awaited_card_flag_is_cleared_by_saved_uuid(self):
        """К концу копирования имя устройства могло исчезнуть: отметку
        «ждём возврата» снимаем по метке, запомненной до копирования."""
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            self._id_log(src)
            with open(os.path.join(src, "photo.jpg"), "wb") as f:
                f.write(b"abc")
            calls = {"n": 0}

            def uuid_until_gone(devpath):
                calls["n"] += 1
                return "CARD-UUID" if calls["n"] <= 2 else None

            um._await_card_register("CARD-UUID", 120)
            try:
                with mock.patch.object(um, "DB_PATH", os.path.join(data_dir, "d.db")), \
                     mock.patch.object(um, "_get_filesystem_uuid", side_effect=uuid_until_gone):
                    um._init_db().close()
                    self._run(src, dest)
                self.assertFalse(um._card_is_awaited("CARD-UUID"))
            finally:
                um._await_card_clear("CARD-UUID")

    def test_queue_carries_name_when_set_otherwise_id(self):
        import queue
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "video.mp4"), "wb") as f:
                f.write(b"x" * 10)
            self._id_log(src)
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                pq1 = queue.Queue()
                device_id, _, _ = self._run(src, dest, progress_queue=pq1)
                labels1 = [m[1] for m in list(pq1.queue) if m[0] == device_id]
                self.assertTrue(labels1)
                for label in labels1:
                    self.assertEqual(label, str(device_id))

                conn = sqlite3.connect(db_path)
                try:
                    conn.execute("UPDATE devices SET name = ? WHERE id = ?", ("Kiosk-1", device_id))
                    conn.commit()
                finally:
                    conn.close()

                with open(os.path.join(src, "clip2.mp4"), "wb") as f:
                    f.write(b"y" * 10)
                pq2 = queue.Queue()
                device_id2, _, _ = self._run(src, dest, progress_queue=pq2)
                self.assertEqual(device_id2, device_id)
                labels2 = [m[1] for m in list(pq2.queue) if m[0] == device_id]
                self.assertTrue(labels2)
                for label in labels2:
                    self.assertEqual(label, "Kiosk-1")

    def test_queue_reflects_rename_during_copy(self):
        """Переименование посреди копирования не должно затираться следующим
        событием прогресса: подпись перечитывается при каждом _emit."""
        import queue
        calls = {"n": 0}

        def _fake_name(conn, device_id):
            calls["n"] += 1
            return "" if calls["n"] == 1 else "Kiosk-1"

        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            with open(os.path.join(src, "video.mp4"), "wb") as f:
                f.write(b"x" * 10)
            self._id_log(src)
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "_get_device_name", side_effect=_fake_name):
                um._init_db().close()
                pq = queue.Queue()
                device_id, _, _ = self._run(src, dest, progress_queue=pq)
                labels = [m[1] for m in list(pq.queue) if m[0] == device_id]
                self.assertTrue(labels)
                for label in labels:
                    self.assertEqual(label, "Kiosk-1")

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
            self._id_log(src)

            db_path = os.path.join(data_dir, "d.db")
            self._id_log(src)
            with mock.patch.object(um.platform, "system", return_value="Linux"), \
                 mock.patch.object(um, "DB_PATH", db_path), \
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
            self.assertEqual(states, ["identifying", "error"])

    @mock.patch.object(um, "_require_archive_device", new=lambda device: None)
    def test_should_unmount_triggers_unmount(self):
        with tempfile.TemporaryDirectory() as src, \
             tempfile.TemporaryDirectory() as dest, \
             tempfile.TemporaryDirectory() as data_dir:
            self._id_log(src)
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
             mock.patch.object(um, "_wait_for_system_mount", return_value=None), \
             mock.patch.object(um, "_mount_device", return_value="/mnt/usb_backup/sda1") as mount_mock, \
             mock.patch.object(um, "copy_task", return_value=(1, 2, 3)) as copy_mock:
            result = um.copy_task_linux("sda1", None, None, None)
        self.assertEqual(result, (1, 2, 3))
        mount_mock.assert_called_once_with("sda1")
        args, _kwargs = copy_mock.call_args
        self.assertEqual(args[1], "/mnt/usb_backup/sda1")  # mountpoint
        self.assertEqual(args[2], "sda1")  # devname
        self.assertTrue(args[5])  # should_unmount: we created the mount

    def test_reuses_system_mount_without_self_mounting_or_unmount(self):
        """When the desktop auto-mounter already holds the device, reuse that
        mount and never create a second one — the double RW mount is exactly
        what corrupts FAT sticks. We must also not unmount someone else's mount."""
        sys_mp = "/run/user/1000/media/A3"
        with mock.patch.object(um.os.path, "ismount", return_value=False), \
             mock.patch.object(um, "_wait_for_system_mount", return_value=sys_mp), \
             mock.patch.object(um, "_mount_device") as mount_mock, \
             mock.patch.object(um, "_is_dest_path", return_value=False), \
             mock.patch.object(um, "copy_task", return_value=(1, 2, 3)) as copy_mock:
            result = um.copy_task_linux("sdc1", None, None, None)
        self.assertEqual(result, (1, 2, 3))
        mount_mock.assert_not_called()
        args, _kwargs = copy_mock.call_args
        self.assertEqual(args[1], sys_mp)  # mountpoint: the system's own
        self.assertFalse(args[5])  # should_unmount False: not ours to tear down

    def test_mount_failure_is_shown_and_retried(self):
        # Регистратор отдаёт USB-диск раньше карты: первое монтирование падает
        # с «не найден носитель». Устройство нельзя молча забыть до переподключения.
        q = queue.Queue()
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um.os.path, "ismount", return_value=False), \
             mock.patch.object(um, "_wait_for_system_mount", return_value=None), \
             mock.patch.object(um, "_mount_device", return_value=None), \
             mock.patch.object(um, "_interrupted_devices", set()) as interrupted:
            run = ThreadPoolExecutor(max_workers=1)
            try:
                um._make_submit_fn(q)(run, "sdd", None, None, None).result()
            finally:
                run.shutdown()
            self.assertIn("sdd", interrupted)
        key, _label, state, *_rest = q.get_nowait()
        self.assertEqual((key, state), ("identity:sdd", "error"))

    def test_already_mounted_path_used_directly_without_unmount_flag(self):
        with mock.patch.object(um.os.path, "ismount", return_value=True), \
             mock.patch.object(um, "copy_task", return_value=(5, 6, 7)) as copy_mock:
            result = um.copy_task_linux("sda1", "/media/system/sda1", None, None)
        self.assertEqual(result, (5, 6, 7))
        args, _kwargs = copy_mock.call_args
        self.assertFalse(args[5])  # should_unmount False: system already owns the mount

    @mock.patch.object(um, "_require_archive_device", new=lambda device: None)
    def test_destination_drive_is_skipped_and_kept_mounted(self):
        """Regression: the drive hosting the backup destination used to be
        treated as a source — backed up and then unmounted, after which every
        real backup silently went into a shadow directory on the root FS."""
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as mnt:
            dest = os.path.join(mnt, "backups")
            with mock.patch.object(um.os.path, "ismount", return_value=False), \
                 mock.patch.object(um, "_wait_for_system_mount", return_value=None), \
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

    @mock.patch.object(um, "_require_archive_device", new=lambda device: None)
    def test_destination_drive_heals_marker_on_connect(self):
        with tempfile.TemporaryDirectory() as mnt:
            dest = os.path.join(mnt, "backups")
            os.makedirs(dest)
            cfg_path = os.path.join(mnt, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({"backup_dest": dest}, f)
            with mock.patch.object(um.os.path, "ismount", return_value=False), \
                 mock.patch.object(um, "_wait_for_system_mount", return_value=None), \
                 mock.patch.object(um, "_mount_device", return_value=mnt), \
                 mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "copy_task") as copy_mock:
                um.copy_task_linux("sdb1", None, None, None)
            copy_mock.assert_not_called()
            self.assertTrue(os.path.isfile(os.path.join(dest, um.DEST_MARKER_FILE)),
                            "reconnecting the dest drive must (re)stamp the marker")

    @mock.patch.object(um, "_require_archive_device", new=lambda device: None)
    @mock.patch.object(um.platform, "system", new=lambda: "Linux")
    def test_destination_drive_is_recognised_after_mountpoint_change(self):
        """Native mode may mount the configured destination under our own
        /mnt/usb_backup path after a reboot/replug. The same disk must still
        be skipped as destination rather than backed up as a source."""
        import queue
        pq = queue.Queue()
        with tempfile.TemporaryDirectory() as mnt, \
             tempfile.TemporaryDirectory() as cfgdir:
            dest = os.path.join(mnt, "backups")
            os.makedirs(dest)
            cfg_path = os.path.join(cfgdir, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({
                    "backup_dest": "/run/user/1000/media/OLD/backups",
                    "backup_mount_relpath": "backups",
                    "backup_fs_uuid": "UUID-DEST",
                }, f)
            with mock.patch.object(um.os.path, "ismount", return_value=False), \
                 mock.patch.object(um, "_wait_for_system_mount", return_value=None), \
                 mock.patch.object(um, "_mount_device", return_value=mnt), \
                 mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "_iter_mounts", return_value=[("/dev/sdb1", mnt)]), \
                 mock.patch.object(um, "_find_mount_for_path", return_value=("/dev/sdb1", mnt)), \
                 mock.patch.object(um, "_get_filesystem_uuid", return_value="UUID-DEST"), \
                 mock.patch.object(um, "copy_task") as copy_mock:
                result = um.copy_task_linux("sdb1", None, None, None, progress_queue=pq)
                self.assertEqual(result, (0, 0, 0))
                copy_mock.assert_not_called()
                self.assertTrue(os.path.isfile(os.path.join(dest, um.DEST_MARKER_FILE)))
                self.assertEqual(pq.get_nowait()[0], "_status_")


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
    def test_selects_platform_worker(self):
        from concurrent.futures import ThreadPoolExecutor
        for system, name in (("Windows", "copy_task_windows"), ("Linux", "copy_task_linux")):
            with self.subTest(system=system), mock.patch.object(um, "_safe_removal", False), \
                 mock.patch.object(um.platform, "system", return_value=system), \
                 mock.patch.object(um, name, return_value=(7, 1, 10)):
                with ThreadPoolExecutor(max_workers=1) as executor:
                    future = um._make_submit_fn()(executor, "source", None, None, None)
                    self.assertEqual(future.result(timeout=3), (7, 1, 10))


if __name__ == "__main__":
    unittest.main(verbosity=2)
