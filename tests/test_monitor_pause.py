import tempfile
import threading
import unittest
from unittest import mock
from concurrent.futures import Future
import usb_monitor as um


class MonitorPauseTest(unittest.TestCase):
    def test_existing_and_new_cards_wait_for_explicit_resume(self):
        stop = threading.Event()
        submitted = []
        tick = [0]

        def submit(executor, dev, *args):
            submitted.append((tick[0], dev))
            future = Future()
            future.set_result(None)
            return future

        def sleep(interval):
            tick[0] += 1
            if tick[0] == 2:
                self.assertEqual(submitted, [])
                um.set_safe_removal(False)
            if tick[0] == 3:
                stop.set()

        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(um, "_safe_removal", True), \
             mock.patch.object(um.platform, "system", return_value="Linux"), \
             mock.patch.object(um, "_init_db"), \
             mock.patch.object(um, "_config_backup_dest", return_value=None), \
             mock.patch.object(um, "get_dest_base", return_value=directory), \
             mock.patch.object(um, "_archive_path_allowed", return_value=False), \
             mock.patch.object(um, "MOUNT_BASE", directory), \
             mock.patch.object(um, "_get_linux_partitions", side_effect=lambda: {"existing": None} if tick[0] == 0 else {"existing": None, "new": None}), \
             mock.patch.object(um, "_make_submit_fn", return_value=submit), \
             mock.patch.object(um.time, "sleep", side_effect=sleep):
            um.monitor_usb(stop_event=stop)
        self.assertEqual(submitted, [(2, "existing"), (2, "new")])

    def test_resume_requeues_interrupted_card_but_not_completed_card(self):
        import os
        stop = threading.Event()
        finished = threading.Event()
        counts = {'done': 0, 'paused': 0}
        tick = [0]

        def worker(dev, *args):
            counts[dev] += 1
            if dev == 'paused' and counts[dev] == 1:
                um.set_safe_removal(True)
            else:
                um._worker_local.completed = True
            finished.set()
            return 1, 0, 0

        def sleep(interval):
            tick[0] += 1
            self.assertTrue(finished.wait(3))
            for future in tuple(um._submitted_jobs):
                future.result(timeout=3)
            if tick[0] == 1:
                um.set_safe_removal(False)
            else:
                stop.set()

        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(um, 'DB_PATH', os.path.join(directory, 'db')), \
             mock.patch.object(um, '_safe_removal', False), \
             mock.patch.object(um, '_interrupted_devices', set()), \
             mock.patch.object(um, 'MAX_WORKERS', 1), \
             mock.patch.object(um.platform, 'system', return_value='Linux'), \
             mock.patch.object(um, '_init_db'), \
             mock.patch.object(um, '_config_backup_dest', return_value=None), \
             mock.patch.object(um, 'get_dest_base', return_value=directory), \
             mock.patch.object(um, '_archive_path_allowed', return_value=False), \
             mock.patch.object(um, 'MOUNT_BASE', directory), \
             mock.patch.object(um, '_get_linux_partitions', return_value={'done': None, 'paused': None}), \
             mock.patch.object(um, '_copy_task_linux', side_effect=worker), \
             mock.patch.object(um.time, 'sleep', side_effect=sleep):
            um.monitor_usb(stop_event=stop)
        self.assertEqual(counts, {'done': 1, 'paused': 2})

    def test_initially_paused_mounted_source_requires_system_eject(self):
        stop = threading.Event()
        def sleep(interval):
            self.assertEqual(um.safe_removal_status()[0], 'blocked')
            stop.set()
        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(um, '_safe_removal', True), \
             mock.patch.object(um, '_foreign_mounts', set()), \
             mock.patch.object(um.platform, 'system', return_value='Linux'), \
             mock.patch.object(um, '_init_db'), \
             mock.patch.object(um, '_config_backup_dest', return_value=None), \
             mock.patch.object(um, 'get_dest_base', return_value=directory), \
             mock.patch.object(um, '_archive_path_allowed', return_value=False), \
             mock.patch.object(um, '_is_dest_path', return_value=False), \
             mock.patch.object(um.os.path, 'ismount', return_value=True), \
             mock.patch.object(um, 'MOUNT_BASE', directory), \
             mock.patch.object(um, '_get_linux_partitions', return_value={'card': '/media/card'}), \
             mock.patch.object(um.time, 'sleep', side_effect=sleep):
            um.monitor_usb(stop_event=stop)
