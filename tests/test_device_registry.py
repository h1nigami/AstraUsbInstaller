"""Проверки ID регистратора и схемы SQLite."""

import os
import sys
import sqlite3
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class InitDbTest(unittest.TestCase):
    def test_creates_schema_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as d:
            db_path = os.path.join(d, "test.db")
            with mock.patch.object(um, "DB_PATH", db_path):
                um._init_db().close()
                # Second call simulates a restart against an existing DB —
                # the ALTER TABLE migration must not raise.
                conn2 = um._init_db()
            try:
                tables = {r[0] for r in conn2.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'")}
                self.assertTrue({"devices", "backups"} <= tables)
                cols = {r[1] for r in conn2.execute("PRAGMA table_info(devices)")}
                self.assertIn("person", cols)
                self.assertIn("name", cols)
                self.assertIn("serial", cols)
                self.assertIn("id_source", cols)
            finally:
                conn2.close()


class DeviceIdentityReaderTest(unittest.TestCase):
    def test_reads_device_id_from_log(self):
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "LOG"))
            with open(os.path.join(mount, "LOG", "boot.txt"), "w", encoding="utf-8") as out:
                out.write("2026/09/15-12:00:00 #ID:1234567 #Включение системы\n")
            self.assertEqual(getattr(um, "_read_device_id", lambda _: None)(mount), 1234567)

    def test_reads_device_id_from_latest_recording_without_log(self):
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "DCIM"))
            for name in ("A11_1111111_222222_20260914120000_0001.mp4",
                         "A11_7654321_222222_20260915120000_0001.mp4"):
                with open(os.path.join(mount, "DCIM", name), "wb"):
                    pass
            self.assertEqual(um._read_device_id(mount), 7654321)

    def test_log_id_wins_over_conflicting_recording_id(self):
        """Записи на карте могут быть старыми (карту переставили с другого
        регистратора) — журнал считается более надёжным источником и
        побеждает без ошибки при расхождении."""
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "LOG"))
            os.makedirs(os.path.join(mount, "DCIM"))
            with open(os.path.join(mount, "LOG", "boot.txt"), "w", encoding="utf-8") as out:
                out.write("#ID:1234567\n")
            with open(os.path.join(mount, "DCIM", "A11_7654321_222222_20260915120000_0001.mp4"), "wb"):
                pass
            self.assertEqual(um._read_device_id(mount), 1234567)

    def test_empty_id_in_log_is_reported_as_missing(self):
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "LOG"))
            with open(os.path.join(mount, "LOG", "boot.txt"), "w", encoding="utf-8") as out:
                out.write("#ID: \n")
            with self.assertRaisesRegex(OSError, "ID регистратора не найден"):
                um._read_device_id(mount)

    def test_latest_log_without_id_does_not_reuse_older_id(self):
        with tempfile.TemporaryDirectory() as mount:
            log_dir = os.path.join(mount, "LOG")
            os.makedirs(log_dir)
            with open(os.path.join(log_dir, "20260914.txt"), "w", encoding="utf-8") as out:
                out.write("#ID:1111111\n")
            with open(os.path.join(log_dir, "20260915.txt"), "w", encoding="utf-8") as out:
                out.write("Запуск регистратора\n")
            with self.assertRaisesRegex(OSError, "ID регистратора не найден"):
                um._read_device_id(mount)

    def test_unreadable_log_does_not_fall_back_to_recording(self):
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "LOG"))
            os.makedirs(os.path.join(mount, "DCIM"))
            with open(os.path.join(mount, "LOG", "boot.txt"), "wb") as out:
                out.write(b"\xff")
            with open(os.path.join(mount, "DCIM", "A11_7654321_222222_20260915120000_0001.mp4"), "wb"):
                pass
            with self.assertRaisesRegex(OSError, "Не удалось прочитать журнал"):
                um._read_device_id(mount)

    def test_very_long_decimal_id_is_rejected_without_parser_crash(self):
        with tempfile.TemporaryDirectory() as mount:
            os.makedirs(os.path.join(mount, "LOG"))
            with open(os.path.join(mount, "LOG", "boot.txt"), "w", encoding="utf-8") as out:
                out.write("#ID:" + "9" * 5000 + "\n")
            with self.assertRaisesRegex(OSError, "ID регистратора не найден"):
                um._read_device_id(mount)


class ResolveDeviceIdTest(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self.tmpdir.name, "d.db")
        self._patcher = mock.patch.object(um, "DB_PATH", self.db_path)
        self._patcher.start()
        um._init_db().close()
        self.conn = sqlite3.connect(self.db_path)

    def tearDown(self):
        self.conn.close()
        self._patcher.stop()
        self.tmpdir.cleanup()

    @staticmethod
    def _recording(mount, device_id):
        os.makedirs(os.path.join(mount, "DCIM"), exist_ok=True)
        with open(os.path.join(mount, "DCIM", f"A11_{device_id}_222222_20260915120000_0001.mp4"), "wb"):
            pass

    def test_new_device_creates_record_without_marker(self):
        with tempfile.TemporaryDirectory() as mp:
            self._recording(mp, 1234567)
            dev_id = um._resolve_device_id(self.conn, mp, "SER1", "LABEL1", "sda1")
            self.assertEqual(dev_id, 1234567)
            self.assertFalse(os.path.exists(os.path.join(mp, ".astra_id")))
            row = self.conn.execute(
                "SELECT serial, label, id_source FROM devices WHERE id=?", (dev_id,)).fetchone()
            self.assertEqual(row, ("SER1", "LABEL1", "device"))

    def test_corrupt_existing_marker_is_ignored_and_preserved(self):
        with tempfile.TemporaryDirectory() as mp:
            self._recording(mp, 1234567)
            marker = os.path.join(mp, ".astra_id")
            with open(marker, "w", encoding="utf-8") as stream:
                stream.write("broken")
            self.assertEqual(um._resolve_device_id(self.conn, mp, "SER", "CAM", "sdb1"), 1234567)
            with open(marker, encoding="utf-8") as stream:
                self.assertEqual(stream.read(), "broken")

    def test_old_marker_without_device_id_does_not_register(self):
        with tempfile.TemporaryDirectory() as mp:
            with open(os.path.join(mp, ".astra_id"), "w", encoding="utf-8") as out:
                out.write("999\n")
            with self.assertRaisesRegex(OSError, "ID регистратора не найден"):
                um._resolve_device_id(self.conn, mp, "CONFLICTING_SERIAL", "CAM", "sdb1")
            self.assertEqual(self.conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 0)

    def test_device_id_wins_over_old_astra_marker(self):
        with tempfile.TemporaryDirectory() as mp:
            os.makedirs(os.path.join(mp, "DCIM"))
            with open(os.path.join(mp, "DCIM", "A11_1234567_222222_20260915120000_0001.mp4"), "wb"):
                pass
            old_marker = os.path.join(mp, ".astra_id")
            with open(old_marker, "w", encoding="utf-8") as out:
                out.write("999\n")
            self.assertEqual(um._resolve_device_id(self.conn, mp, "SHARED", "CAM", "sdb1"), 1234567)
            with open(old_marker, encoding="utf-8") as stream:
                self.assertEqual(stream.read(), "999\n")

    def test_old_database_row_with_same_number_is_not_reused(self):
        self.conn.execute(
            "INSERT INTO devices (id, serial, label, first_seen, last_seen)"
            " VALUES (1234567, 'OLD', 'OLD', '2026-09-14T10:00:00', '2026-09-14T10:00:00')")
        self.conn.commit()
        with tempfile.TemporaryDirectory() as mp:
            self._recording(mp, 1234567)
            with self.assertRaisesRegex(OSError, "занят прежней записью"):
                um._resolve_device_id(self.conn, mp, "SHARED", "CAM", "sdb1")
            self.assertEqual(self.conn.execute("SELECT serial FROM devices WHERE id=1234567")
                             .fetchone()[0], "OLD")

    def test_same_serial_with_different_device_ids_gets_separate_rows(self):
        with tempfile.TemporaryDirectory() as mp1:
            self._recording(mp1, 1234567)
            first_id = um._resolve_device_id(self.conn, mp1, "SERIALSAME", "L1", "sda1")
        with tempfile.TemporaryDirectory() as mp2:
            self._recording(mp2, 7654321)
            second_id = um._resolve_device_id(self.conn, mp2, "SERIALSAME", "L1", "sda2")
        self.assertEqual((first_id, second_id), (1234567, 7654321))
        serials = [row[0] for row in self.conn.execute(
            "SELECT serial FROM devices WHERE id IN (?, ?)", (first_id, second_id))]
        self.assertEqual(len(set(serials)), 2)

    def test_missing_usb_serial_does_not_merge_different_ids(self):
        with tempfile.TemporaryDirectory() as mp1:
            self._recording(mp1, 1234567)
            id1 = um._resolve_device_id(self.conn, mp1, None, "L", "sda1")
        with tempfile.TemporaryDirectory() as mp2:
            self._recording(mp2, 7654321)
            id2 = um._resolve_device_id(self.conn, mp2, None, "L", "sda1")
        self.assertEqual((id1, id2), (1234567, 7654321))
        serials = [r[0] for r in self.conn.execute(
            "SELECT serial FROM devices WHERE id IN (?, ?)", (id1, id2))]
        self.assertEqual(len(set(serials)), 2, "synthetic serials must not collide")

    def test_rename_survives_reconnect_and_label_refresh(self):
        with tempfile.TemporaryDirectory() as mp:
            self._recording(mp, 1234567)
            dev_id = um._resolve_device_id(self.conn, mp, "SERNAME", "LABEL1", "sda1")
            self.conn.execute("UPDATE devices SET name = ? WHERE id = ?", ("Kiosk-1", dev_id))
            self.conn.commit()

            dev_id2 = um._resolve_device_id(self.conn, mp, "SERNAME", "NEWLABEL", "sda1")
            self.assertEqual(dev_id2, dev_id)

            row = self.conn.execute(
                "SELECT label, name FROM devices WHERE id=?", (dev_id,)).fetchone()
            self.assertEqual(row[0], "NEWLABEL")
            self.assertEqual(row[1], "Kiosk-1")

class FriendlyLabelTest(unittest.TestCase):
    def test_custom_name_is_shown_after_astra_id(self):
        self.assertEqual(um._friendly_device_label(3, "Проходная"), "Astra ID 3 · Проходная")

    def test_without_name_shows_astra_id(self):
        self.assertEqual(um._friendly_device_label(3, ""), "Astra ID 3")
        self.assertEqual(um._friendly_device_label(3, None), "Astra ID 3")


class ShortLabelTest(unittest.TestCase):
    def test_name_without_decorations(self):
        self.assertEqual(um._short_device_label(3, "Проходная"), "Проходная")

    def test_without_name_shows_bare_id(self):
        self.assertEqual(um._short_device_label(3, ""), "3")
        self.assertEqual(um._short_device_label(3, None), "3")


class SharedSerialTest(unittest.TestCase):
    """USB-эмуляторы отдают один серийник на все экземпляры."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        with mock.patch.object(um, "DB_PATH", os.path.join(self.tmp.name, "t.db")):
            self.conn = um._init_db()

    def tearDown(self):
        self.conn.close()
        self.tmp.cleanup()

    def _mount(self, name, device_id):
        mp = os.path.join(self.tmp.name, name)
        os.makedirs(mp, exist_ok=True)
        ResolveDeviceIdTest._recording(mp, device_id)
        return mp

    def test_device_with_taken_serial_still_gets_a_row(self):
        serial = "Linux_File-Stor_Gadget_123456789ABC-0:0"
        first = um._resolve_device_id(self.conn, self._mount("a", 1234567), serial, "sdb1", "sdb1")

        second = um._resolve_device_id(
            self.conn, self._mount("b", 3666666), serial, "sdc1", "sdc1")

        self.assertEqual(second, 3666666)
        self.assertNotEqual(first, second)
        row = self.conn.execute(
            "SELECT id FROM devices WHERE id=?", (second,)).fetchone()
        self.assertIsNotNone(
            row, "устройство с занятым серийником должно попадать в список")

    def test_recovers_devices_that_only_exist_in_backups(self):
        now = "2026-09-01T10:00:00"
        self.conn.execute(
            "INSERT INTO backups (device_id, dest_path, total_files, total_bytes,"
            " started_at, finished_at) VALUES (?, ?, ?, ?, ?, ?)",
            (777, "/dest/Device777", 3, 100, now, now))
        self.conn.commit()
        self.conn.close()

        with mock.patch.object(um, "DB_PATH", os.path.join(self.tmp.name, "t.db")):
            self.conn = um._init_db()

        row = self.conn.execute("SELECT id FROM devices WHERE id=777").fetchone()
        self.assertIsNotNone(
            row, "устройство, у которого остались только бэкапы, должно восстанавливаться")


if __name__ == "__main__":
    unittest.main(verbosity=2)
