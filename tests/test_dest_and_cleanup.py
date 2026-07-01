"""Tests for backup-destination resolution, old-video cleanup, and the
Docker (non-TTY) progress line throttling in usb_monitor.py."""

import io
import os
import sys
import json
import time
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class GetDestBaseTest(unittest.TestCase):
    def test_uses_config_when_present(self):
        with tempfile.TemporaryDirectory() as d:
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({"backup_dest": "/custom/dest"}, f)
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path):
                self.assertEqual(um.get_dest_base(), "/custom/dest")

    def test_falls_back_to_env_when_config_missing(self):
        with tempfile.TemporaryDirectory() as d:
            cfg_path = os.path.join(d, "missing.json")
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.dict(os.environ, {"USB_BACKUP_DEST": "/env/dest"}):
                self.assertEqual(um.get_dest_base(), "/env/dest")

    def test_falls_back_to_env_when_config_has_no_dest_key(self):
        with tempfile.TemporaryDirectory() as d:
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({}, f)
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.dict(os.environ, {"USB_BACKUP_DEST": "/env/dest2"}):
                self.assertEqual(um.get_dest_base(), "/env/dest2")

    def test_falls_back_to_env_on_invalid_json(self):
        with tempfile.TemporaryDirectory() as d:
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                f.write("{not valid json")
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.dict(os.environ, {"USB_BACKUP_DEST": "/env/dest3"}):
                self.assertEqual(um.get_dest_base(), "/env/dest3")


class CleanupOldVideosTest(unittest.TestCase):
    def _touch(self, path, days_old, size=10):
        with open(path, "wb") as f:
            f.write(b"x" * size)
        old_time = time.time() - days_old * 86400
        os.utime(path, (old_time, old_time))

    def test_deletes_only_videos_past_threshold(self):
        with tempfile.TemporaryDirectory() as dest:
            old_video = os.path.join(dest, "old.mp4")
            new_video = os.path.join(dest, "new.mp4")
            old_photo = os.path.join(dest, "old.jpg")
            self._touch(old_video, 40)
            self._touch(new_video, 5)
            self._touch(old_photo, 40)
            deleted, freed = um.cleanup_old_backup_videos(dest, older_than_days=30)
            self.assertEqual(deleted, 1)
            self.assertEqual(freed, 10)
            self.assertFalse(os.path.exists(old_video), "old video must be removed")
            self.assertTrue(os.path.exists(new_video), "recent video must survive")
            self.assertTrue(os.path.exists(old_photo), "non-video must never be touched")

    def test_missing_dest_dir_returns_zero(self):
        self.assertEqual(um.cleanup_old_backup_videos("/no/such/dir", 30), (0, 0))

    def test_uses_get_dest_base_when_dest_is_none(self):
        with tempfile.TemporaryDirectory() as dest:
            video = os.path.join(dest, "a.mp4")
            self._touch(video, 40)
            with mock.patch.object(um, "get_dest_base", return_value=dest):
                deleted, _freed = um.cleanup_old_backup_videos(None, 30)
            self.assertEqual(deleted, 1)

    def test_recurses_into_subdirectories(self):
        with tempfile.TemporaryDirectory() as dest:
            sub = os.path.join(dest, "Device1", "20260101_000000")
            os.makedirs(sub)
            video = os.path.join(sub, "clip.mkv")
            self._touch(video, 40)
            deleted, _freed = um.cleanup_old_backup_videos(dest, 30)
            self.assertEqual(deleted, 1)
            self.assertFalse(os.path.exists(video))


class DockerProgressTest(unittest.TestCase):
    def setUp(self):
        um._docker_progress_cache.clear()

    def test_first_update_for_a_device_always_prints(self):
        buf = io.StringIO()
        with redirect_stdout(buf):
            um._docker_progress("Device1", 1, 10, 10, 100, "f.mp4", time.time())
        self.assertIn("Device1", buf.getvalue())

    def test_rapid_small_progress_deltas_are_throttled(self):
        start = time.time()
        buf = io.StringIO()
        with redirect_stdout(buf):
            um._docker_progress("Device1", 1, 10, 10, 100, "f.mp4", start)   # 10% -> prints
            um._docker_progress("Device1", 2, 10, 12, 100, "f2.mp4", start)  # 12%, <5pt delta -> suppressed
        lines = [l for l in buf.getvalue().splitlines() if l.strip()]
        self.assertEqual(len(lines), 1)

    def test_completion_line_always_prints(self):
        start = time.time()
        buf = io.StringIO()
        with redirect_stdout(buf):
            um._docker_progress("Device2", 5, 10, 50, 100, "f.mp4", start)      # 50%
            um._docker_progress("Device2", 10, 10, 100, 100, "done.mp4", start)  # 100%
        lines = [l for l in buf.getvalue().splitlines() if l.strip()]
        self.assertEqual(len(lines), 2, "a >=5pt jump (here: to completion) must not be throttled")

    def test_repeated_completion_calls_print_only_once(self):
        start = time.time()
        buf = io.StringIO()
        with redirect_stdout(buf):
            um._docker_progress("Device3", 10, 10, 100, 100, "done.mp4", start)
            um._docker_progress("Device3", 10, 10, 100, 100, "done.mp4", start)
        lines = [l for l in buf.getvalue().splitlines() if l.strip()]
        self.assertEqual(len(lines), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
