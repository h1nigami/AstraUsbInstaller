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

    # Порядок проверок в run_diagnostics — по ним читаются результаты.
    SERVICE, TIMER, V2, PROCS, HOLDERS, UDEV, JOURNAL, KERNEL, DOCKER = range(9)

    def _runner(self, *, service=("active", "enabled"), timer=("active", "enabled"),
                pgrep="", docker="", journal="", missing=()):
        def fake(cmd, **kw):
            if cmd[0] in missing:
                raise FileNotFoundError(cmd[0])
            if cmd[0] == "systemctl":
                if cmd[1] == "show":
                    return self._Result(0, "0\n")
                action, unit = cmd[1], cmd[2]
                if "avalonia" in unit:
                    return self._Result(1, "inactive\n")
                val = (service if "monitor" in unit else timer)[0 if action == "is-active" else 1]
                return self._Result(0 if val in ("active", "enabled") else 1, val + "\n")
            if cmd[0] == "pgrep":
                return self._Result(0 if pgrep else 1, pgrep)
            if cmd[0] == "docker":
                return self._Result(0, docker)
            if cmd[0] == "dpkg":
                return self._Result(1)
            if cmd[0] == "journalctl":
                return self._Result(0, journal)
            if cmd[0] == "fuser":
                return self._Result(1, "")
            raise AssertionError(cmd)
        return fake

    def _diag(self, **kw):
        pid = kw.pop("self_pid", 100)
        # Правило udev на месте, файлов второй версии нет — здоровая станция.
        return gui_mod.run_diagnostics(
            runner=self._runner(**kw), system=lambda: "Linux", self_pid=pid,
            exists=lambda path: path == gui_mod.DIAG_UDEV_RULE,
            mounts=["/mnt/usb_backup/sdb1"])

    def test_non_linux_returns_single_skip(self):
        res = gui_mod.run_diagnostics(system=lambda: "Windows")
        self.assertEqual([s for s, _ in res], [gui_mod.DIAG_SKIP])

    def test_all_healthy(self):
        res = self._diag(pgrep="100 python3 -c from gui import launch; launch()\n")
        self.assertEqual([s for s, _ in res],
                         [gui_mod.DIAG_OK] * 9)

    def test_service_inactive_fails(self):
        res = self._diag(service=("inactive", "enabled"))
        self.assertEqual(res[self.SERVICE][0], gui_mod.DIAG_FAIL)

    def test_timer_active_but_disabled_warns(self):
        res = self._diag(timer=("active", "disabled"))
        self.assertEqual(res[self.TIMER][0], gui_mod.DIAG_WARN)

    def test_second_copy_detected(self):
        res = self._diag(pgrep="100 python3 -c from gui import launch\n"
                               "200 python3 /opt/astra-usb-monitor/main.py\n")
        status, text = res[self.PROCS]
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("200", text)

    def test_running_docker_container_fails(self):
        res = self._diag(docker="astra-usb-monitor Up 3 hours\n")
        status, text = res[self.DOCKER]
        self.assertEqual(status, gui_mod.DIAG_FAIL)
        self.assertIn("Up", text)

    def test_missing_systemd_and_docker_skip(self):
        res = self._diag(missing=("systemctl", "docker"))
        self.assertEqual(res[self.SERVICE][0], gui_mod.DIAG_SKIP)
        self.assertEqual(res[self.TIMER][0], gui_mod.DIAG_SKIP)
        self.assertEqual(res[self.DOCKER][0], gui_mod.DIAG_SKIP)


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class DiagnosticsV2Test(unittest.TestCase):
    """Остатки второй версии: её служба тянет те же карты, что и наша."""

    class _Result:
        def __init__(self, returncode=0, stdout=""):
            self.returncode = returncode
            self.stdout = stdout

    def _runner(self, *, avalonia="inactive", package=False, missing=()):
        def fake(cmd, **kw):
            if cmd[0] in missing:
                raise FileNotFoundError(cmd[0])
            if cmd[0] == "systemctl":
                return self._Result(0, avalonia + "\n")
            if cmd[0] == "dpkg":
                return self._Result(0 if package else 1)
            raise AssertionError(cmd)
        return fake

    def _check(self, *, files=(), **kw):
        return gui_mod._diag_station_v2(self._runner(**kw), lambda p: p in files)

    def test_clean_system_is_ok(self):
        status, _ = self._check()
        self.assertEqual(status, gui_mod.DIAG_OK)

    def test_running_second_station_is_failure(self):
        status, text = self._check(avalonia="active")
        self.assertEqual(status, gui_mod.DIAG_FAIL)
        self.assertIn("устройства", text)

    def test_installed_package_without_service_warns(self):
        status, text = self._check(package=True)
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("bestcam-station", text)

    def test_leftover_unit_file_warns(self):
        status, _ = self._check(files=("/etc/systemd/system/astra-usb-avalonia.service",))
        self.assertEqual(status, gui_mod.DIAG_WARN)

    def test_leftover_directory_warns(self):
        status, _ = self._check(files=("/opt/astra-usb-avalonia",))
        self.assertEqual(status, gui_mod.DIAG_WARN)

    def test_missing_tools_skip(self):
        status, _ = self._check(missing=("systemctl", "dpkg"))
        self.assertEqual(status, gui_mod.DIAG_SKIP)


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class DiagnosticsHoldersTest(unittest.TestCase):
    """Кто ещё держит смонтированную флешку, кроме нас."""

    class _Result:
        def __init__(self, returncode=0, stdout=""):
            self.returncode = returncode
            self.stdout = stdout

    def _runner(self, *, fuser="", ps="", missing=()):
        def fake(cmd, **kw):
            if cmd[0] in missing:
                raise FileNotFoundError(cmd[0])
            if cmd[0] == "fuser":
                return self._Result(0 if fuser else 1, fuser)
            if cmd[0] == "ps":
                return self._Result(0, ps)
            raise AssertionError(cmd)
        return fake

    def _check(self, mounts=("/mnt/usb_backup/sdb1",), self_pid=100, **kw):
        return gui_mod._diag_mount_holders(self._runner(**kw), mounts, self_pid)

    def test_no_mounts_is_ok(self):
        status, _ = self._check(mounts=())
        self.assertEqual(status, gui_mod.DIAG_OK)

    def test_only_our_process_is_ok(self):
        status, _ = self._check(fuser="100\n")
        self.assertEqual(status, gui_mod.DIAG_OK)

    def test_foreign_holder_warns_with_name(self):
        status, text = self._check(fuser="100 431\n", ps="  431 nautilus\n")
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("nautilus", text)
        self.assertIn("/mnt/usb_backup/sdb1", text)

    def test_missing_fuser_skips(self):
        status, _ = self._check(missing=("fuser",))
        self.assertEqual(status, gui_mod.DIAG_SKIP)


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class DiagnosticsJournalTest(unittest.TestCase):
    """Журнал службы и ядра: считаем известные маркеры отказов."""

    class _Result:
        def __init__(self, returncode=0, stdout=""):
            self.returncode = returncode
            self.stdout = stdout

    def _runner(self, *, journal="", restarts="0", missing=()):
        def fake(cmd, **kw):
            if cmd[0] in missing:
                raise FileNotFoundError(cmd[0])
            if cmd[0] == "journalctl":
                return self._Result(0, journal)
            if cmd[0] == "systemctl":
                return self._Result(0, restarts + "\n")
            raise AssertionError(cmd)
        return fake

    def test_quiet_journal_is_ok(self):
        status, _ = gui_mod._diag_journal(self._runner(journal="всё хорошо\n"))
        self.assertEqual(status, gui_mod.DIAG_OK)

    def test_copy_failures_are_counted_with_last_line(self):
        journal = ("  Copy failed /mnt/a.mp4: раз\n"
                   "  Copy failed /mnt/b.mp4: два\n"
                   "Mount error /dev/sdb1: занято\n")
        status, text = gui_mod._diag_journal(self._runner(journal=journal))
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("2", text)
        self.assertIn("b.mp4", text, "показываем последнюю строку каждого вида")

    def test_service_restarts_are_reported(self):
        status, text = gui_mod._diag_journal(self._runner(restarts="3"))
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("3", text)

    def test_missing_journalctl_skips(self):
        status, _ = gui_mod._diag_journal(self._runner(missing=("journalctl",)))
        self.assertEqual(status, gui_mod.DIAG_SKIP)

    def test_kernel_disconnect_warns(self):
        journal = ("usb 1-1: USB disconnect, device number 5\n"
                   "sd 0:0:0:0: [sdb] I/O error, dev sdb, sector 123\n")
        status, text = gui_mod._diag_kernel(self._runner(journal=journal))
        self.assertEqual(status, gui_mod.DIAG_WARN)
        self.assertIn("sdb", text)

    def test_quiet_kernel_is_ok(self):
        status, _ = gui_mod._diag_kernel(self._runner(journal="usb 1-1: new device\n"))
        self.assertEqual(status, gui_mod.DIAG_OK)


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
