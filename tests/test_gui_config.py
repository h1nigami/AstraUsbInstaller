"""Проверки настроек GUI и отображения регистраторов.

При отсутствии Tkinter набор пропускается; только тест размещения требует экран.
"""

import os
import queue
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um

try:
    import tkinter
except ImportError:
    gui_mod = None
    _HAS_TK = False
else:
    import gui as gui_mod
    _HAS_TK = True


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

    def _bay_app(self, count=10):
        app = gui_mod.App.__new__(gui_mod.App)
        app.root = gui_mod.tk.Tcl()
        app._bay_count = count
        app._bay_count_var = mock.Mock()
        app.progress_queue = queue.Queue()
        app.mon_status = gui_mod.tk.StringVar(app.root)
        app.workers_data = {}
        app.port_assignment = {}
        app.C = dict(border="gray", bg_panel="black", fg_main="white",
                     accent="blue", accent_warn="orange", accent_ok="green",
                     bg_surface="black")
        for owner, name in ((gui_mod.tk, "Frame"), (gui_mod.tk, "Label"),
                            (gui_mod.ttk, "Frame"), (gui_mod.ttk, "Label")):
            patcher = mock.patch.object(owner, name)
            patcher.start()
            self.addCleanup(patcher.stop)
        app._build_workers_tab(mock.Mock())
        return app

    def test_bay_count_defaults_and_rejects_invalid_config_values(self):
        for cfg, expected in (({}, 10), ({"bay_count": 8}, 8),
                              ({"bay_count": 1}, 1), ({"bay_count": 12}, 12),
                              ({"bay_count": 0}, 10), ({"bay_count": 13}, 10),
                              ({"bay_count": "8"}, 10), ({"bay_count": None}, 10),
                              ({"bay_count": True}, 10), ({"bay_count": 8.5}, 10)):
            with self.subTest(cfg=cfg):
                self.assertEqual(gui_mod._get_bay_count(cfg), expected)

    def test_workers_tab_has_exactly_configured_number_of_visible_ports(self):
        app = self._bay_app(8)
        self.assertEqual(len(app.ports), 8)

    def test_save_bay_count_preserves_config_and_connected_worker(self):
        gui_mod._save_config({"backup_dest": "/archive", "exit_password": "secret"})
        app = self._bay_app()
        app.workers_data = {17: {"device": "17", "state": "Копирование",
                                "state_raw": "copying", "progress": "50%"}}
        app.port_assignment = {17: 9}
        app._bay_count_var.get.return_value = " 8 "
        app._save_bay_count()

        cfg = gui_mod._load_config()
        self.assertEqual(gui_mod._get_bay_count(cfg), 8)
        self.assertEqual(cfg["backup_dest"], "/archive")
        self.assertEqual(cfg["exit_password"], "secret")
        self.assertEqual(app._bay_count, 8)
        self.assertEqual(len(app.ports), 8)
        self.assertEqual(app.ports[app.port_assignment[17]]["device_id"], 17)
        self.assertEqual(app.workers_data[17]["progress"], "50%")

    def test_invalid_or_too_small_bay_count_leaves_config_and_ports_unchanged(self):
        gui_mod._save_config({"bay_count": 10})
        app = self._bay_app()
        ports = app.ports
        for value in ("", "eight", "8.5", "0", "-1", "13"):
            with self.subTest(value=value), mock.patch.object(gui_mod.messagebox, "showwarning"):
                app._bay_count_var.get.return_value = value
                app._save_bay_count()
                self.assertEqual(gui_mod._load_config()["bay_count"], 10)
                self.assertIs(app.ports, ports)

        app.workers_data = {i: {"state_raw": "copying"} for i in range(9)}
        app._bay_count_var.get.return_value = "8"
        with mock.patch.object(gui_mod.messagebox, "showwarning"):
            app._save_bay_count()
        self.assertEqual(gui_mod._load_config()["bay_count"], 10)
        self.assertIs(app.ports, ports)

    def test_failed_bay_count_write_leaves_ports_unchanged(self):
        gui_mod._save_config({"bay_count": 10})
        app = self._bay_app()
        ports = app.ports
        app._bay_count_var.get.return_value = "8"
        with mock.patch.object(um, "_save_config", return_value=False), \
             mock.patch.object(gui_mod.messagebox, "showerror"):
            app._save_bay_count()
        self.assertEqual(gui_mod._load_config()["bay_count"], 10)
        self.assertEqual(app._bay_count, 10)
        self.assertIs(app.ports, ports)

    def test_more_connected_recorders_than_bays_keep_progress_and_warn_operator(self):
        gui_mod._save_config({"bay_count": 8})
        app = self._bay_app(8)
        app.mon_status.set("Ошибка мониторинга")
        for device in range(10):
            app.progress_queue.put((device + 1, str(device + 1), "copying",
                                    50, 100, "Копирование", f"sd{device}"))
        marker = os.path.join(self.tmpdir.name, ".copying")
        with mock.patch.object(um, "COPYING_MARKER", marker):
            app._poll_queue()
        self.assertEqual(len(app.ports), 8)
        self.assertEqual(len(app.port_assignment), 8)
        self.assertEqual(set(app.workers_data), set(range(1, 11)))
        self.assertTrue(all(data["state_raw"] == "copying"
                            for data in app.workers_data.values()))
        self.assertTrue(os.path.exists(marker))
        self.assertEqual(gui_mod._load_config()["bay_count"], 8)
        self.assertEqual(app.mon_status.get(), "Ошибка мониторинга")
        self.assertIn("10", app._overflow_status.get())
        self.assertIn("8", app._overflow_status.get())
        app._bay_count_var.get.return_value = "10"
        app._save_bay_count()
        self.assertEqual(gui_mod._load_config()["bay_count"], 10)
        self.assertEqual(len(app.ports), 10)
        self.assertEqual(set(app.port_assignment), set(range(1, 11)))
        self.assertTrue(all(port["device_id"] in app.workers_data for port in app.ports))
        self.assertEqual(app._overflow_status.get(), "")

    def test_hidden_recorder_appears_on_confirmed_removal_without_another_progress_event(self):
        app = self._bay_app(8)
        for device in range(9):
            app.progress_queue.put((device + 1, str(device + 1), "done",
                                    100, 100, "Готово", f"sd{device}"))
        app._poll_queue()
        self.assertNotIn(9, app.port_assignment)
        app.progress_queue.put(("_removed_", "sd0", "", 0, 0, "", ""))
        with mock.patch.object(gui_mod.time, "time", return_value=1000):
            app._poll_queue()
        self.assertEqual(app.ports[app.port_assignment[1]]["device_id"], 1)
        with mock.patch.object(gui_mod.time, "time", return_value=1006):
            app._poll_queue()
        self.assertNotIn(1, app.workers_data)
        self.assertEqual(len(app.port_assignment), 8)
        self.assertEqual(app.ports[app.port_assignment[9]]["device_id"], 9)
        self.assertEqual(app.workers_data[9]["state_raw"], "done")
        self.assertEqual(app._overflow_status.get(), "")

    def test_only_hidden_copy_still_blocks_updates(self):
        app = self._bay_app(8)
        for device in range(8):
            app.progress_queue.put((device + 1, str(device + 1), "done",
                                    100, 100, "Готово", f"sd{device}"))
        app.progress_queue.put((9, "9", "copying", 50, 100, "Копирование", "sd8"))
        marker = os.path.join(self.tmpdir.name, ".copying")
        with mock.patch.object(um, "COPYING_MARKER", marker):
            app._poll_queue()
            self.assertTrue(um.is_copying())
        self.assertNotIn(9, app.port_assignment)
        self.assertTrue(gui_mod._is_busy(app.workers_data))

    @unittest.skipUnless(sys.platform == "win32" or os.environ.get("DISPLAY"),
                         "Для проверки размещения виджетов нужен экран")
    def test_small_screen_keeps_settings_accessible_and_eight_ports_visible(self):
        root = gui_mod.tk.Tk()
        self.addCleanup(root.destroy)
        root.withdraw()
        if sys.platform == "win32":
            root.attributes("-alpha", 0)
        with mock.patch.object(gui_mod.tk, "Tk", return_value=root), \
             mock.patch.object(gui_mod, "DB_PATH", os.path.join(self.tmpdir.name, "devices.db")), \
             mock.patch.object(um, "DB_PATH", os.path.join(self.tmpdir.name, "devices.db")), \
             mock.patch.object(um, "_CONFIG_PATH", self.cfg_path), \
             mock.patch.object(um, "DEST_BASE", os.path.join(self.tmpdir.name, "archive")), \
             mock.patch.object(gui_mod.App, "_start_monitor"), \
             mock.patch.object(gui_mod.App, "_poll_queue"), \
             mock.patch.object(gui_mod.App, "_check_lock_timeout"):
            app = gui_mod.App()
        root.attributes("-fullscreen", False)
        root.geometry("1024x600")
        app.tabs_unlocked = True
        app.nb.select(3)
        root.deiconify()
        root.update()
        tab = root.nametowidget(app.nb.tabs()[3])
        canvases = [child for child in tab.winfo_children()
                    if isinstance(child, gui_mod.tk.Canvas)]
        self.assertEqual(len(canvases), 1, "Настройки должны прокручиваться на маленьком экране")
        canvas = canvases[0]
        canvas.yview_moveto(1)
        root.update()
        content = canvas.winfo_children()[0]
        about = content.winfo_children()[0].winfo_children()[-1]
        self.assertLessEqual(about.winfo_rooty() + about.winfo_height(),
                             canvas.winfo_rooty() + canvas.winfo_height())
        app._bay_count_var.set("8")
        app._save_bay_count()
        app.nb.select(0)
        root.update()
        self.assertEqual(len(app.ports), 8)
        self.assertTrue(all(port["frame"].winfo_ismapped() for port in app.ports))


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

    def test_offline_package_is_handed_over_without_network(self):
        calls = []

        def fake_runner(cmd, timeout):
            calls.append(cmd)
            return self._Result(0)

        self.assertEqual(gui_mod._start_update_service(fake_runner, network_check=lambda: False,
                                                       require_network=False),
                         "Проверка обновления запущена")
        self.assertEqual(calls, [["systemctl", "start", "astra-usb-update.service", "--no-block"]])


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

    def test_waits_while_station_identifies_returning_cards(self):
        """Идёт опознание — карты после сбоя ещё возвращаются, плитки держим."""
        data = {
            7: {"state_raw": "detached", "detached_at": 1000.0},
            "identity:sdf": {"state_raw": "identifying"},
        }
        self.assertEqual(gui_mod._detached_too_long(data, now=1060.0), [])

    def test_pulled_device_clears_quickly(self):
        """Опознавать некого — карту просто вынули, серую плитку не держим."""
        data = {7: {"state_raw": "detached", "detached_at": 1000.0}}
        self.assertEqual(gui_mod._detached_too_long(data, now=1004.0), [])
        self.assertEqual(gui_mod._detached_too_long(data, now=1007.0), [7])

    def test_ceiling_applies_even_while_identifying(self):
        """Потолок обязателен: иначе зависшее опознание держит плитку вечно."""
        data = {
            7: {"state_raw": "detached", "detached_at": 1000.0},
            "identity:sdf": {"state_raw": "identifying"},
        }
        self.assertEqual(gui_mod._detached_too_long(data, now=1000.0 + 200), [7])


@unittest.skipUnless(_HAS_TK, "tkinter is not installed in this environment")
class StartupCleanupTest(unittest.TestCase):
    """Автоочистка при запуске идёт один раз и ей нужна эксклюзивная
    блокировка: занятая выгрузкой станция откладывает её, а не отменяет."""

    def _fake_app(self):
        app = mock.Mock()
        app._cleanup_days = 30
        app.root.after = lambda _delay, fn, *args: fn(*args)
        app._cleanup_worker = lambda *a, **kw: gui_mod.App._cleanup_worker(app, *a, **kw)
        return app

    def test_busy_station_retries_until_cleanup_runs(self):
        app = self._fake_app()
        results = [gui_mod.usb_monitor.StationBusy("Станция занята"),
                   gui_mod.usb_monitor.StationBusy("Станция занята"),
                   (2, 2048)]
        sleeps = []

        def cleanup(**kwargs):
            outcome = results.pop(0)
            if isinstance(outcome, Exception):
                raise outcome
            return outcome

        with mock.patch.object(gui_mod, "cleanup_old_backup_videos", side_effect=cleanup):
            gui_mod.App._run_startup_cleanup(app, attempts=5, pause=7, sleep=sleeps.append)

        self.assertEqual(results, [])
        self.assertEqual(sleeps, [7, 7])
        self.assertIn("Удалено 2 видео", app._cleanup_status_var.set.call_args[0][0])

    def test_other_errors_are_not_retried(self):
        app = self._fake_app()
        sleeps = []
        with mock.patch.object(gui_mod, "cleanup_old_backup_videos",
                               side_effect=OSError("диск архива недоступен")) as cleanup:
            gui_mod.App._run_startup_cleanup(app, attempts=5, pause=7, sleep=sleeps.append)
        cleanup.assert_called_once()
        self.assertEqual(sleeps, [])
