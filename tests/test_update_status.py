"""Интерфейс показывает результат реальной службы вместо подтверждения её запуска."""

import gc
import json
import os
import sys
import threading
import unittest
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import updater

try:
    import gui
except ImportError:
    gui = None


class UpdateStatusReaderTest(unittest.TestCase):
    @staticmethod
    def journal(message, monotonic=110, pid="42"):
        return json.dumps({"MESSAGE": message, "__MONOTONIC_TIMESTAMP": str(monotonic),
                           "__REALTIME_TIMESTAMP": "1793620800000000", "_PID": pid}, ensure_ascii=False)

    def status(self, state="activating", result="success", messages=(), started="100", journal_error=""):
        answers = [
            SimpleNamespace(returncode=0, stdout=f"ActiveState={state}\nResult={result}\nExecMainStartTimestampMonotonic={started}\n", stderr=""),
            SimpleNamespace(returncode=1 if journal_error else 0, stdout="\n".join(messages), stderr=journal_error),
        ]
        with mock.patch.object(updater.subprocess, "run", side_effect=answers):
            return updater.read_update_status()

    def test_running_update_shows_version_and_ignores_old_attempt_and_commands(self):
        status = self.status(messages=[
            self.journal("[updater] релиз v1.28 уже откатывали", 90),
            self.journal("Обновление: найдена версия v1.29"),
            self.journal("Обновление: загрузка v1.29"),
            self.journal("Обновление: запуск команды: bash /tmp/install.sh"),
        ])
        self.assertTrue(status["active"])
        self.assertEqual(status["started"], "100")
        self.assertIn("v1.29", status["message"])
        self.assertIn("загрузка", status["message"])
        self.assertNotIn("v1.28", status["message"])
        self.assertNotIn("/tmp/install.sh", status["message"])

    def test_finished_check_explains_why_failed_release_was_skipped(self):
        status = self.status(state="inactive", messages=[self.journal("[updater] релиз v1.28 уже откатывали — пропуск")])
        self.assertFalse(status["active"])
        self.assertIn("v1.28", status["message"])
        self.assertIn("пропуск", status["message"])

    def test_failed_service_shows_python_error_even_without_normal_updater_messages(self):
        status = self.status(state="failed", result="exit-code", messages=[self.journal("ModuleNotFoundError: No module named tkinter")])
        self.assertFalse(status["active"])
        self.assertIn("ошиб", status["message"].lower())
        self.assertIn("tkinter", status["message"])

    def test_journal_permission_failure_is_visible(self):
        status = self.status(state="inactive", journal_error="Permission denied")
        self.assertIn("Permission denied", status["message"])

    def test_malformed_journal_records_do_not_hide_valid_result(self):
        status = self.status(state="inactive", messages=["[]", "not json",
            self.journal("Обновление: обновление v1.29 установлено")])
        self.assertIn("v1.29 установлено", status["message"])

    def test_missing_systemd_is_visible_without_exception(self):
        with mock.patch.object(updater.subprocess, "run", side_effect=FileNotFoundError("systemctl")):
            status = updater.read_update_status()
        self.assertFalse(status["active"])
        self.assertIn("systemctl", status["message"])


@unittest.skipUnless(gui is not None, "Нужен Tkinter, дисплей не требуется")
class UpdateStatusGuiTest(unittest.TestCase):
    def setUp(self):
        self.interpreter = gui.tk.Tcl()
        self.app = gui.App.__new__(gui.App)
        self.app.root = SimpleNamespace(after=lambda delay, callback, *args: callback(*args))
        self.app._about_status_var = gui.tk.StringVar(self.interpreter)
        self.app._update_watch_generation = 1
        self.app.stop_event = threading.Event()

    def tearDown(self):
        self.app = None
        self.interpreter = None
        gc.collect()

    def test_stale_worker_cannot_overwrite_newer_result(self):
        self.app._about_status_var.set("Новый результат")
        self.app._apply_update_status(0, "Старый результат")
        self.assertEqual(self.app._about_status_var.get(), "Новый результат")

    def test_new_fast_attempt_is_not_confused_with_previous_result(self):
        answers = [
            {"active": False, "started": "100", "message": "Старый результат"},
            {"active": False, "started": "200", "message": "v1.29 уже откатывали: пропуск"},
        ]
        with mock.patch.object(gui.updater, "read_update_status", side_effect=answers), \
             mock.patch.object(gui, "_start_update_service", return_value=gui.UPDATE_STARTED):
            self.app._update_status_worker(1, start=True)
        self.assertIn("v1.29", self.app._about_status_var.get())
        self.assertNotIn("Старый результат", self.app._about_status_var.get())

    def test_offline_start_failure_cleans_spool_and_shows_reason(self):
        with mock.patch.object(gui.updater, "read_update_status", return_value={"active": False, "started": "100", "message": "старое"}), \
             mock.patch.object(gui, "_start_update_service", return_value="Служба недоступна"), \
             mock.patch.object(gui.updater, "_clear_offline_spool") as clear:
            self.app._update_status_worker(1, start=True, require_network=False)
        self.assertEqual(self.app._about_status_var.get(), "Служба недоступна")
        clear.assert_called_once()

    def test_already_running_attempt_can_finish_without_changing_start_time(self):
        answers = [
            {"active": True, "started": "100", "message": "загрузка"},
            {"active": True, "started": "100", "message": "установка"},
            {"active": False, "started": "100", "message": "Обновление установлено"},
        ]
        with mock.patch.object(gui.updater, "read_update_status", side_effect=answers), \
             mock.patch.object(gui, "_start_update_service", return_value=gui.UPDATE_STARTED):
            self.app._update_status_worker(1, start=True)
        self.assertEqual(self.app._about_status_var.get(), "Обновление установлено")

    def test_running_attempt_that_finishes_before_first_poll_is_not_reported_as_missing(self):
        answers = [
            {"active": True, "started": "100", "message": "установка"},
            {"active": False, "started": "100", "message": "Обновление установлено"},
        ]
        with mock.patch.object(gui.updater, "read_update_status", side_effect=answers), \
             mock.patch.object(gui, "_start_update_service", return_value=gui.UPDATE_STARTED), \
             mock.patch.object(gui.time, "monotonic", side_effect=[0, 16]):
            self.app._update_status_worker(1, start=True)
        self.assertEqual(self.app._about_status_var.get(), "Обновление установлено")

    def test_unknown_previous_state_does_not_make_unstarted_service_a_new_attempt(self):
        answers = [
            {"active": False, "started": None, "message": "нет данных"},
            {"active": False, "started": "0", "message": "не запускалась"},
        ]
        with mock.patch.object(gui.updater, "read_update_status", side_effect=answers), \
             mock.patch.object(gui, "_start_update_service", return_value=gui.UPDATE_STARTED), \
             mock.patch.object(gui.time, "monotonic", side_effect=[0, 16]):
            self.app._update_status_worker(1, start=True)
        self.assertIn("не начала новую проверку", self.app._about_status_var.get())

    def test_unchanged_old_result_does_not_count_as_new_check(self):
        old = {"active": False, "started": "100", "message": "Предыдущая проверка"}
        with mock.patch.object(gui.updater, "read_update_status", return_value=old), \
             mock.patch.object(gui, "_start_update_service", return_value=gui.UPDATE_STARTED), \
             mock.patch.object(gui.time, "monotonic", side_effect=[0, 16]):
            self.app._update_status_worker(1, start=True)
        self.assertIn("не начала новую проверку", self.app._about_status_var.get())

    def test_cancelled_worker_does_not_start_another_check(self):
        self.app._update_watch_generation = 2
        with mock.patch.object(gui.updater, "read_update_status", return_value={"started": "100"}), \
             mock.patch.object(gui, "_start_update_service", side_effect=AssertionError("устаревший запрос запустил службу")):
            self.app._update_status_worker(1, start=True)

    def test_window_closed_during_status_read_does_not_receive_callback(self):
        self.app._about_status_var.set("Окно закрыто")

        def read_status():
            self.app.stop_event.set()
            return {"active": False, "started": "100", "message": "Поздний ответ"}

        with mock.patch.object(gui.updater, "read_update_status", side_effect=read_status):
            self.app._update_status_worker(1)
        self.assertEqual(self.app._about_status_var.get(), "Окно закрыто")


if __name__ == "__main__":
    unittest.main()
