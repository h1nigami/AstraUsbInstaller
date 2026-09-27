import json
import os
import sqlite3
import tempfile
import unittest
from unittest import mock
import usb_monitor as um


class ReviewCoreTest(unittest.TestCase):
    def test_reset_rejects_unmarked_destination_before_database_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            db = os.path.join(directory, 'devices.db')
            with sqlite3.connect(db) as conn:
                conn.execute('CREATE TABLE devices(id INTEGER)')
                conn.execute('INSERT INTO devices VALUES (1)')
            conn.close()
            personal = os.path.join(directory, 'personal.txt')
            with open(personal, 'w') as stream:
                stream.write('keep')
            with mock.patch.object(um, 'DB_PATH', db), mock.patch.object(um, 'is_copying', return_value=False):
                with self.assertRaises(OSError):
                    um.factory_reset(db, directory)
            self.assertTrue(os.path.exists(personal))
            with sqlite3.connect(db) as conn:
                self.assertEqual(conn.execute('SELECT COUNT(*) FROM devices').fetchone()[0], 1)
            conn.close()

    def test_corrupt_config_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'config.json')
            with open(path, 'w') as stream:
                stream.write('{broken')
            self.assertTrue(um._load_config(path).get('_config_unreadable'))
            self.assertFalse(um.update_config({'exit_password': 'exit'}, path=path))
            with open(path) as stream:
                self.assertEqual(stream.read(), '{broken')

    def test_config_updates_merge_and_remove_only_requested_keys(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'config.json')
            self.assertTrue(um.update_config({'backup_dest': 'disk', 'old': 1}, path=path))
            self.assertTrue(um.update_config({'exit_password': 'secret'}, remove_keys=('old',), path=path))
            self.assertEqual(um._load_config(path), {'backup_dest': 'disk', 'exit_password': 'secret'})

class SafeRemovalReviewTest(unittest.TestCase):
    def tearDown(self):
        um.set_safe_removal(False)

    def test_queued_worker_cannot_start_after_pause(self):
        import threading
        from concurrent.futures import ThreadPoolExecutor
        gate = threading.Event()
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(um, 'DB_PATH', os.path.join(directory, 'db')), \
             mock.patch.object(um.platform, 'system', return_value='Linux'), mock.patch.object(um, 'copy_task_linux') as worker:
            with ThreadPoolExecutor(max_workers=1) as executor:
                occupied = executor.submit(gate.wait)
                try:
                    future = um._make_submit_fn()(executor, 'sdb1', None, None, None)
                    um.set_safe_removal(True)
                    self.assertEqual(um.safe_removal_status()[0], 'stopping')
                finally:
                    gate.set()
                future.result(timeout=3)
                worker.assert_not_called()
                self.assertEqual(um.safe_removal_status()[0], 'ready')

    def test_wait_for_return_obeys_pause(self):
        um.set_safe_removal(True)
        with mock.patch.object(um, '_find_card_by_uuid', return_value='sdb1'):
            self.assertIsNone(um._wait_for_card('uuid', 1))

    def test_unmount_failure_blocks_safe_removal(self):
        import subprocess
        with mock.patch.object(um.subprocess, 'run', side_effect=subprocess.CalledProcessError(32, 'umount')), \
             mock.patch.object(um.os.path, 'ismount', return_value=True):
            self.assertFalse(um._unmount('/mnt/test-busy'))
            um.set_safe_removal(True)
            self.assertEqual(um.safe_removal_status()[0], 'blocked')
        with mock.patch.object(um.os.path, 'ismount', return_value=False):
            self.assertEqual(um.safe_removal_status()[0], 'ready')

    def test_explicit_config_repair_failure_preserves_corrupt_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, 'config.json')
            with open(path, 'w') as stream:
                stream.write('{broken')
            with mock.patch.object(um.os, 'replace', side_effect=OSError('disk full')):
                self.assertFalse(um.update_config({'exit_password': 'secret'}, path=path, repair=True))
            with open(path) as stream:
                self.assertEqual(stream.read(), '{broken')

class DestructiveArchiveReviewTest(unittest.TestCase):
    def test_cleanup_refuses_system_destination_and_preserves_video(self):
        with tempfile.TemporaryDirectory() as directory:
            video = os.path.join(directory, 'old.mp4')
            with open(video, 'w') as stream:
                stream.write('keep')
            os.utime(video, (0, 0))
            with mock.patch.object(um, '_require_archive_device', side_effect=OSError('system disk')):
                with self.assertRaises(OSError):
                    um.cleanup_old_backup_videos(directory)
            self.assertTrue(os.path.exists(video))

    def test_manual_cleanup_only_deletes_selected_device_videos(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(um, '_require_archive_device'):
            for name in ('Device1', 'Device2', 'personal'):
                os.mkdir(os.path.join(directory, name))
                for filename in ('clip.mp4', 'photo.jpg'):
                    with open(os.path.join(directory, name, filename), 'w') as stream:
                        stream.write('keep')
            with open(os.path.join(directory, um.DEST_MARKER_FILE), 'w'):
                pass
            self.assertEqual(um.cleanup_old_backup_videos(directory, None, device_id=1), (1, 4))
            self.assertFalse(os.path.exists(os.path.join(directory, 'Device1', 'clip.mp4')))
            for relative in ('Device1/photo.jpg', 'Device2/clip.mp4', 'personal/clip.mp4'):
                self.assertTrue(os.path.exists(os.path.join(directory, relative)))

class LogStreamingReviewTest(unittest.TestCase):
    def test_latest_log_is_read_as_stream_and_last_id_wins(self):
        import io
        class Stream(io.StringIO):
            def readlines(self, *args):
                raise AssertionError('log must not be loaded at once')
        with tempfile.TemporaryDirectory() as directory:
            os.mkdir(os.path.join(directory, 'LOG'))
            path = os.path.join(directory, 'LOG', 'boot.txt')
            with open(path, 'w'):
                pass
            with mock.patch('builtins.open', return_value=Stream('#ID:1\nstatus\n#ID:2\n')):
                self.assertEqual(um._id_from_latest_log(directory), 2)

class ConfigTypeReviewTest(unittest.TestCase):
    def test_invalid_cleanup_flag_and_destination_types_fail_closed(self):
        for cfg in ({'auto_cleanup_enabled': 'false'}, {'backup_dest': []}, {'backup_fs_uuid': 42}):
            with self.subTest(cfg=cfg), tempfile.TemporaryDirectory() as directory:
                path = os.path.join(directory, 'config.json')
                with open(path, 'w') as stream:
                    json.dump(cfg, stream)
                self.assertTrue(um._load_config(path).get('_config_unreadable'))
