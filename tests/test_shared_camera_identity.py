"""Раздельная регистрация камер с одинаковыми заводскими серийниками."""

import os
import queue
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor, TimeoutError
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
        self.check_ten_new_cameras()

    def test_duplicate_connected_marker_is_rejected_without_rewrite(self):
        first, second = self.mount(1, 123456), self.mount(2, 123456)
        self.resolve(first, "sdb1")
        with self.assertRaisesRegex(OSError, "Дубликат Astra ID 123456"):
            self.resolve(second, "sdc1")
        self.assertEqual(um._read_device_id_from_usb(second), 123456)

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
        with self.assertRaisesRegex(OSError, "Дубликат Astra ID 123456"):
            self.resolve(second, "sdc1")

    def test_slow_marker_io_does_not_block_poll_or_other_camera(self):
        for operation in ("_read_device_id_from_usb", "_write_device_id_to_usb"):
            with self.subTest(operation=operation):
                first, second = self.mount(operation + "a"), self.mount(operation + "b")
                entered, release = threading.Event(), threading.Event()
                original = getattr(um, operation)

                def delayed(path, *args):
                    if path == first:
                        entered.set()
                        release.wait(5)
                    return original(path, *args)

                um._update_connected_devices({"sdb1", "sdc1"})
                with mock.patch.object(um, operation, side_effect=delayed), \
                     ThreadPoolExecutor(max_workers=3) as pool:
                    slow = pool.submit(self.resolve, first, "sdb1")
                    try:
                        self.assertTrue(entered.wait(2))
                        poll = pool.submit(um._update_connected_devices, {"sdb1", "sdc1"})
                        other = pool.submit(self.resolve, second, "sdc1")
                        self.assertIsNone(poll.result(timeout=1))
                        other_id = other.result(timeout=1)
                    except TimeoutError:
                        self.fail("Задержка одного USB блокирует опрос или другую камеру")
                    finally:
                        release.set()
                    self.assertNotEqual(slow.result(timeout=2), other_id)

    def test_disconnect_during_marker_write_rejects_registration(self):
        mount = self.mount(1)
        um._update_connected_devices({"sdb1"})
        original = um._write_device_id_to_usb

        def disconnect(path, device_id):
            original(path, device_id)
            um._update_connected_devices(set())

        with mock.patch.object(um, "_write_device_id_to_usb", side_effect=disconnect):
            with self.assertRaises(OSError):
                self.resolve(mount, "sdb1")
        device_id = um._read_device_id_from_usb(mount)
        second = self.mount(2, device_id)
        um._update_connected_devices({"sdb1", "sdc1"})
        self.assertEqual(self.resolve(second, "sdc1"), device_id)

    def test_failed_late_write_does_not_release_replacement_claim(self):
        first, replacement = self.mount(1), self.mount(2)
        original = um._write_device_id_to_usb
        replacement_ids = []

        def replace(path, device_id):
            if path == first:
                um._release_device_id("sdb1")
                original(replacement, device_id)
                replacement_ids.append(self.resolve(replacement, "sdb1"))
            else:
                original(path, device_id)

        with mock.patch.object(um, "_write_device_id_to_usb", side_effect=replace):
            with self.assertRaises(OSError):
                self.resolve(first, "sdb1")
        clone = self.mount(3, replacement_ids[0])
        with self.assertRaisesRegex(OSError, f"Дубликат Astra ID {replacement_ids[0]}"):
            self.resolve(clone, "sdc1")

    def test_late_initial_read_does_not_replace_reconnected_device_claim(self):
        first, replacement = self.mount(1, 7), self.mount(2, 9)
        original = um._read_device_id_from_usb
        um._update_connected_devices({"sdb1", "sdc1"})

        def reconnect(path):
            if path == first:
                um._release_device_id("sdb1")
                self.assertEqual(self.resolve(replacement, "sdb1"), 9)
            return original(path)

        with mock.patch.object(um, "_read_device_id_from_usb", side_effect=reconnect):
            with self.assertRaises(OSError):
                self.resolve(first, "sdb1")
        clone = self.mount(3, 9)
        with self.assertRaisesRegex(OSError, "Дубликат Astra ID 9"):
            self.resolve(clone, "sdc1")

    def check_ten_new_cameras(self):
        mounts = [self.mount(n) for n in range(10)]
        barrier = threading.Barrier(10)

        def register(n):
            barrier.wait(timeout=5)
            return self.resolve(mounts[n], f"sd{n}")

        with ThreadPoolExecutor(max_workers=10) as pool:
            ids = list(pool.map(register, range(10)))
        self.assertEqual(len(set(ids)), 10)
        for n, device_id in enumerate(ids):
            self.assertEqual(um._read_device_id_from_usb(mounts[n]), device_id)
        um._update_connected_devices({f"new{n}" for n in range(10)})
        for n, device_id in enumerate(ids):
            self.assertEqual(self.resolve(mounts[n], f"new{n}"), device_id)

    def test_copy_task_rejects_duplicate_marker_without_copying(self):
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
             mock.patch.object(um.subprocess, "run"), \
             mock.patch.object(um, "_delete_source_videos"):
            first_id = um.copy_task(first, first, "sdb1", None, None, progress_queue=progress)[0]
            second_id = um.copy_task(second, second, "sdc1", None, None, progress_queue=progress)[0]
        self.assertEqual(first_id, 123456)
        self.assertIsNone(second_id)
        self.assertEqual(um._read_device_id_from_usb(second), 123456)
        with open(os.path.join(dest, "Device123456", "video.mp4"), "rb") as stream:
            self.assertEqual(stream.read(), b"first")

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
