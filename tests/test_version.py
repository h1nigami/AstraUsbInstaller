"""Tests for reading the VERSION file that ties the install to a GitHub release."""

import os
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class ReadVersionTest(unittest.TestCase):
    def _write(self, d, text):
        path = os.path.join(d, "VERSION")
        with open(path, "w") as f:
            f.write(text)
        return path

    def test_parses_tag_and_date(self):
        with tempfile.TemporaryDirectory() as d:
            path = self._write(d, "v1.1 2026-07-17\n")
            self.assertEqual(um.read_version(path), ("v1.1", "2026-07-17"))

    def test_missing_file_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(um.read_version(os.path.join(d, "VERSION")))

    def test_garbage_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(um.read_version(self._write(d, "мусор")))

    def test_extra_whitespace_is_tolerated(self):
        with tempfile.TemporaryDirectory() as d:
            path = self._write(d, "  v2.0   2026-09-01  \n")
            self.assertEqual(um.read_version(path), ("v2.0", "2026-09-01"))

    def test_invalid_utf8_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "VERSION")
            with open(path, "wb") as f:
                f.write(b"\xff\xfe\x00binary")
            self.assertIsNone(um.read_version(path))


class CopyingMarkerTest(unittest.TestCase):
    def test_absent_marker_means_idle(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertFalse(um.is_copying(os.path.join(d, ".copying")))

    def test_fresh_marker_means_busy(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, ".copying")
            with mock.patch.object(um, "COPYING_MARKER", path):
                um.touch_copying_marker()
            self.assertTrue(um.is_copying(path))

    def test_stale_marker_means_idle(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, ".copying")
            with open(path, "w"):
                pass
            old = time.time() - 3600
            os.utime(path, (old, old))
            self.assertFalse(um.is_copying(path))
