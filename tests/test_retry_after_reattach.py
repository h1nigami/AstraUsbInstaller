"""Найдено на .41 6 октября: камера отдаёт карту позже USB-диска, монтирование
падает «не найден носитель», а когда карта появляется, ядро на миг убирает
и заново создаёт диск. Повтор после этого обязан состояться."""

import contextlib
import io
import os
import queue
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class RetryAfterReattachTest(unittest.TestCase):
    def test_device_that_blinks_out_once_is_still_retried(self):
        # Опрос: диск есть → на один опрос пропал (ядро пересоздаёт его) → снова есть.
        # Первый элемент забирает запуск (и первая попытка падает), второй —
        # первый опрос после неудачи: в нём диска нет.
        polls = iter([{"sdd": None}, {}, {"sdd": None}, {"sdd": None}, {"sdd": None}])
        stop = um.threading.Event()
        attempts = []

        def poll():
            item = next(polls, None)
            if item is None:
                stop.set()
                return {"sdd": None}
            return item

        def copy(dev, *args, **kwargs):
            attempts.append(dev)
            if len(attempts) == 1:
                raise OSError("Не удалось смонтировать sdd")
            return None, 0, 0

        with tempfile.TemporaryDirectory() as tmp, contextlib.redirect_stdout(io.StringIO()), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
             mock.patch.object(um, "MOUNT_BASE", os.path.join(tmp, "mnt")), \
             mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "c.json")), \
             mock.patch.object(um, "_get_linux_partitions", side_effect=poll), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value=None), \
             mock.patch.object(um, "_repair_archive_ownership"), \
             mock.patch.object(um, "_interrupted_devices", set()), \
             mock.patch.object(um, "copy_task_linux", side_effect=copy):
            um.monitor_usb(interval=0.2, stop_event=stop, progress_queue=queue.Queue())
        self.assertGreaterEqual(len(attempts), 2, "после неудачного монтирования повтора не было")


if __name__ == "__main__":
    unittest.main()
