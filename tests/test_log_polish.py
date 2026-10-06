"""Мелочи журнала, найденные на станции: прогресс, отключение, отступы."""

import contextlib
import io
import os
import queue
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class ProgressCountsSkippedFilesTest(unittest.TestCase):
    def test_progress_reaches_100_when_some_files_were_already_archived(self):
        # Станция видела «99.2% | 497/650» у законченной выгрузки: файлы, уже
        # лежащие в архиве, пропускались мимо счётчика прогресса.
        with tempfile.TemporaryDirectory() as tmp:
            src, dst = os.path.join(tmp, "card"), os.path.join(tmp, "archive")
            os.makedirs(src)
            os.makedirs(dst)
            for name, body in (("old.mp4", b"x" * 300), ("new.mp4", b"y" * 100)):
                with open(os.path.join(src, name), "wb") as f:
                    f.write(body)
            with open(os.path.join(dst, "old.mp4"), "wb") as f:
                f.write(b"x" * 300)
            stamp = os.stat(os.path.join(src, "old.mp4")).st_mtime
            os.utime(os.path.join(dst, "old.mp4"), (stamp, stamp))

            out, emitted = io.StringIO(), []
            um._log_progress_cache.clear()
            with mock.patch.object(um, "USE_RICH", False), mock.patch.object(um, "IS_TTY", False), \
                 mock.patch.object(um, "_require_archive_device"), contextlib.redirect_stdout(out):
                copied, copied_bytes, backed_up, failed = um._copy_files_open(
                    src, dst, "20261006_120000", "ID 7", 2, 400, None, None, time.time(),
                    emit_fn=lambda state, cur, total, msg: emitted.append(cur))

        self.assertEqual((copied, copied_bytes, failed), (1, 100, 0))
        self.assertEqual(len(backed_up), 2)
        self.assertIn("100.0% | 2/2 файлов", out.getvalue())
        self.assertEqual(emitted[-1], 400)


class RemovalIsLoggedTest(unittest.TestCase):
    def test_confirmed_removal_is_written_to_log(self):
        polls = iter([{"sdd": None}, {}, {}, {}, {}])
        stop = um.threading.Event()

        def poll():
            item = next(polls, None)
            if item is None:
                stop.set()
                return {}
            return item

        out = io.StringIO()
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(out), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
             mock.patch.object(um, "MOUNT_BASE", os.path.join(tmp, "mnt")), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "c.json")), \
             mock.patch.object(um, "_get_linux_partitions", side_effect=poll), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value=None), \
             mock.patch.object(um, "_repair_archive_ownership"), \
             mock.patch.object(um, "copy_task_linux", return_value=(None, 0, 0)):
            um.monitor_usb(interval=0.01, stop_event=stop, progress_queue=queue.Queue())
        self.assertIn("Устройство отключено: sdd", out.getvalue())


class FileLogIndentTest(unittest.TestCase):
    def test_indent_is_dropped_after_the_date(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "app.log")
            with open(path, "w", encoding="utf-8") as fh:
                um._LogTee(io.StringIO(), fh).write("  Новое USB-устройство: sdd\n")
            with open(path, encoding="utf-8") as f:
                line = f.read().rstrip("\n")
        self.assertRegex(line, r"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}  Новое USB-устройство: sdd$")

    def test_indent_stays_in_systemd(self):
        stream = io.StringIO()
        with tempfile.TemporaryDirectory() as tmp:
            with open(os.path.join(tmp, "app.log"), "w", encoding="utf-8") as fh:
                um._LogTee(stream, fh).write("  Новое USB-устройство: sdd\n")
        self.assertEqual(stream.getvalue(), "  Новое USB-устройство: sdd\n")


if __name__ == "__main__":
    unittest.main()
