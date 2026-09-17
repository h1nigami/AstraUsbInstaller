"""Tests for the release-payload logic of updater.py. No network here —
downloading and applying are exercised on the dock station."""

import hashlib
import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

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
        release = dict(RELEASE, assets=[RELEASE["assets"][0]])
        self.assertIsNone(updater.pick_asset(release))

    def test_none_when_no_assets(self):
        self.assertIsNone(updater.pick_asset({"assets": []}))

    def test_rejects_foreign_or_unpublished_release(self):
        for fields in ({"tag_name": "v2.0.3"}, {"tag_name": "v10.2"},
                       {"tag_name": "v1."}, {"tag_name": "v1.2/other"},
                       {"prerelease": True}, {"draft": True}):
            with self.subTest(fields=fields):
                self.assertIsNone(updater.pick_asset(dict(RELEASE, **fields)))

    def test_archive_must_match_release_tag(self):
        self.assertIsNone(updater.pick_asset(dict(RELEASE, tag_name="v1.3")))


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


class FailedTagTest(unittest.TestCase):
    """Тег провалившегося релиза переживает откат, чтобы апдейтер не
    пытался ставить его же на каждом следующем тике таймера."""

    def test_write_then_read_back(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "app.failed")
            updater._write_failed_tag("v1.3", path)
            self.assertEqual(updater._read_failed_tag(path), "v1.3")

    def test_read_missing_file_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "missing.failed")
            self.assertIsNone(updater._read_failed_tag(path))

    def test_read_unreadable_content_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            # Каталог вместо файла — open() падает, как и на битом файле.
            path = os.path.join(d, "app.failed")
            os.makedirs(path)
            self.assertIsNone(updater._read_failed_tag(path))

    def test_clear_removes_file(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "app.failed")
            updater._write_failed_tag("v1.3", path)
            updater._clear_failed_tag(path)
            self.assertIsNone(updater._read_failed_tag(path))

    def test_clear_missing_file_does_not_raise(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "missing.failed")
            updater._clear_failed_tag(path)


def _make_tarball():
    """Минимальный валидный tar.gz с одним корневым каталогом — как
    архив релиза после распаковки GitHub."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w:gz") as tar:
        data = b"#!/bin/bash\nexit 0\n"
        info = tarfile.TarInfo(name="src/install_native.sh")
        info.size = len(data)
        tar.addfile(info, io.BytesIO(data))
    return buf.getvalue()


class MainTest(unittest.TestCase):
    """main() связывает загрузку, сверку суммы, проверку простоя и память о
    провалившемся релизе. _fetch и _apply подменены моками — реальная сеть и
    реальная установка сюда не попадают, а контрольная сумма — единственный
    барьер между битой закачкой и запуском скачанного шелл-скрипта от root,
    поэтому именно её путь проверяется по-настоящему."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.failed_tag_path = os.path.join(self.tmp.name, "app.failed")
        patcher = mock.patch.object(updater, "FAILED_TAG_FILE", self.failed_tag_path)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _release_bytes(self, release=None):
        return json.dumps(release if release is not None else RELEASE).encode()

    def test_checksum_mismatch_skips_apply(self):
        responses = [self._release_bytes(), b"archive bytes", b"deadbeef  file.tar.gz\n"]
        with mock.patch.object(updater, "_fetch", side_effect=responses), \
             mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-07-17")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            rc = updater.main()
        apply_mock.assert_not_called()
        self.assertEqual(rc, 0)

    def test_valid_release_calls_apply(self):
        payload = _make_tarball()
        checksum = hashlib.sha256(payload).hexdigest()
        responses = [self._release_bytes(), payload, f"{checksum}  file.tar.gz\n".encode()]
        with mock.patch.object(updater, "_fetch", side_effect=responses), \
             mock.patch.object(updater, "_apply", return_value=0) as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-07-17")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            rc = updater.main()
        apply_mock.assert_called_once()
        self.assertEqual(rc, 0)

    def test_busy_skips_apply(self):
        with mock.patch.object(updater, "_fetch", return_value=self._release_bytes()), \
             mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-07-17")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=True):
            rc = updater.main()
        apply_mock.assert_not_called()
        self.assertEqual(rc, 0)

    def test_copying_started_during_download_skips_install(self):
        payload = _make_tarball()
        responses = [self._release_bytes(), payload,
                     hashlib.sha256(payload).hexdigest().encode()]
        with mock.patch.object(updater, "_fetch", side_effect=responses), \
             mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=None), \
             mock.patch.object(updater.usb_monitor, "is_copying", side_effect=[False, True]):
            self.assertEqual(updater.main(), 0)
        apply_mock.assert_not_called()

    def test_failed_tag_skips_without_further_fetch(self):
        updater._write_failed_tag(RELEASE["tag_name"], self.failed_tag_path)
        with mock.patch.object(updater, "_fetch", return_value=self._release_bytes()) as fetch_mock, \
             mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-07-17")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            rc = updater.main()
        fetch_mock.assert_called_once()  # только запрос релиза, дальше не пошли
        apply_mock.assert_not_called()
        self.assertEqual(rc, 0)

    def test_no_assets_skips_apply(self):
        release = dict(RELEASE, assets=[])
        with mock.patch.object(updater, "_fetch", return_value=self._release_bytes(release)), \
             mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-07-17")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            rc = updater.main()
        apply_mock.assert_not_called()
        self.assertEqual(rc, 0)


class MixedAssetsTest(unittest.TestCase):
    """В релизе могут оказаться сборки для двух платформ сразу. Точка на
    Python-версии обязана взять свой архив и сумму именно от него."""

    @staticmethod
    def _release(*names):
        return {"tag_name": "v1.9", "assets": [
            {"name": n, "browser_download_url": "https://example/" + n} for n in names]}

    def test_ignores_release_without_tarball(self):
        r = self._release("astra-usb-monitor-win-v1.9.zip",
                          "astra-usb-monitor-win-v1.9.zip.sha256")
        self.assertIsNone(updater.pick_asset(r))

    def test_picks_checksum_belonging_to_the_tarball(self):
        r = self._release("astra-usb-monitor-win-v1.9.zip",
                          "astra-usb-monitor-win-v1.9.zip.sha256",
                          "astra-usb-monitor-v1.9.tar.gz",
                          "astra-usb-monitor-v1.9.tar.gz.sha256")
        picked = updater.pick_asset(r)
        self.assertIsNotNone(picked)
        tarball, checksum = picked
        self.assertTrue(tarball.endswith("astra-usb-monitor-v1.9.tar.gz"))
        self.assertTrue(checksum.endswith("astra-usb-monitor-v1.9.tar.gz.sha256"),
                        "сумма должна принадлежать выбранному архиву, а не чужому")

    def test_ignores_cross_platform_archives(self):
        """В том же репозитории выходят релизы кроссплатформенной версии.
        Точка на Python не должна принимать их архивы за своё обновление:
        установщик у них другой, и такая «установка» сломала бы точку."""
        r = self._release("bestcam-station-v2.0-linux-x64.tar.gz",
                          "bestcam-station-v2.0-linux-x64.tar.gz.sha256",
                          "bestcam-station-v2.0-linux-arm64.tar.gz",
                          "bestcam-station-v2.0-linux-arm64.tar.gz.sha256")
        self.assertIsNone(updater.pick_asset(r))

    def test_takes_own_archive_from_a_mixed_release(self):
        r = self._release("bestcam-station-v2.0-linux-x64.tar.gz",
                          "bestcam-station-v2.0-linux-x64.tar.gz.sha256",
                          "astra-usb-monitor-v1.9.tar.gz",
                          "astra-usb-monitor-v1.9.tar.gz.sha256")
        picked = updater.pick_asset(r)
        self.assertIsNotNone(picked)
        tarball, checksum = picked
        self.assertTrue(tarball.endswith("astra-usb-monitor-v1.9.tar.gz"))
        self.assertTrue(checksum.endswith("astra-usb-monitor-v1.9.tar.gz.sha256"))

    def test_none_when_tarball_has_no_own_checksum(self):
        r = self._release("astra-usb-monitor-v1.9.tar.gz",
                          "astra-usb-monitor-win-v1.9.zip.sha256")
        self.assertIsNone(updater.pick_asset(r))


class ServiceHealthTest(unittest.TestCase):
    def test_requires_successful_restart_count_query(self):
        for code, count, healthy in ((0, "0\n", True), (0, "1\n", False),
                                     (1, "", False), (0, "", False),
                                     (1, "0\n", False)):
            with self.subTest(code=code, count=count), \
                 mock.patch.object(updater.subprocess, "run", side_effect=[
                     mock.Mock(returncode=0),
                     mock.Mock(returncode=code, stdout=count)]):
                self.assertEqual(updater._service_healthy(), healthy)


class ApplyTest(unittest.TestCase):
    def test_wrong_installed_version_restores_previous_code_and_preserves_data(self):
        with tempfile.TemporaryDirectory() as root:
            app = os.path.join(root, "app")
            previous = app + ".prev"
            os.makedirs(os.path.join(app, "data"))
            code = os.path.join(app, "main.py")
            database = os.path.join(app, "data", "devices.db")
            for path, content in ((code, "old"), (database, "history")):
                with open(path, "w") as stream:
                    stream.write(content)

            def run(command, **kwargs):
                if command[0] == "bash":
                    with open(code, "w") as stream:
                        stream.write("new")
                return mock.Mock(returncode=0)

            with mock.patch.multiple(updater, APP_DIR=app, PREV_DIR=previous,
                                     FAILED_TAG_FILE=app + ".failed"), \
                 mock.patch.object(updater.subprocess, "run", side_effect=run), \
                 mock.patch.object(updater.time, "sleep"), \
                 mock.patch.object(updater, "_service_healthy", return_value=True), \
                 mock.patch.object(updater.usb_monitor, "read_version", return_value=("v1.1", "2026-09-14")):
                result = updater._apply(root, "v1.2")
                self.assertEqual(updater._read_failed_tag(), "v1.2")
            self.assertEqual(result, 1)
            with open(code) as stream:
                self.assertEqual(stream.read(), "old")
            with open(database) as stream:
                self.assertEqual(stream.read(), "history")


def _write_offline(stick_dir, tag, payload=None, checksum=True, name=None):
    payload = payload if payload is not None else _make_tarball()
    name = name or f"astra-usb-monitor-{tag}.tar.gz"
    tarball = os.path.join(stick_dir, name)
    with open(tarball, "wb") as stream:
        stream.write(payload)
    if checksum:
        digest = hashlib.sha256(payload).hexdigest()
        with open(tarball + ".sha256", "w") as stream:
            stream.write(f"{digest}  {name}\n")
    return tarball


class OfflinePackageTest(unittest.TestCase):
    def test_find_ignores_non_archive_names(self):
        with tempfile.TemporaryDirectory() as stick:
            for name in ("readme.txt", "astra-usb-monitor-v2.0.tar.gz",
                         "astra-usb-monitor-v1.3.zip", "update.tar.gz"):
                with open(os.path.join(stick, name), "w") as stream:
                    stream.write("x")
            self.assertEqual(updater.find_offline_archives(stick), [])

    def test_find_picks_newest_first(self):
        with tempfile.TemporaryDirectory() as stick:
            for tag in ("v1.9", "v1.13", "v1.10"):
                _write_offline(stick, tag)
            found = updater.find_offline_archives(stick)
            self.assertEqual([tag for tag, _path in found],
                             ["v1.13", "v1.10", "v1.9"])

    def test_find_missing_dir_is_empty(self):
        self.assertEqual(updater.find_offline_archives("/nonexistent-dir-xyz"), [])

    def test_check_valid_package(self):
        with tempfile.TemporaryDirectory() as stick:
            tarball = _write_offline(stick, "v1.13")
            self.assertEqual(updater.check_offline_package(tarball), ("v1.13", None))

    def test_check_missing_checksum(self):
        with tempfile.TemporaryDirectory() as stick:
            tarball = _write_offline(stick, "v1.13", checksum=False)
            tag, error = updater.check_offline_package(tarball)
            self.assertIsNone(tag)
            self.assertIn(".sha256", error)

    def test_check_tampered_archive(self):
        with tempfile.TemporaryDirectory() as stick:
            tarball = _write_offline(stick, "v1.13")
            with open(tarball, "ab") as stream:
                stream.write(b"tampered")
            tag, error = updater.check_offline_package(tarball)
            self.assertIsNone(tag)
            self.assertTrue(error)


class OfflineSpoolTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.spool = os.path.join(self.tmp.name, "spool")
        self.failed_tag_path = os.path.join(self.tmp.name, "app.failed")
        self.failed_patcher = mock.patch.object(
            updater, "FAILED_TAG_FILE", self.failed_tag_path)
        self.failed_patcher.start()
        self.addCleanup(self.failed_patcher.stop)

    def _stage(self, tag="v1.13", current=("v1.12", "2026-09-15")):
        stick = os.path.join(self.tmp.name, "stick")
        os.makedirs(stick, exist_ok=True)
        tarball = _write_offline(stick, tag)
        staged, error = updater.stage_offline_package(tarball, self.spool)
        self.assertEqual((staged, error), (tag, None))
        return staged

    def test_main_applies_staged_package_and_clears_spool(self):
        self._stage()
        applied = {}

        def fake_apply(src_dir, tag):
            applied["tag"] = tag
            applied["has_installer"] = os.path.isfile(
                os.path.join(src_dir, "install_native.sh"))
            return 0

        with mock.patch.object(updater, "_apply", side_effect=fake_apply), \
             mock.patch.object(updater.usb_monitor, "read_version",
                               return_value=("v1.12", "2026-09-15")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            self.assertEqual(updater.main(spool_dir=self.spool), 0)
        self.assertEqual(applied["tag"], "v1.13")
        self.assertTrue(applied["has_installer"])
        self.assertFalse(os.path.exists(self.spool))

    def test_main_skips_same_version(self):
        self._stage(tag="v1.12")
        with mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version",
                               return_value=("v1.12", "2026-09-15")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            self.assertEqual(updater.main(spool_dir=self.spool), 0)
        apply_mock.assert_not_called()
        self.assertFalse(os.path.exists(self.spool))

    def test_main_defers_while_busy_and_keeps_spool(self):
        self._stage()
        with mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version",
                               return_value=("v1.12", "2026-09-15")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=True):
            self.assertEqual(updater.main(spool_dir=self.spool), 0)
        apply_mock.assert_not_called()
        self.assertTrue(os.path.isfile(os.path.join(self.spool, "release.tar.gz")))

    def test_main_skips_failed_tag(self):
        self._stage()
        updater._write_failed_tag("v1.13", self.failed_tag_path)
        with mock.patch.object(updater, "_apply") as apply_mock, \
             mock.patch.object(updater.usb_monitor, "read_version",
                               return_value=("v1.12", "2026-09-15")), \
             mock.patch.object(updater.usb_monitor, "is_copying", return_value=False):
            self.assertEqual(updater.main(spool_dir=self.spool), 0)
        apply_mock.assert_not_called()
        self.assertFalse(os.path.exists(self.spool))
