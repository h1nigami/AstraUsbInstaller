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

    def test_resolves_same_destination_disk_under_new_mountpoint(self):
        with tempfile.TemporaryDirectory() as d, \
             tempfile.TemporaryDirectory() as mount_root:
            resolved = os.path.join(mount_root, "backups")
            os.makedirs(resolved)
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({
                    "backup_dest": "/run/user/1000/media/OLD/backups",
                    "backup_mount_relpath": "backups",
                    "backup_fs_uuid": "UUID-1",
                }, f)
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "_iter_mounts", return_value=[("/dev/sdb1", mount_root)]), \
                 mock.patch.object(um, "_get_filesystem_uuid", return_value="UUID-1"):
                self.assertEqual(um.get_dest_base(), resolved)

    def test_describe_dest_path_captures_relative_path_and_device_identity(self):
        with tempfile.TemporaryDirectory() as mount_root:
            dest = os.path.join(mount_root, "nested", "backups")
            os.makedirs(dest)
            with mock.patch.object(um, "_find_mount_for_path", return_value=("/dev/sdb1", mount_root)), \
                 mock.patch.object(um, "_get_filesystem_uuid", return_value="UUID-2"), \
                 mock.patch.object(um, "_get_device_serial_linux", return_value="SER-2"):
                info = um.describe_dest_path(dest)
        self.assertEqual(info["backup_dest"], dest)
        self.assertEqual(info["backup_mount_relpath"], os.path.join("nested", "backups"))
        self.assertEqual(info["backup_fs_uuid"], "UUID-2")
        self.assertEqual(info["backup_device_serial"], "SER-2")


class DestMarkerTest(unittest.TestCase):
    """The marker file distinguishes the really selected destination from a
    same-named shadow directory recreated on the root filesystem after the
    destination disk was unmounted (the "interface says OK, disk is empty"
    bug)."""

    def test_ensure_dest_marker_creates_file(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertTrue(um.ensure_dest_marker(d))
            self.assertTrue(os.path.isfile(os.path.join(d, um.DEST_MARKER_FILE)))

    def test_ensure_dest_marker_fails_on_unwritable_path(self):
        self.assertFalse(um.ensure_dest_marker("/no/such/dir"))

    def test_dest_available_without_configured_dest(self):
        with tempfile.TemporaryDirectory() as d:
            cfg_path = os.path.join(d, "missing.json")
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path):
                self.assertTrue(um.dest_available(), "env/default dest keeps legacy behaviour")

    def _with_config(self, d, dest):
        cfg_path = os.path.join(d, "config.json")
        with open(cfg_path, "w") as f:
            json.dump({"backup_dest": dest}, f)
        return mock.patch.object(um, "_CONFIG_PATH", cfg_path)

    def test_dest_available_with_marker(self):
        with tempfile.TemporaryDirectory() as d:
            dest = os.path.join(d, "disk")
            os.makedirs(dest)
            um.ensure_dest_marker(dest)
            with self._with_config(d, dest):
                self.assertTrue(um.dest_available())

    def test_dest_unavailable_when_dir_missing(self):
        with tempfile.TemporaryDirectory() as d:
            with self._with_config(d, os.path.join(d, "gone")):
                self.assertFalse(um.dest_available())

    def test_dest_unavailable_when_marker_missing(self):
        # Directory exists but has no marker: this is exactly the shadow
        # directory makedirs used to recreate after the disk was unmounted.
        with tempfile.TemporaryDirectory() as d:
            shadow = os.path.join(d, "disk")
            os.makedirs(shadow)
            with self._with_config(d, shadow):
                self.assertFalse(um.dest_available())

    def test_dest_available_when_real_disk_is_resolved_under_new_mountpoint(self):
        with tempfile.TemporaryDirectory() as d, \
             tempfile.TemporaryDirectory() as mount_root:
            resolved = os.path.join(mount_root, "backups")
            os.makedirs(resolved)
            um.ensure_dest_marker(resolved)
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({
                    "backup_dest": "/run/user/1000/media/OLD/backups",
                    "backup_mount_relpath": "backups",
                    "backup_fs_uuid": "UUID-3",
                }, f)
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "_iter_mounts", return_value=[("/dev/sdb1", mount_root)]), \
                 mock.patch.object(um, "_get_filesystem_uuid", return_value="UUID-3"):
                self.assertTrue(um.dest_available())


class IsDestPathTest(unittest.TestCase):
    def test_exact_mountpoint_match(self):
        with mock.patch.object(um, "get_dest_base", return_value="/mnt/usb_backup/sdb1"):
            self.assertTrue(um._is_dest_path("/mnt/usb_backup/sdb1"))

    def test_dest_inside_mountpoint(self):
        with mock.patch.object(um, "get_dest_base", return_value="/mnt/usb_backup/sdb1/backups"):
            self.assertTrue(um._is_dest_path("/mnt/usb_backup/sdb1"))

    def test_sibling_name_prefix_is_not_a_match(self):
        # sdb11 must not be treated as hosting a dest at sdb1.
        with mock.patch.object(um, "get_dest_base", return_value="/mnt/usb_backup/sdb11"):
            self.assertFalse(um._is_dest_path("/mnt/usb_backup/sdb1"))

    def test_unrelated_path(self):
        with mock.patch.object(um, "get_dest_base", return_value="/app/USB_Backups"):
            self.assertFalse(um._is_dest_path("/mnt/usb_backup/sdb1"))

    def test_resolved_dest_under_app_mount_is_still_detected(self):
        with tempfile.TemporaryDirectory() as d, \
             tempfile.TemporaryDirectory() as mount_root:
            dest = os.path.join(mount_root, "backups")
            os.makedirs(dest)
            cfg_path = os.path.join(d, "config.json")
            with open(cfg_path, "w") as f:
                json.dump({
                    "backup_dest": "/run/user/1000/media/OLD/backups",
                    "backup_mount_relpath": "backups",
                    "backup_fs_uuid": "UUID-4",
                }, f)
            with mock.patch.object(um, "_CONFIG_PATH", cfg_path), \
                 mock.patch.object(um, "_iter_mounts", return_value=[("/dev/sdb1", mount_root)]), \
                 mock.patch.object(um, "_get_filesystem_uuid", return_value="UUID-4"):
                self.assertTrue(um._is_dest_path(mount_root))


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
