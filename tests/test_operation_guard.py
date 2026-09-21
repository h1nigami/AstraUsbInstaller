import os
import tempfile
import unittest
from unittest import mock
import usb_monitor as um


class OperationGuardTest(unittest.TestCase):
    def test_shared_copy_excludes_cleanup_and_reset(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(um, 'DB_PATH', os.path.join(directory, 'db')):
            with um.operation_guard():
                with self.assertRaises(OSError):
                    um.cleanup_old_backup_videos(directory)
                with self.assertRaises(OSError):
                    um.factory_reset(dest_base=directory)
            with um.operation_guard(exclusive=True):
                with self.assertRaises(OSError):
                    with um.operation_guard():
                        self.fail('worker admitted during maintenance')
            with um.operation_guard():
                pass

    @unittest.skipIf(os.name == 'nt', 'Linux flock')
    def test_external_process_cannot_lock_during_copy(self):
        import subprocess
        import sys
        script = 'import fcntl,sys; f=open(sys.argv[1], "a"); fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)'
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(um, 'DB_PATH', os.path.join(directory, 'db')):
            with um.operation_guard():
                blocked = subprocess.run([sys.executable, '-c', script, um.DB_PATH + '.operations.lock'], capture_output=True)
                self.assertNotEqual(blocked.returncode, 0)
            allowed = subprocess.run([sys.executable, '-c', script, um.DB_PATH + '.operations.lock'], capture_output=True)
            self.assertEqual(allowed.returncode, 0, allowed.stderr)
