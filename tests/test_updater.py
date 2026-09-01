"""Tests for the release-payload logic of updater.py. No network here —
downloading and applying are exercised on the dock station."""

import hashlib
import os
import sys
import tempfile
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


class Sha256Test(unittest.TestCase):
    def test_matches_hashlib(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "blob.bin")
            payload = b"astra" * 100000
            with open(path, "wb") as f:
                f.write(payload)
            self.assertEqual(updater.sha256_of(path),
                             hashlib.sha256(payload).hexdigest())

    def test_reads_checksum_field_from_sha_file(self):
        # Строка формата GitHub: "<hex>  <filename>"
        self.assertEqual(
            updater.parse_sha256("abc123  astra-usb-monitor-v1.2.tar.gz\n"),
            "abc123")

    def test_parse_sha256_handles_bare_hex(self):
        self.assertEqual(updater.parse_sha256("abc123\n"), "abc123")


class BackupAppDirTest(unittest.TestCase):
    """Резервная копия кода не должна утаскивать data/ и USB_Backups/."""

    def _make_app_dir(self, root):
        app_dir = os.path.join(root, "app")
        os.makedirs(os.path.join(app_dir, "data"))
        os.makedirs(os.path.join(app_dir, "USB_Backups", "Device1"))
        os.makedirs(os.path.join(app_dir, "__pycache__"))
        with open(os.path.join(app_dir, "usb_monitor.py"), "w") as f:
            f.write("код")
        with open(os.path.join(app_dir, "data", "devices.db"), "w") as f:
            f.write("база устройств")
        with open(os.path.join(app_dir, "USB_Backups", "Device1", "video.mp4"), "w") as f:
            f.write("гигабайты видео")
        with open(os.path.join(app_dir, "__pycache__", "usb_monitor.cpython-311.pyc"), "w") as f:
            f.write("байткод")
        return app_dir

    def test_copy_excludes_data_and_backups_but_keeps_code(self):
        with tempfile.TemporaryDirectory() as root:
            app_dir = self._make_app_dir(root)
            prev_dir = os.path.join(root, "app.prev")

            updater._backup_app_dir(app_dir, prev_dir)

            self.assertTrue(os.path.exists(os.path.join(prev_dir, "usb_monitor.py")))
            self.assertFalse(os.path.exists(os.path.join(prev_dir, "data")))
            self.assertFalse(os.path.exists(os.path.join(prev_dir, "USB_Backups")))
            self.assertFalse(os.path.exists(os.path.join(prev_dir, "__pycache__")))

    def test_removes_stale_previous_copy_first(self):
        with tempfile.TemporaryDirectory() as root:
            app_dir = self._make_app_dir(root)
            prev_dir = os.path.join(root, "app.prev")
            os.makedirs(prev_dir)
            with open(os.path.join(prev_dir, "leftover.py"), "w") as f:
                f.write("старьё от прошлого отката")

            updater._backup_app_dir(app_dir, prev_dir)

            self.assertFalse(os.path.exists(os.path.join(prev_dir, "leftover.py")))


class RestoreAppDirTest(unittest.TestCase):
    """Откат возвращает код из копии, но не трогает app_dir целиком и не
    удаляет то, чего в копии никогда не было (data/, USB_Backups/)."""

    def test_restores_code_without_touching_data_or_app_dir(self):
        with tempfile.TemporaryDirectory() as root:
            app_dir = os.path.join(root, "app")
            prev_dir = os.path.join(root, "app.prev")
            os.makedirs(os.path.join(app_dir, "data"))
            os.makedirs(prev_dir)

            with open(os.path.join(app_dir, "data", "devices.db"), "w") as f:
                f.write("история устройств — не трогать")
            with open(os.path.join(app_dir, "usb_monitor.py"), "w") as f:
                f.write("новая версия, которая не поднялась")
            with open(os.path.join(prev_dir, "usb_monitor.py"), "w") as f:
                f.write("старая рабочая версия")

            updater._restore_app_dir(prev_dir, app_dir)

            with open(os.path.join(app_dir, "usb_monitor.py")) as f:
                self.assertEqual(f.read(), "старая рабочая версия")
            with open(os.path.join(app_dir, "data", "devices.db")) as f:
                self.assertEqual(f.read(), "история устройств — не трогать")
            self.assertTrue(os.path.isdir(app_dir))
