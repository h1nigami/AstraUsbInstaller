"""Automated regression tests for usb_monitor core logic.

Pure stdlib (unittest) so they run without pytest or a GUI/X11 display:

    python3 -m unittest discover -s tests -v
    python3 tests/test_usb_monitor.py
"""

import os
import sys
import time
import tempfile
import unittest
from datetime import datetime

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class FormatHelpersTest(unittest.TestCase):
    def test_format_size_units(self):
        self.assertEqual(um._format_size(0), "0.0 B")
        self.assertEqual(um._format_size(512), "512.0 B")
        self.assertEqual(um._format_size(1024), "1.0 KB")
        self.assertEqual(um._format_size(1024 ** 2), "1.0 MB")
        self.assertEqual(um._format_size(1024 ** 3), "1.0 GB")

    def test_format_time(self):
        self.assertEqual(um._format_time(0), "0:00")
        self.assertEqual(um._format_time(65), "1:05")
        self.assertEqual(um._format_time(3661), "1:01:01")


class FilterDateTest(unittest.TestCase):
    """Regression for the search date-filter separator bug.

    started_at is stored via datetime.isoformat() ('T' separator). The filter
    must use the same separator or the upper-bound comparison wrongly excludes
    same-day backups (space 0x20 sorts before 'T' 0x54).
    """

    def test_uses_T_separator(self):
        self.assertEqual(
            um.format_filter_dt("2026", "06", "30", "15", "00", "59"),
            "2026-06-30T15:00:59",
        )

    def test_same_day_backup_within_range(self):
        stored = datetime(2026, 6, 30, 10, 0, 0).isoformat()  # how the DB stores it
        dt_from = um.format_filter_dt("2026", "06", "29", "15", "00", "00")
        dt_to = um.format_filter_dt("2026", "06", "30", "15", "00", "59")
        # Lexicographic comparison is exactly what the SQL WHERE clause does.
        self.assertTrue(stored >= dt_from)
        self.assertTrue(stored <= dt_to, "same-day backup must fall inside the range")

    def test_backup_after_upper_bound_excluded(self):
        stored = datetime(2026, 6, 30, 20, 0, 0).isoformat()
        dt_to = um.format_filter_dt("2026", "06", "30", "15", "00", "59")
        self.assertFalse(stored <= dt_to)


class LsblkParseTest(unittest.TestCase):
    def test_usb_disk_with_partitions_not_duplicated(self):
        data = {"blockdevices": [
            {"name": "sda", "tran": "usb", "type": "disk", "children": [
                {"name": "sda1", "type": "part"},
                {"name": "sda2", "type": "part"},
            ]}
        ]}
        self.assertEqual(um._parse_lsblk_tree(data), ["sda1", "sda2"])

    def test_usb_whole_disk_no_partitions(self):
        data = {"blockdevices": [
            {"name": "sdb", "tran": "usb", "type": "disk"}
        ]}
        self.assertEqual(um._parse_lsblk_tree(data), ["sdb"])

    def test_non_usb_disk_ignored(self):
        data = {"blockdevices": [
            {"name": "nvme0n1", "tran": "nvme", "type": "disk", "children": [
                {"name": "nvme0n1p1", "type": "part"},
            ]}
        ]}
        self.assertEqual(um._parse_lsblk_tree(data), [])

    def test_usb_whole_disk_with_non_partition_child(self):
        # Whole-disk container (e.g. LUKS) — no partition table, child is not
        # a 'part'. The disk itself must still be returned (mountable target).
        data = {"blockdevices": [
            {"name": "sdd", "tran": "usb", "type": "disk", "children": [
                {"name": "sdd_crypt", "type": "crypt"},
            ]}
        ]}
        self.assertEqual(um._parse_lsblk_tree(data), ["sdd"])

    def test_partition_inherits_usb_from_parent(self):
        # Some lsblk versions only set tran on the disk, not the partition.
        data = {"blockdevices": [
            {"name": "sdc", "tran": "usb", "type": "disk", "children": [
                {"name": "sdc1", "type": "part", "tran": None},
            ]}
        ]}
        self.assertEqual(um._parse_lsblk_tree(data), ["sdc1"])


class ScanDriveTest(unittest.TestCase):
    def test_scan_skips_astra_id_marker(self):
        with tempfile.TemporaryDirectory() as d:
            with open(os.path.join(d, "a.txt"), "wb") as f:
                f.write(b"x" * 100)
            with open(os.path.join(d, um.DEVICE_ID_FILE), "w") as f:
                f.write("5\n")
            total_files, total_bytes = um._scan_drive(d)
            self.assertEqual(total_files, 1)
            self.assertEqual(total_bytes, 100)


class CopyAndDeleteTest(unittest.TestCase):
    def _copy(self, src, dst):
        return um._copy_files(src, dst, "20260630_120000", 1, 0, 0,
                              None, None, time.time())

    def test_copy_files_reports_backed_up_paths(self):
        with tempfile.TemporaryDirectory() as src, tempfile.TemporaryDirectory() as dst:
            f1 = os.path.join(src, "video.mp4")
            with open(f1, "wb") as f:
                f.write(b"data")
            with open(os.path.join(src, um.DEVICE_ID_FILE), "w") as f:
                f.write("1\n")
            copied_files, copied_bytes, backed_up = self._copy(src, dst)
            self.assertEqual(copied_files, 1)
            self.assertEqual(copied_bytes, 4)
            self.assertIn(f1, backed_up)
            self.assertTrue(os.path.exists(os.path.join(dst, "video.mp4")))

    def test_identical_file_counts_as_backed_up(self):
        with tempfile.TemporaryDirectory() as src, tempfile.TemporaryDirectory() as dst:
            f1 = os.path.join(src, "clip.mov")
            with open(f1, "wb") as f:
                f.write(b"same")
            # First pass copies it.
            self._copy(src, dst)
            # Second pass: identical (size+mtime) -> skipped but still "backed up".
            copied_files, _bytes, backed_up = self._copy(src, dst)
            self.assertEqual(copied_files, 0)
            self.assertIn(f1, backed_up)

    def test_delete_source_videos_only_allowed(self):
        with tempfile.TemporaryDirectory() as src:
            ok = os.path.join(src, "ok.mp4")
            failed = os.path.join(src, "failed.mp4")
            photo = os.path.join(src, "keep.jpg")
            for p in (ok, failed, photo):
                with open(p, "wb") as f:
                    f.write(b"x")
            # Simulate: only `ok` was successfully backed up.
            deleted = um._delete_source_videos(src, allowed={ok})
            self.assertEqual(deleted, 1)
            self.assertFalse(os.path.exists(ok), "backed-up video should be removed")
            self.assertTrue(os.path.exists(failed), "un-backed-up video MUST be preserved")
            self.assertTrue(os.path.exists(photo), "non-video must never be touched")

    def test_failed_copy_video_is_preserved(self):
        """End-to-end: a video that fails to copy is not deleted from source."""
        from unittest import mock
        with tempfile.TemporaryDirectory() as src, tempfile.TemporaryDirectory() as dst:
            good = os.path.join(src, "good.mp4")
            bad = os.path.join(src, "bad.mp4")
            with open(good, "wb") as f:
                f.write(b"good")
            with open(bad, "wb") as f:
                f.write(b"bad")

            real_copy2 = um.shutil.copy2

            def flaky_copy2(s, d, *a, **kw):
                if os.path.basename(s) == "bad.mp4":
                    raise OSError("simulated copy failure")
                return real_copy2(s, d, *a, **kw)

            with mock.patch.object(um.shutil, "copy2", side_effect=flaky_copy2):
                _cf, _cb, backed_up = self._copy(src, dst)

            um._delete_source_videos(src, backed_up)
            # good was copied -> eligible for deletion; bad failed -> preserved.
            self.assertNotIn(bad, backed_up)
            self.assertTrue(os.path.exists(bad), "failed-copy video must survive")
            self.assertFalse(os.path.exists(good), "successfully backed-up video is deleted")


if __name__ == "__main__":
    unittest.main(verbosity=2)
