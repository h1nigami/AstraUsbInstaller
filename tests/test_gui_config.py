"""Проверки настроек GUI без создания окна.

При отсутствии Tkinter набор пропускается; CI проверяет импорт явно.
"""

import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

try:
    import gui as gui_mod
    _HAS_TK = True
except Exception:
    gui_mod = None
    _HAS_TK = False


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class GuiConfigTest(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.TemporaryDirectory()
        self.cfg_path = os.path.join(self.tmpdir.name, "config.json")
        self._patcher = mock.patch.object(gui_mod, "CONFIG_PATH", self.cfg_path)
        self._patcher.start()

    def tearDown(self):
        self._patcher.stop()
        self.tmpdir.cleanup()

    def test_load_config_missing_file_returns_empty_dict(self):
        self.assertEqual(gui_mod._load_config(), {})

    def test_save_then_load_roundtrip(self):
        gui_mod._save_config({"a": 1})
        self.assertEqual(gui_mod._load_config(), {"a": 1})

    def test_get_exit_password_defaults_from_env_and_persists(self):
        with mock.patch.dict(os.environ, {"APP_EXIT_PASSWORD": "hunter2"}):
            pw = gui_mod._get_exit_password()
        self.assertEqual(pw, "hunter2")
        self.assertEqual(gui_mod._load_config()["exit_password"], "hunter2")

    def test_get_exit_password_prefers_saved_value_over_env(self):
        gui_mod._save_config({"exit_password": "saved"})
        with mock.patch.dict(os.environ, {"APP_EXIT_PASSWORD": "other"}):
            self.assertEqual(gui_mod._get_exit_password(), "saved")

    def test_set_exit_password_overwrites_and_preserves_other_keys(self):
        gui_mod._save_config({"backup_dest": "/x"})
        gui_mod._set_exit_password("newpw")
        cfg = gui_mod._load_config()
        self.assertEqual(cfg["exit_password"], "newpw")
        self.assertEqual(cfg["backup_dest"], "/x")


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class UpdateCheckTest(unittest.TestCase):
    class _Result:
        def __init__(self, returncode):
            self.returncode = returncode

    def test_successful_start(self):
        calls = []

        def fake_runner(cmd, timeout):
            calls.append(cmd)
            return self._Result(0)

        self.assertEqual(gui_mod._start_update_service(fake_runner),
                         "Проверка обновления запущена")
        self.assertEqual(calls[0][:3], ["systemctl", "start", "astra-usb-update.service"])

    def test_failed_start_reports_error(self):
        def fake_runner(cmd, timeout):
            return self._Result(1)

        self.assertEqual(gui_mod._start_update_service(fake_runner),
                         "Не удалось запустить проверку обновлений")

    def test_missing_systemd_reports_gracefully(self):
        def fake_runner(cmd, timeout):
            raise FileNotFoundError("systemctl")

        self.assertEqual(gui_mod._start_update_service(fake_runner),
                         "Проверка недоступна: нет systemd")


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class BusyMarkerTest(unittest.TestCase):
    """_is_busy drives the updater's busy marker — it must key off the raw
    state, not the localized label, so renaming a label can't silently
    disable it."""

    def test_idle_when_no_workers(self):
        self.assertFalse(gui_mod._is_busy({}))

    def test_busy_while_scanning_or_copying(self):
        self.assertTrue(gui_mod._is_busy({1: {"state_raw": "scanning"}}))
        self.assertTrue(gui_mod._is_busy({1: {"state_raw": "copying"}}))

    def test_idle_when_done_or_error(self):
        self.assertFalse(gui_mod._is_busy({1: {"state_raw": "done"}}))
        self.assertFalse(gui_mod._is_busy({1: {"state_raw": "error"}}))


if __name__ == "__main__":
    unittest.main(verbosity=2)


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class DetachedTileTest(unittest.TestCase):
    """При сбросе хаба карта возвращается под другим именем за пару секунд.
    Плитка обязана это пережить, иначе мигает вся стойка."""

    def test_returned_device_is_not_purged(self):
        data = {7: {"state_raw": "copying", "devname": "sdf"}}
        self.assertEqual(gui_mod._detached_too_long(data, now=1000.0), [])

    def test_detached_kept_within_grace(self):
        data = {7: {"state_raw": "detached", "detached_at": 1000.0}}
        self.assertEqual(gui_mod._detached_too_long(data, now=1005.0, grace=25), [])

    def test_detached_purged_after_grace(self):
        data = {7: {"state_raw": "detached", "detached_at": 1000.0}}
        self.assertEqual(gui_mod._detached_too_long(data, now=1030.0, grace=25), [7])
