"""Tests for device identity + SQLite schema logic in usb_monitor.py.

Covers: _init_db schema/migrations, the .astra_id marker file round-trip,
and _resolve_device_id's three resolution paths (marker file, serial match,
brand-new device).
"""

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
                self.assertIn("serial", cols)
            finally:
                conn2.close()


class DeviceIdMarkerFileTest(unittest.TestCase):
    def test_write_then_read_roundtrip(self):
        with tempfile.TemporaryDirectory() as mp:
            um._write_device_id_to_usb(mp, 42)
            self.assertEqual(um._read_device_id_from_usb(mp), 42)

    def test_read_missing_file_returns_none(self):
        with tempfile.TemporaryDirectory() as mp:
            self.assertIsNone(um._read_device_id_from_usb(mp))

    def test_read_non_numeric_content_returns_none(self):
        with tempfile.TemporaryDirectory() as mp:
            with open(os.path.join(mp, um.DEVICE_ID_FILE), "w") as f:
                f.write("not-a-number")
            self.assertIsNone(um._read_device_id_from_usb(mp))

    def test_none_mountpoint_is_safe_noop(self):
        self.assertIsNone(um._read_device_id_from_usb(None))
        um._write_device_id_to_usb(None, 5)  # must not raise


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

    def test_new_device_creates_record_and_writes_marker(self):
        with tempfile.TemporaryDirectory() as mp:
            dev_id = um._resolve_device_id(self.conn, mp, "SER1", "LABEL1", "sda1")
            self.assertEqual(um._read_device_id_from_usb(mp), dev_id)
            row = self.conn.execute(
                "SELECT serial, label FROM devices WHERE id=?", (dev_id,)).fetchone()
            self.assertEqual(row, ("SER1", "LABEL1"))

    def test_existing_marker_wins_and_updates_last_seen_label(self):
        with tempfile.TemporaryDirectory() as mp:
            um._write_device_id_to_usb(mp, 999)
            dev_id = um._resolve_device_id(self.conn, mp, "SERX", "NEWLABEL", "sdb1")
            self.assertEqual(dev_id, 999)
            row = self.conn.execute(
                "SELECT label FROM devices WHERE id=?", (999,)).fetchone()
            self.assertEqual(row[0], "NEWLABEL")

    def test_serial_match_reuses_existing_id_without_marker(self):
        with tempfile.TemporaryDirectory() as mp1:
            first_id = um._resolve_device_id(self.conn, mp1, "SERIALSAME", "L1", "sda1")
        with tempfile.TemporaryDirectory() as mp2:
            # Marker missing on mp2 (e.g. reformatted drive), but serial matches DB.
            second_id = um._resolve_device_id(self.conn, mp2, "SERIALSAME", "L1", "sda2")
            self.assertEqual(second_id, first_id)
            self.assertEqual(um._read_device_id_from_usb(mp2), first_id)

    def test_no_marker_no_serial_creates_distinct_devices_without_crashing(self):
        # Regression: devices.serial is UNIQUE NOT NULL, so two drives that
        # both expose no discoverable serial must not collide on "" and
        # raise IntegrityError out of the backup worker.
        with tempfile.TemporaryDirectory() as mp1:
            id1 = um._resolve_device_id(self.conn, mp1, None, "L", "sda1")
        with tempfile.TemporaryDirectory() as mp2:
            id2 = um._resolve_device_id(self.conn, mp2, None, "L", "sda1")
        self.assertNotEqual(id1, id2)
        serials = [r[0] for r in self.conn.execute(
            "SELECT serial FROM devices WHERE id IN (?, ?)", (id1, id2))]
        self.assertEqual(len(set(serials)), 2, "synthetic serials must not collide")

    def test_get_device_id_by_serial_edge_cases(self):
        self.assertIsNone(um._get_device_id_by_serial(self.conn, None))
        self.assertIsNone(um._get_device_id_by_serial(None, "x"))
        self.assertIsNone(um._get_device_id_by_serial(self.conn, "unknown-serial"))

    def test_create_device_falls_back_to_devname_when_no_label(self):
        dev_id = um._create_device(self.conn, "", "", "sdz1")
        row = self.conn.execute(
            "SELECT label FROM devices WHERE id=?", (dev_id,)).fetchone()
        self.assertEqual(row[0], "sdz1")


if __name__ == "__main__":
    unittest.main(verbosity=2)
