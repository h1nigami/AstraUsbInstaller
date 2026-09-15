"""Заводской сброс: удаление архивов, строк базы и файла настроек."""

import os
import sqlite3
import tempfile
import unittest
from unittest import mock

import usb_monitor as um


def _make_db(path):
    conn = sqlite3.connect(path)
    conn.execute(
        "CREATE TABLE devices (id INTEGER PRIMARY KEY, serial TEXT, id_source TEXT DEFAULT '')"
    )
    conn.execute(
        "CREATE TABLE backups (id INTEGER PRIMARY KEY AUTOINCREMENT, device_id INTEGER, dest_path TEXT)"
    )
    conn.execute("INSERT INTO devices (id, serial, id_source) VALUES (1, 'S1', 'device')")
    conn.execute("INSERT INTO devices (id, serial, id_source) VALUES (2, 'S2', '')")
    conn.execute("INSERT INTO backups (device_id, dest_path) VALUES (1, 'Device1')")
    conn.commit()
    conn.close()


class FactoryResetTest(unittest.TestCase):
    def test_removes_rows_archives_and_claims(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "devices.db")
            _make_db(db)
            dest = os.path.join(tmp, "USB_Backups")
            os.makedirs(os.path.join(dest, "Device1"))
            with open(os.path.join(dest, "Device1", "a.mp4"), "w") as f:
                f.write("x")
            with um._device_id_lock:
                um._connected_device_ids[("db", "sda1")] = (1, object())
            try:
                with mock.patch.object(um, "is_copying", return_value=False):
                    result = um.factory_reset(db_path=db, dest_base=dest)
            finally:
                with um._device_id_lock:
                    um._connected_device_ids.pop(("db", "sda1"), None)
            self.assertEqual(result["devices"], 2)
            self.assertEqual(result["backups"], 1)
            conn = sqlite3.connect(db)
            try:
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 0)
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 0)
                conn.execute("INSERT INTO devices (id, serial) VALUES (9, 'S9')")
            finally:
                conn.close()
            self.assertTrue(os.path.isdir(dest))
            self.assertEqual(os.listdir(dest), [])
            with um._device_id_lock:
                self.assertEqual(um._connected_device_ids, {})

    def test_missing_db_and_dest_are_tolerated(self):
        with tempfile.TemporaryDirectory() as tmp:
            with mock.patch.object(um, "is_copying", return_value=False):
                result = um.factory_reset(
                    db_path=os.path.join(tmp, "no.db"),
                    dest_base=os.path.join(tmp, "no_dir"),
                )
            self.assertEqual(result, {"devices": 0, "backups": 0, "entries": 0})

    def test_refuses_while_copying(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "devices.db")
            _make_db(db)
            with mock.patch.object(um, "is_copying", return_value=True):
                with self.assertRaises(OSError):
                    um.factory_reset(db_path=db, dest_base=tmp)
            conn = sqlite3.connect(db)
            try:
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 2)
            finally:
                conn.close()


if __name__ == "__main__":
    unittest.main()
