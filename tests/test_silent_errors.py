"""Сбои, которые раньше проглатывались молча, должны оставлять след в журнале."""

import contextlib
import io
import os
import queue
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


def _quiet():
    return contextlib.redirect_stdout(io.StringIO())


class WorkerCrashTest(unittest.TestCase):
    def test_unexpected_error_is_logged_and_shown(self):
        q = queue.Queue()
        out = io.StringIO()
        with mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "copy_task_linux", side_effect=KeyError("boom")), \
             contextlib.redirect_stdout(out):
            pool = ThreadPoolExecutor(max_workers=1)
            try:
                um._make_submit_fn(q)(pool, "sdd", None, None, None).result()
            finally:
                pool.shutdown()
        self.assertIn("Traceback", out.getvalue())
        self.assertIn("boom", out.getvalue())
        key, _label, state, *_rest, devname = q.get_nowait()
        self.assertEqual((key, state, devname), ("identity:sdd", "error", "sdd"))


class PollFailureTest(unittest.TestCase):
    def test_lsblk_failure_is_reported_once(self):
        um._log_once_last.clear()
        out = io.StringIO()
        err = subprocess.TimeoutExpired(["lsblk"], 5)
        with mock.patch.object(um.subprocess, "run", side_effect=err), contextlib.redirect_stdout(out):
            self.assertIsNone(um._get_lsblk_partitions())
            self.assertIsNone(um._get_lsblk_partitions())
        self.assertEqual(len(out.getvalue().splitlines()), 1)
        self.assertIn("lsblk не ответил", out.getvalue())

    def test_failed_poll_is_not_an_empty_bus(self):
        with mock.patch.object(um, "_get_lsblk_partitions", return_value=None), \
             mock.patch.object(um, "_get_sys_block_partitions", return_value=None):
            with self.assertRaises(OSError):
                um._get_linux_partitions()

    def test_monitor_keeps_devices_when_poll_fails(self):
        polls = iter([{"sdd": None}, OSError("lsblk"), OSError("lsblk"), OSError("lsblk")])

        def poll():
            item = next(polls, None)
            if isinstance(item, Exception):
                raise item
            if item is None:
                stop.set()
                return {"sdd": None}
            return item

        stop = um.threading.Event()
        q = queue.Queue()
        with tempfile.TemporaryDirectory() as tmp, _quiet(), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
             mock.patch.object(um, "MOUNT_BASE", os.path.join(tmp, "mnt")), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "c.json")), \
             mock.patch.object(um, "_get_linux_partitions", side_effect=poll), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value=None), \
             mock.patch.object(um, "_repair_archive_ownership"), \
             mock.patch.object(um, "copy_task_linux", return_value=(None, 0, 0)):
            um.monitor_usb(interval=0.01, stop_event=stop, progress_queue=q)
        removed = [item for item in list(q.queue) if item[0] == "_removed_"]
        self.assertEqual(removed, [])


class ArchiveReasonTest(unittest.TestCase):
    def test_reason_for_rejected_archive_is_logged(self):
        um._log_once_last.clear()
        out = io.StringIO()

        @contextlib.contextmanager
        def broken(path, create=False):
            raise OSError("Не удалось определить физический диск архива")
            yield

        with mock.patch.object(um, "_archive_directory", broken), contextlib.redirect_stdout(out):
            self.assertFalse(um._archive_path_allowed("/srv/archive"))
            self.assertFalse(um.ensure_dest_marker("/srv/archive"))
        self.assertIn("/srv/archive", out.getvalue())
        self.assertIn("физический диск", out.getvalue())


class _LockedConn:
    """Соединение, у которого любая миграция падает, как на занятой базе."""

    def __init__(self, conn):
        self.conn = conn

    def execute(self, sql, *args):
        if sql.lstrip().upper().startswith("ALTER"):
            raise sqlite3.OperationalError("database is locked")
        return self.conn.execute(sql, *args)

    def __getattr__(self, name):
        return getattr(self.conn, name)


class MigrationTest(unittest.TestCase):
    def test_repeated_init_is_fine(self):
        with tempfile.TemporaryDirectory() as tmp,              mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")):
            um._init_db().close()
            um._init_db().close()  # колонки уже есть, это не ошибка

    def test_real_migration_error_is_not_swallowed(self):
        with tempfile.TemporaryDirectory() as tmp:
            db = os.path.join(tmp, "d.db")
            conn = sqlite3.connect(db)
            conn.execute("CREATE TABLE devices (id INTEGER PRIMARY KEY, serial TEXT, label TEXT,"
                         " name TEXT, id_source TEXT, first_seen TEXT, last_seen TEXT)")
            conn.commit()
            conn.close()
            opened = []
            real_connect = sqlite3.connect

            def connect(*args, **kwargs):
                opened.append(real_connect(db))
                return _LockedConn(opened[-1])

            try:
                with mock.patch.object(um, "DB_PATH", db),                      mock.patch.object(um.sqlite3, "connect", connect):
                    with self.assertRaises(sqlite3.OperationalError):
                        um._init_db()
            finally:
                for c in opened:
                    c.close()


class MoreLoggingTest(unittest.TestCase):
    def setUp(self):
        um._log_once_last.clear()

    def test_copying_marker_failure_is_logged_once(self):
        with tempfile.TemporaryDirectory() as tmp:
            blocker = os.path.join(tmp, "file")
            open(blocker, "w").close()
            out = io.StringIO()
            with mock.patch.object(um, "COPYING_MARKER", os.path.join(blocker, "data", ".copying")),                  contextlib.redirect_stdout(out):
                um.touch_copying_marker()
                um.touch_copying_marker()
        self.assertEqual(len(out.getvalue().splitlines()), 1)
        self.assertIn("метку копирования", out.getvalue())

    def test_unreadable_config_reason_is_logged(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "config.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write('{"lock_timeout_minutes": -5}')
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertTrue(um._load_config(path).get("_config_unreadable"))
                um._load_config(path)
        self.assertEqual(len(out.getvalue().splitlines()), 1)
        self.assertIn(path, out.getvalue())
        self.assertIn("lock_timeout_minutes", out.getvalue())

    def test_unreadable_files_in_scan_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            open(os.path.join(tmp, "a.mp4"), "w").close()
            out = io.StringIO()
            with mock.patch.object(um.os.path, "getsize", side_effect=OSError("I/O error")),                  contextlib.redirect_stdout(out):
                self.assertEqual(um._scan_drive(tmp), (1, 0))
        self.assertIn("I/O error", out.getvalue())


class UpdaterLoggingTest(unittest.TestCase):
    def test_failed_tag_write_is_logged(self):
        import updater
        out = io.StringIO()
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(out):
            updater._write_failed_tag("v1.99", path=os.path.join(tmp, "no", "such", "dir"))
        self.assertIn("v1.99", out.getvalue())

    def test_broken_offline_spool_is_logged(self):
        import updater
        out = io.StringIO()
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(out):
            self.assertIsNone(updater._take_offline_spool(os.path.join(tmp, "absent")))
            self.assertEqual(out.getvalue(), "")  # спула нет — молчим, это норма
            self.assertIsNone(updater._take_offline_spool(tmp))  # спул есть, тега нет
        self.assertIn("офлайн", out.getvalue())


if __name__ == "__main__":
    unittest.main()
