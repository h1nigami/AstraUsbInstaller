"""Раздельная регистрация устройств с одинаковым USB-серийником."""

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
        for name, value in (("DB_PATH", os.path.join(self.tmp.name, "devices.db")),
                            ("_connected_devices", {}), ("_connected_device_ids", {})):
            patcher = mock.patch.object(um, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        um._init_db().close()

    def mount(self, name, device_id=None, payload=b"video"):
        path = os.path.join(self.tmp.name, str(name))
        os.makedirs(path)
        if device_id is None:
            with open(os.path.join(path, "video.mp4"), "wb") as out:
                out.write(payload)
        else:
            dcim = os.path.join(path, "DCIM")
            os.makedirs(dcim)
            with open(os.path.join(dcim,
                                   f"A11_{device_id}_222222_20260915120000_0001.mp4"), "wb") as out:
                out.write(payload)
        return path

    def resolve(self, mount, devname):
        conn = um._connect()
        try:
            return um._resolve_device_id(conn, mount, "SAME_FACTORY_SERIAL", "CAM", devname)
        finally:
            conn.close()

    def test_ten_distinct_device_ids_register_concurrently(self):
        mounts = [self.mount(n, 1234560 + n) for n in range(10)]
        barrier = threading.Barrier(10)

        def register(n):
            barrier.wait(timeout=5)
            return self.resolve(mounts[n], f"sd{n}")

        with ThreadPoolExecutor(max_workers=10) as pool:
            ids = list(pool.map(register, range(10)))
        self.assertEqual(ids, [1234560 + n for n in range(10)])
        conn = um._connect()
        try:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 10)
            self.assertEqual({row[0] for row in conn.execute("SELECT id_source FROM devices")}, {"device"})
        finally:
            conn.close()
        self.assertTrue(all(not os.path.exists(os.path.join(mount, ".astra_id")) for mount in mounts))

    def test_two_connected_devices_with_same_id_are_not_merged(self):
        first = self.mount("first", 1234567)
        second = self.mount("second", 1234567)
        self.assertEqual(self.resolve(first, "sdb1"), 1234567)
        with self.assertRaisesRegex(OSError, "Дубликат ID устройства 1234567"):
            self.resolve(second, "sdc1")
        conn = um._connect()
        try:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 1)
        finally:
            conn.close()

    def test_reconnect_after_release_keeps_device_id(self):
        mount = self.mount("camera", 1234567)
        um._update_connected_devices({"sdb1"})
        self.assertEqual(self.resolve(mount, "sdb1"), 1234567)
        um._release_device_id("sdb1")
        um._update_connected_devices({"sdc1"})
        self.assertEqual(self.resolve(mount, "sdc1"), 1234567)

    def test_removed_device_cannot_finish_late_id_read(self):
        mount = self.mount("camera", 1234567)
        um._update_connected_devices({"sdb1"})
        entered = threading.Event()
        release = threading.Event()
        original = um._read_device_id

        def delayed(path):
            entered.set()
            release.wait(5)
            return original(path)

        with mock.patch.object(um, "_read_device_id", side_effect=delayed), \
             ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(self.resolve, mount, "sdb1")
            self.assertTrue(entered.wait(2))
            um._update_connected_devices(set())
            release.set()
            with self.assertRaisesRegex(OSError, "отключено"):
                future.result(timeout=2)

    def test_duplicate_does_not_copy_or_delete_second_source(self):
        first = self.mount("first", 1234567, b"first")
        second = self.mount("second", 1234567, b"second")
        dest = os.path.join(self.tmp.name, "archive")
        os.makedirs(dest)
        progress = queue.Queue()
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
             mock.patch.object(um, "_get_device_serial_linux", return_value="SAME"), \
             mock.patch.object(um, "get_dest_base", return_value=dest), \
             mock.patch.object(um, "dest_available", return_value=True), \
             mock.patch.object(um, "_repair_archive_ownership"):
            self.assertEqual(um.copy_task(first, first, "sdb1", None, None)[0], 1234567)
            self.assertIsNone(um.copy_task(second, second, "sdc1", None, None,
                                            progress_queue=progress)[0])
        file_name = "A11_1234567_222222_20260915120000_0001.mp4"
        with open(os.path.join(dest, "Device1234567", "DCIM", file_name), "rb") as stream:
            self.assertEqual(stream.read(), b"first")
        with open(os.path.join(second, "DCIM", file_name), "rb") as stream:
            self.assertEqual(stream.read(), b"second")
        self.assertEqual([progress.get_nowait()[2] for _ in range(progress.qsize())],
                         ["identifying", "error"])

    def test_replaced_card_is_not_copied_under_previous_id(self):
        source = self.mount("replace", 1234567, b"old")
        dest = os.path.join(self.tmp.name, "archive")
        os.makedirs(dest)
        progress = queue.Queue()

        def replace_source():
            dcim = os.path.join(source, "DCIM")
            os.unlink(os.path.join(dcim, "A11_1234567_222222_20260915120000_0001.mp4"))
            with open(os.path.join(dcim,
                                   "A11_7654321_222222_20260915120000_0001.mp4"), "wb") as out:
                out.write(b"new")
            return dest

        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
             mock.patch.object(um, "_get_device_serial_linux", return_value="SAME"), \
             mock.patch.object(um, "get_dest_base", side_effect=replace_source), \
             mock.patch.object(um, "dest_available", return_value=True), \
             mock.patch.object(um, "_repair_archive_ownership"):
            um.copy_task(source, source, "sdb1", None, None, progress_queue=progress)

        self.assertFalse(os.path.exists(os.path.join(dest, "Device1234567")))
        self.assertTrue(os.path.exists(os.path.join(
            source, "DCIM", "A11_7654321_222222_20260915120000_0001.mp4")))
        self.assertEqual([progress.get_nowait()[2] for _ in range(progress.qsize())],
                         ["identifying", "error"])

    def test_replacement_during_scan_aborts_before_archive_creation(self):
        source = self.mount("scan-replace", 1234567, b"old")
        dest = os.path.join(self.tmp.name, "archive")
        os.makedirs(dest)
        original_scan = um._scan_drive

        def replace_after_scan(path):
            result = original_scan(path)
            dcim = os.path.join(source, "DCIM")
            os.unlink(os.path.join(dcim, "A11_1234567_222222_20260915120000_0001.mp4"))
            with open(os.path.join(dcim,
                                   "A11_7654321_222222_20260915120000_0001.mp4"), "wb") as out:
                out.write(b"new")
            return result

        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
             mock.patch.object(um, "_get_device_serial_linux", return_value="SAME"), \
             mock.patch.object(um, "get_dest_base", return_value=dest), \
             mock.patch.object(um, "dest_available", return_value=True), \
             mock.patch.object(um, "_scan_drive", side_effect=replace_after_scan), \
             mock.patch.object(um, "_repair_archive_ownership"):
            um.copy_task(source, source, "sdb1", None, None)

        self.assertFalse(os.path.exists(os.path.join(dest, "Device1234567")))
        self.assertTrue(os.path.exists(os.path.join(
            source, "DCIM", "A11_7654321_222222_20260915120000_0001.mp4")))


if __name__ == "__main__":
    unittest.main()


class ReenumeratedCardTest(SharedCameraIdentityTest):
    """Хаб сбрасывает порт, и карта возвращается под новым именем. Ядро
    какое-то время держит оба имени, поэтому «владелец ещё на шине» тут не
    работает. Различаем по метке файловой системы: у разных карт она разная,
    у одной и той же при переподключении сохраняется."""

    def test_same_card_under_new_name_takes_over_its_id(self):
        first = self.mount("before", 1234567)
        second = self.mount("after", 1234567)
        with mock.patch.object(um, "_get_filesystem_uuid", lambda dev: "C23E-1A23"):
            self.assertEqual(self.resolve(first, "sdb"), 1234567)
            # Та же карта, новое имя — не дубликат, а возврат.
            self.assertEqual(self.resolve(second, "sdg"), 1234567)

    def test_two_different_cards_with_same_id_still_rejected(self):
        first = self.mount("one", 1234567)
        second = self.mount("two", 1234567)
        uuids = {"/dev/sdb": "C23E-1A23", "/dev/sdg": "DFDD-190B"}
        with mock.patch.object(um, "_get_filesystem_uuid", lambda dev: uuids.get(dev)):
            self.assertEqual(self.resolve(first, "sdb"), 1234567)
            with self.assertRaisesRegex(OSError, "Дубликат ID устройства 1234567"):
                self.resolve(second, "sdg")

    def test_unknown_uuid_falls_back_to_rejecting(self):
        first = self.mount("one", 1234567)
        second = self.mount("two", 1234567)
        with mock.patch.object(um, "_get_filesystem_uuid", lambda dev: None):
            self.assertEqual(self.resolve(first, "sdb"), 1234567)
            with self.assertRaisesRegex(OSError, "Дубликат ID устройства 1234567"):
                self.resolve(second, "sdg")
