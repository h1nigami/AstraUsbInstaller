"""Раздельная регистрация камер с одинаковыми заводскими серийниками."""

import os
import queue
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest import mock

import usb_monitor as um


class SharedCameraIdentityTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        patcher = mock.patch.object(um, "DB_PATH", os.path.join(self.tmp.name, "devices.db"))
        patcher.start()
        self.addCleanup(patcher.stop)
        um._init_db().close()

    def mount(self, number, marker=None):
        path = os.path.join(self.tmp.name, str(number))
        os.mkdir(path)
        if marker is not None:
            um._write_device_id_to_usb(path, marker)
        return path

    def resolve(self, mount, devname):
        conn = um._connect()
        try:
            return um._resolve_device_id(conn, mount, "SAME_FACTORY_SERIAL", "CAM", devname)
        finally:
            conn.close()

    def test_ten_new_cameras_register_concurrently_and_keep_ids(self):
        self.check_ten_cameras(marker=None)

    def test_ten_duplicate_markers_are_split_and_keep_ids(self):
        self.check_ten_cameras(marker=123456)

    def test_fast_reconnect_under_new_device_name_keeps_id(self):
        mount = self.mount(1)
        um._update_connected_devices({"sdb1"})
        device_id = self.resolve(mount, "sdb1")
        um._update_connected_devices({"sdc1"})
        self.assertEqual(self.resolve(mount, "sdc1"), device_id)

    def test_late_worker_cannot_register_removed_device(self):
        mount = self.mount(1, 123456)
        um._update_connected_devices({"sdb1"})
        um._update_connected_devices(set())
        with self.assertRaises(OSError):
            self.resolve(mount, "sdb1")
        um._update_connected_devices({"sdc1"})
        self.assertEqual(self.resolve(mount, "sdc1"), 123456)

    def test_single_missing_poll_does_not_forget_existing_owner(self):
        first, second = self.mount(1, 123456), self.mount(2, 123456)
        um._update_connected_devices({"sdb1"})
        self.resolve(first, "sdb1")
        um._update_connected_devices(set())
        um._update_connected_devices({"sdb1", "sdc1"})
        self.assertNotEqual(self.resolve(second, "sdc1"), 123456)

    def check_ten_cameras(self, marker):
        mounts = [self.mount(n, marker) for n in range(10)]
        barrier = threading.Barrier(10)

        def register(n):
            barrier.wait(timeout=5)
            return self.resolve(mounts[n], f"sd{n}")

        with ThreadPoolExecutor(max_workers=10) as pool:
            ids = list(pool.map(register, range(10)))
        self.assertEqual(len(set(ids)), 10)
        if marker is not None:
            self.assertEqual(ids.count(marker), 1)
        for n, device_id in enumerate(ids):
            self.assertEqual(um._read_device_id_from_usb(mounts[n]), device_id)
        um._update_connected_devices({f"new{n}" for n in range(10)})
        for n, device_id in enumerate(ids):
            self.assertEqual(self.resolve(mounts[n], f"new{n}"), device_id)

    def test_copy_tasks_with_duplicate_markers_use_separate_folders_and_cards(self):
        first, second = self.mount(1, 123456), self.mount(2, 123456)
        for path, content in ((first, b"first"), (second, b"second")):
            with open(os.path.join(path, "video.mp4"), "wb") as stream:
                stream.write(content)
        dest = self.mount("archive")
        progress = queue.Queue()
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
             mock.patch.object(um, "_get_device_serial_linux", return_value="SAME"), \
             mock.patch.object(um, "get_dest_base", return_value=dest), \
             mock.patch.object(um, "dest_available", return_value=True), \
             mock.patch.object(um, "_delete_source_videos"):
            first_id = um.copy_task(first, first, "sdb1", None, None, progress_queue=progress)[0]
            second_id = um.copy_task(second, second, "sdc1", None, None, progress_queue=progress)[0]
        self.assertNotEqual(first_id, second_id)
        for device_id, content in ((first_id, b"first"), (second_id, b"second")):
            with open(os.path.join(dest, f"Device{device_id}", "video.mp4"), "rb") as stream:
                self.assertEqual(stream.read(), content)
        self.assertEqual({event[0] for event in list(progress.queue)}, {first_id, second_id})

    def test_marker_write_failure_reports_error_without_copy_or_delete(self):
        mount = self.mount(1)
        progress = queue.Queue()
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
             mock.patch.object(um, "_get_device_serial_linux", return_value="SAME"), \
             mock.patch.object(um, "_write_device_id_to_usb", return_value=None), \
             mock.patch.object(um, "_copy_files") as copy, \
             mock.patch.object(um, "_delete_source_videos") as delete:
            um.copy_task(mount, mount, "sdb1", None, None, progress_queue=progress)
        copy.assert_not_called()
        delete.assert_not_called()
        self.assertEqual(progress.get_nowait()[2], "error")


if __name__ == "__main__":
    unittest.main()
