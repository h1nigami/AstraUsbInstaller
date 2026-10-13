"""Поиск и выгрузка во вкладке «Поиск» без создания окна."""

import os
import sqlite3
import sys
import tempfile
import types
import unittest
from datetime import datetime
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um

try:
    import gui as gui_mod
    _HAS_TK = True
except Exception:
    gui_mod = None
    _HAS_TK = False


def _touch(path, when):
    with open(path, "wb") as f:
        f.write(b"x")
    ts = when.timestamp()
    os.utime(path, (ts, ts))


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class SearchWorkerTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dev_dir = os.path.join(self.tmp.name, "Device7")
        os.makedirs(self.dev_dir)
        _touch(os.path.join(self.dev_dir, "old.mp4"), datetime(2026, 10, 5, 12, 0))
        _touch(os.path.join(self.dev_dir, "in.mp4"), datetime(2026, 10, 11, 15, 30))
        _touch(os.path.join(self.dev_dir, "new.mp4"), datetime(2026, 10, 13, 9, 0))
        self.db = os.path.join(self.tmp.name, "devices.db")
        conn = sqlite3.connect(self.db)
        conn.execute("CREATE TABLE devices (id INTEGER, serial TEXT, person TEXT, name TEXT)")
        conn.execute("CREATE TABLE backups (device_id INTEGER, dest_path TEXT, started_at TEXT)")
        conn.execute("INSERT INTO devices VALUES (7, 's', 'Иванов', '')")
        # Сеансы и за период, и после него: архив папки общий для всех сеансов.
        for started in ("2026-10-11T16:00:00", "2026-10-13T10:00:00"):
            conn.execute("INSERT INTO backups VALUES (7, ?, ?)", (self.dev_dir, started))
        conn.commit()
        conn.close()

    def tearDown(self):
        self.tmp.cleanup()

    def _search(self, dt_from, dt_to):
        got = {}
        fake = types.SimpleNamespace(
            _get_db=lambda: sqlite3.connect(self.db),
            _apply_search_results=None,
            _search_gen=1,
            root=types.SimpleNamespace(after=lambda _ms, _fn, results, _gen: got.update(r=results)),
        )
        params = {"dt_from": dt_from, "dt_to": dt_to, "dev_id": None,
                  "person": "", "filetype": "Все", "filename": ""}
        gui_mod.App._search_worker(fake, params, 1)
        return sorted(r["filename"] for r in got["r"])

    def test_only_files_inside_period_are_listed(self):
        found = self._search(um.format_filter_dt("2026", "10", "11", "00", "00", "00"),
                             um.format_filter_dt("2026", "10", "12", "23", "59", "59"))
        self.assertEqual(found, ["in.mp4"])

    def test_file_found_even_if_copied_after_period(self):
        found = self._search(um.format_filter_dt("2026", "10", "13", "00", "00", "00"),
                             um.format_filter_dt("2026", "10", "13", "23", "59", "59"))
        self.assertEqual(found, ["new.mp4"])


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class ExportSelectionTest(unittest.TestCase):
    def test_selected_rows_win_over_all_results(self):
        results = [{"path": "a"}, {"path": "b"}, {"path": "c"}]
        fake = types.SimpleNamespace(
            _search_results=results,
            search_tree=types.SimpleNamespace(selection=lambda: ("2",)),
        )
        self.assertEqual(gui_mod.App._export_targets(fake), [{"path": "c"}])

    def test_nothing_selected_means_all_results(self):
        results = [{"path": "a"}, {"path": "b"}]
        fake = types.SimpleNamespace(
            _search_results=results,
            search_tree=types.SimpleNamespace(selection=lambda: ()),
        )
        self.assertEqual(gui_mod.App._export_targets(fake), results)


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class ArchiveDiskUsageTest(unittest.TestCase):
    def test_usage_of_archive_disk(self):
        usage = types.SimpleNamespace(total=500, used=380, free=120)
        with mock.patch.object(gui_mod, "dest_available", return_value=True),                 mock.patch.object(gui_mod.shutil, "disk_usage", return_value=usage) as du:
            self.assertIs(gui_mod._archive_disk_usage(), usage)
        du.assert_called_once_with(gui_mod.get_dest_base())

    def test_missing_archive_disk_is_not_measured(self):
        with mock.patch.object(gui_mod, "dest_available", return_value=False),                 mock.patch.object(gui_mod.shutil, "disk_usage") as du:
            self.assertIsNone(gui_mod._archive_disk_usage())
        du.assert_not_called()


if __name__ == "__main__":
    unittest.main()
