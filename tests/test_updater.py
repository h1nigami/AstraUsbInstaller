"""Tests for the release-payload logic of updater.py. No network here —
downloading and applying are exercised on the dock station."""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import updater


RELEASE = {
    "tag_name": "v1.2",
    "published_at": "2026-08-20T10:00:00Z",
    "assets": [
        {"name": "astra-usb-monitor-v1.2.tar.gz",
         "browser_download_url": "https://example/app.tar.gz"},
        {"name": "astra-usb-monitor-v1.2.tar.gz.sha256",
         "browser_download_url": "https://example/app.sha256"},
    ],
}


class PickAssetTest(unittest.TestCase):
    def test_picks_tarball_and_checksum(self):
        self.assertEqual(updater.pick_asset(RELEASE),
                         ("https://example/app.tar.gz", "https://example/app.sha256"))

    def test_none_when_checksum_missing(self):
        release = {"assets": [RELEASE["assets"][0]]}
        self.assertIsNone(updater.pick_asset(release))

    def test_none_when_no_assets(self):
        self.assertIsNone(updater.pick_asset({"assets": []}))


class NeedsUpdateTest(unittest.TestCase):
    def test_same_tag_skips(self):
        self.assertFalse(updater.needs_update("v1.1", "v1.1"))

    def test_different_tag_updates(self):
        self.assertTrue(updater.needs_update("v1.1", "v1.2"))

    def test_unknown_local_version_updates(self):
        self.assertTrue(updater.needs_update(None, "v1.2"))

    def test_missing_latest_never_updates(self):
        self.assertFalse(updater.needs_update("v1.1", None))
