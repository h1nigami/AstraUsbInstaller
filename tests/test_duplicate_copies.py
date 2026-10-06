"""Найдено на .41: одинаковые файлы снова и снова копировались в архив с меткой
времени, а повтор при занятой станции забивал журнал «новыми устройствами»."""

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


def _write(path, body, mtime):
    with open(path, "wb") as f:
        f.write(body)
    os.utime(path, (mtime, mtime))


class TimestampedCopyTest(unittest.TestCase):
    def _run(self, archive_names):
        """Карта с новым gpsdebug.txt, в архиве старая версия и заданные копии."""
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        src, dst = os.path.join(tmp.name, "card"), os.path.join(tmp.name, "archive")
        os.makedirs(src)
        os.makedirs(dst)
        new_mtime = 1_790_000_000
        _write(os.path.join(src, "gpsdebug.txt"), b"n" * 300, new_mtime)
        _write(os.path.join(dst, "gpsdebug.txt"), b"o" * 100, new_mtime - 86400)
        for name, (size, mtime) in archive_names.items():
            _write(os.path.join(dst, name), b"n" * size, mtime)
        with mock.patch.object(um, "USE_RICH", False), mock.patch.object(um, "IS_TTY", False), \
             mock.patch.object(um, "_require_archive_device"), contextlib.redirect_stdout(io.StringIO()):
            result = um._copy_files_open(src, dst, "20261006_150000", "ID 7", 1, 300,
                                         None, None, time.time())
        return result, sorted(os.listdir(dst)), new_mtime

    def test_existing_identical_timestamped_copy_is_not_duplicated(self):
        (copied, _bytes, backed_up, failed), names, _ = self._run(
            {"gpsdebug_20261006_104834.txt": (300, 1_790_000_000)})
        self.assertEqual((copied, failed), (0, 0))
        self.assertEqual(len(backed_up), 1)  # файл сохранён — его можно удалять с карты
        self.assertEqual(names, ["gpsdebug.txt", "gpsdebug_20261006_104834.txt"])

    def test_changed_file_still_gets_a_new_timestamped_copy(self):
        (copied, _bytes, backed_up, failed), names, _ = self._run(
            {"gpsdebug_20261006_104834.txt": (200, 1_789_990_000)})
        self.assertEqual((copied, failed), (1, 0))
        self.assertIn("gpsdebug_20261006_150000.txt", names)

    def test_similar_names_do_not_count(self):
        # Только «имя_ГГГГММДД_ЧЧММСС.расш»: чужой файл с похожим именем не копия.
        (copied, *_), names, _ = self._run({"gpsdebug_old.txt": (300, 1_790_000_000)})
        self.assertEqual(copied, 1)


class BusyStationRetryTest(unittest.TestCase):
    def test_retries_while_busy_are_not_logged_as_new_devices(self):
        um._log_once_last.clear()
        stop = um.threading.Event()
        calls = []

        def copy(dev, *args, **kwargs):
            calls.append(dev)
            if len(calls) <= 5:
                raise um.StationBusy("Станция занята другой операцией")
            stop.set()
            return None, 0, 0

        out = io.StringIO()
        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(out), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
             mock.patch.object(um, "MOUNT_BASE", os.path.join(tmp, "mnt")), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "c.json")), \
             mock.patch.object(um, "_get_linux_partitions", return_value={"sdd": None}), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value=None), \
             mock.patch.object(um, "_repair_archive_ownership"), \
             mock.patch.object(um, "copy_task_linux", side_effect=copy):
            um.monitor_usb(interval=0.01, stop_event=stop, progress_queue=queue.Queue())
        text = out.getvalue()
        self.assertGreater(len(calls), 5)
        self.assertNotIn("Новое USB-устройство: sdd", text)
        self.assertEqual(text.count("Станция занята"), 1, text)


if __name__ == "__main__":
    unittest.main()
