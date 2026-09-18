"""Проверки настроек GUI без создания окна.

При отсутствии Tkinter набор пропускается; CI проверяет импорт явно.
"""

import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um

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
        # GUI больше не хранит своей копии чтения-записи конфигурации.
        self._patcher = mock.patch.object(um, "_CONFIG_PATH", self.cfg_path)
        self._patcher.start()

    def tearDown(self):
        self._patcher.stop()
        self.tmpdir.cleanup()

    def test_load_config_missing_file_returns_empty_dict(self):
        self.assertEqual(gui_mod._load_config(), {})

    def test_save_then_load_roundtrip(self):
        um._save_config({"a": 1})
        self.assertEqual(gui_mod._load_config(), {"a": 1})

    def test_get_exit_password_defaults_from_env_and_persists(self):
        with mock.patch.dict(os.environ, {"APP_EXIT_PASSWORD": "hunter2"}):
            pw = gui_mod._get_exit_password()
        self.assertEqual(pw, "hunter2")
        self.assertEqual(gui_mod._load_config()["exit_password"], "hunter2")

    def test_get_exit_password_prefers_saved_value_over_env(self):
        um._save_config({"exit_password": "saved"})
        with mock.patch.dict(os.environ, {"APP_EXIT_PASSWORD": "other"}):
            self.assertEqual(gui_mod._get_exit_password(), "saved")

    def test_set_exit_password_overwrites_and_preserves_other_keys(self):
        um._save_config({"backup_dest": "/x"})
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

        self.assertEqual(gui_mod._start_update_service(fake_runner, network_check=lambda: True),
                         "Проверка обновления запущена")
        self.assertEqual(calls[0][:3], ["systemctl", "start", "astra-usb-update.service"])

    def test_failed_start_reports_error(self):
        def fake_runner(cmd, timeout):
            return self._Result(1)

        self.assertEqual(gui_mod._start_update_service(fake_runner, network_check=lambda: True),
                         "Не удалось запустить проверку обновлений")

    def test_missing_systemd_reports_gracefully(self):
        def fake_runner(cmd, timeout):
            raise FileNotFoundError("systemctl")

        self.assertEqual(gui_mod._start_update_service(fake_runner, network_check=lambda: True),
                         "Проверка недоступна: нет systemd")

    def test_no_network_skips_systemctl_entirely(self):
        calls = []

        def fake_runner(cmd, timeout):
            calls.append(cmd)
            return self._Result(0)

        self.assertEqual(gui_mod._start_update_service(fake_runner, network_check=lambda: False),
                         "Нет подключения к интернету")
        self.assertEqual(calls, [])


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class DiagnosticsTest(unittest.TestCase):
    class _Result:
        def __init__(self, returncode, stdout=""):
            self.returncode = returncode
            self.stdout = stdout

    def _runner(self, *, service=("active", "enabled"), timer=("active", "enabled"),
                pgrep="", docker="", missing=()):
        def fake(cmd, **kw):
            if cmd[0] in missing:
                raise FileNotFoundError(cmd[0])
            if cmd[0] == "systemctl":
                action, unit = cmd[1], cmd[2]
                val = (service if "monitor" in unit else timer)[0 if action == "is-active" else 1]
                return self._Result(0 if val in ("active", "enabled") else 1, val + "\n")
            if cmd[0] == "pgrep":
                return self._Result(0 if pgrep else 1, pgrep)
            if cmd[0] == "docker":
                return self._Result(0, docker)
            raise AssertionError(cmd)
        return fake

    def _diag(self, **kw):
        pid = kw.pop("self_pid", 100)
        return gui_mod.run_diagnostics(runner=self._runner(**kw),
                                       system=lambda: "Linux", self_pid=pid)

    def test_non_linux_returns_single_skip(self):
        res = gui_mod.run_diagnostics(system=lambda: "Windows")
        self.assertEqual([s for s, _ in res], [gui_mod.DIAG_SKIP])

    def test_all_healthy(self):
        res = self._diag(pgrep="100 python3 -c from gui import launch; launch()\n")
        self.assertEqual([s for s, _ in res],
                         [gui_mod.DIAG_OK] * 4)

    def test_service_inactive_fails(self):
        res = self._diag(service=("inactive", "enabled"))
        self.assertEqual(res[0][0], gui_mod.DIAG_FAIL)

    def test_timer_active_but_disabled_warns(self):
        res = self._diag(timer=("active", "disabled"))
        self.assertEqual(res[1][0], gui_mod.DIAG_WARN)

    def test_second_copy_detected(self):
        res = self._diag(pgrep="100 python3 -c from gui import launch\n"
                               "200 python3 /opt/astra-usb-monitor/main.py\n")
        status, text = res[2]
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("200", text)

    def test_running_docker_container_fails(self):
        res = self._diag(docker="astra-usb-monitor Up 3 hours\n")
        status, text = res[3]
        self.assertEqual(status, gui_mod.DIAG_FAIL)
        self.assertIn("Up", text)

    def test_missing_systemd_and_docker_skip(self):
        res = self._diag(missing=("systemctl", "docker"))
        self.assertEqual(res[0][0], gui_mod.DIAG_SKIP)
        self.assertEqual(res[1][0], gui_mod.DIAG_SKIP)
        self.assertEqual(res[3][0], gui_mod.DIAG_SKIP)


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
