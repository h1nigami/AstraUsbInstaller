"""Тесты выгрузки логов для диагностики (кнопка в Настройках).

Проверяют запись stdout, обновлений и диагностики в один файл,
перенос запасных записей и экспорт без подпапок.
"""

import os
import io
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class LogExportTest(unittest.TestCase):
    def test_export_creates_one_file_with_log_version_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = os.path.join(tmp, "d.db")
            log_path = os.path.join(tmp, "app.log")
            with open(log_path, "w") as f:
                f.write("[12:00:00] USB Monitor | test\n")
            with mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "LOG_PATH", log_path), \
                 mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "config.json")):
                um._init_db().close()
                conn = um._connect(db_path)
                try:
                    conn.execute(
                        "INSERT INTO devices (id, serial, label, name, person, first_seen, last_seen)"
                        " VALUES (7, 'S', 'L', 'Ivan', 'Petr', 't', 't')")
                    conn.execute(
                        "INSERT INTO backups (device_id, dest_path, total_files, total_bytes,"
                        " started_at, finished_at) VALUES (7, 'p', 1, 2, 't', 't')")
                    conn.commit()
                finally:
                    conn.close()
                out = os.path.join(tmp, "out")
                os.mkdir(out)
                exported = um.export_logs(out)

            self.assertEqual(os.listdir(out), [os.path.basename(exported)])
            self.assertTrue(os.path.isfile(exported))
            with open(exported, encoding="utf-8") as f:
                summary = f.read()
            self.assertIn("USB Monitor", summary)
            self.assertIn("version:", summary)
            self.assertIn("devices: 1", summary)
            self.assertIn("backups: 1", summary)
            for secret in ("Ivan", "Petr", "SECRET"):
                self.assertNotIn(secret, summary)

    def test_export_without_log_file_still_succeeds(self):
        with tempfile.TemporaryDirectory() as tmp:
            with mock.patch.object(um, "DB_PATH", os.path.join(tmp, "d.db")), \
                 mock.patch.object(um, "LOG_PATH", os.path.join(tmp, "no-such.log")), \
                 mock.patch.object(um, "_CONFIG_PATH", os.path.join(tmp, "config.json")):
                um._init_db().close()
                exported = um.export_logs(tmp)
            self.assertTrue(os.path.isfile(exported))

    def test_repeated_exports_do_not_overwrite_each_other_or_source(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = os.path.join(tmp, "astra-usb-monitor.log")
            with open(source, "w", encoding="utf-8") as stream:
                stream.write("исходная запись\n")
            with mock.patch.object(um, "collect_diagnostics", return_value={"version": "v1.26"}):
                first = um.export_logs(tmp, log_path=source)
                second = um.export_logs(tmp, log_path=source)
            self.assertNotEqual(first, second)
            with open(source, encoding="utf-8") as stream:
                self.assertEqual(stream.read(), "исходная запись\n")
            for path in (first, second):
                with open(path, encoding="utf-8") as stream:
                    self.assertIn("исходная запись", stream.read())

    def test_export_reports_unreadable_log_instead_of_false_success(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = os.path.join(tmp, "source.log")
            out = os.path.join(tmp, "out")
            real_open = open

            def open_file(path, *args, **kwargs):
                if path == source:
                    raise PermissionError("журнал недоступен")
                return real_open(path, *args, **kwargs)

            with mock.patch("builtins.open", side_effect=open_file), \
                 mock.patch.object(um, "collect_diagnostics", return_value={}):
                with self.assertRaisesRegex(PermissionError, "журнал недоступен"):
                    um.export_logs(out, log_path=source)
            self.assertEqual(os.listdir(out), [])

    def test_unwritable_archive_falls_back_without_disabling_logging(self):
        with tempfile.TemporaryDirectory() as tmp:
            fallback = os.path.join(tmp, "app.log")
            archive = os.path.join(tmp, "archive")
            real_open = open

            def open_file(path, *args, **kwargs):
                if os.path.dirname(path) == archive:
                    raise PermissionError("архив только для чтения")
                return real_open(path, *args, **kwargs)

            try:
                with mock.patch("builtins.open", side_effect=open_file), \
                     mock.patch.object(um, "LOG_PATH", fallback), \
                     mock.patch.object(um, "get_dest_base", return_value=archive), \
                     mock.patch.object(um, "dest_available", return_value=True):
                    self.assertTrue(um.update_log_location(start=True))
                    print("запись при отказе диска")
            finally:
                um.restore_stdout()
            with open(fallback, encoding="utf-8") as stream:
                self.assertIn("запись при отказе диска", stream.read())

    def test_system_diagnostics_include_failed_release_services_and_usb_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "astra-usb-monitor.log")
            version_path = os.path.join(tmp, "app", "VERSION")
            with open(os.path.join(tmp, "app.failed"), "w") as stream:
                stream.write("v1.26")

            def run(command, **kwargs):
                self.assertLessEqual(kwargs["timeout"], 10)
                if command[0] == "systemctl":
                    self.assertIn("astra-usb-update.service", command)
                    self.assertIn("astra-usb-monitor.service", command)
                    output = "Result=exit-code\nExecMainStatus=1\n"
                elif "-k" in command:
                    output = "usb 1-2: reset high-speed USB device\nnoise unrelated to station\nOut of memory: Killed process 42 (python3)\n"
                else:
                    self.assertIn("astra-usb-update.service", command)
                    output = "установка не удалась (сервис не поднялся)\nновая версия не поднялась: откат\n"
                return mock.Mock(returncode=0, stdout=output, stderr="")

            with mock.patch.object(um.platform, "system", return_value="Linux"), \
                 mock.patch.object(um, "VERSION_FILE", version_path), \
                 mock.patch.object(um, "read_version", return_value=("v1.13", "2026-09-14")), \
                 mock.patch.object(um.subprocess, "run", side_effect=run):
                self.assertTrue(um.append_system_diagnostics(path))
            with open(path, encoding="utf-8") as stream:
                content = stream.read()
            for expected in ("v1.13", "v1.26", "ExecMainStatus=1", "установка не удалась", "откат", "usb 1-2", "Killed process 42"):
                self.assertIn(expected, content)
            self.assertNotIn("noise unrelated", content)

    def test_diagnostics_survive_missing_system_commands(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "astra-usb-monitor.log")
            with mock.patch.object(um.platform, "system", return_value="Linux"), \
                 mock.patch.object(um.subprocess, "run", side_effect=FileNotFoundError("no journalctl")):
                self.assertTrue(um.append_system_diagnostics(path))
            with open(path, encoding="utf-8") as stream:
                self.assertIn("no journalctl", stream.read())

    def test_fallback_log_rejoins_archive_when_disk_returns(self):
        with tempfile.TemporaryDirectory() as tmp:
            fallback = os.path.join(tmp, "data", "app.log")
            archive = os.path.join(tmp, "archive")
            os.mkdir(archive)
            available = {"value": False}
            try:
                with mock.patch.object(um, "LOG_PATH", fallback), \
                     mock.patch.object(um, "get_dest_base", return_value=archive), \
                     mock.patch.object(um, "dest_available", side_effect=lambda: available["value"]):
                    um.update_log_location(start=True)
                    print("обновление не удалось без диска")
                    available["value"] = True
                    um.update_log_location()
                    print("диск вернулся")
            finally:
                um.restore_stdout()
            with open(os.path.join(archive, "astra-usb-monitor.log"), encoding="utf-8") as stream:
                content = stream.read()
            self.assertIn("обновление не удалось без диска", content)
            self.assertIn("диск вернулся", content)

    def test_file_logging_captures_prints_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as tmp:
            log_path = os.path.join(tmp, "app.log")
            real_stdout = sys.stdout
            try:
                with mock.patch.object(um, "LOG_PATH", log_path):
                    self.assertTrue(um.setup_file_logging())
                    print("marker-line-1")
                    self.assertTrue(um.setup_file_logging())  # повторно — без двойной записи
                    print("marker-line-2")
            finally:
                um.restore_stdout()
                sys.stdout = real_stdout
            with open(log_path) as f:
                content = f.read()
            self.assertEqual(content.count("marker-line-1"), 1)
            self.assertEqual(content.count("marker-line-2"), 1)

    def test_file_lines_start_with_date_and_time(self):
        with tempfile.TemporaryDirectory() as tmp:
            log_path = os.path.join(tmp, "app.log")
            with open(log_path, "w", encoding="utf-8") as fh:
                tee = um._LogTee(io.StringIO(), fh)
                tee.write("первая ")
                tee.write("строка\nвторая\n")
                tee.write("\n")
            with open(log_path, encoding="utf-8") as f:
                lines = f.read().splitlines()
        stamp = r"\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}  "
        self.assertEqual(len(lines), 3)
        self.assertRegex(lines[0], "^" + stamp + "первая строка$")
        self.assertRegex(lines[1], "^" + stamp + "вторая$")
        self.assertEqual(lines[2], "")  # пустую строку не штампуем

    def test_log_follows_archive_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            fallback = os.path.join(tmp, "data", "app.log")
            first, second = os.path.join(tmp, "a1"), os.path.join(tmp, "a2")
            os.mkdir(first)
            os.mkdir(second)
            dest = {"path": first, "ok": True}
            real_stdout, real_stderr = sys.stdout, sys.stderr
            try:
                with mock.patch.object(um, "LOG_PATH", fallback), \
                     mock.patch.object(um, "get_dest_base", lambda: dest["path"]), \
                     mock.patch.object(um, "dest_available", lambda: dest["ok"]):
                    # Без старта запись в файл не включается.
                    self.assertFalse(um.update_log_location())
                    self.assertIs(sys.stdout, real_stdout)
                    um.update_log_location(start=True)
                    print("line-in-first")
                    dest["path"] = second
                    um.update_log_location()
                    print("line-in-second")
                    dest["ok"] = False
                    um.update_log_location()
                    print("line-in-fallback")
                    out = os.path.join(tmp, "out")
                    os.mkdir(out)
                    exported = um.export_logs(out, db_path=os.path.join(tmp, "d.db"))
            finally:
                um.restore_stdout()
                sys.stdout, sys.stderr = real_stdout, real_stderr

            def read(path):
                with open(path, encoding="utf-8") as f:
                    return f.read()
            name = um.ARCHIVE_LOG_NAME
            self.assertTrue(read(os.path.join(first, name)).strip().endswith("  line-in-first"))
            self.assertTrue(read(os.path.join(second, name)).strip().endswith("  line-in-second"))
            self.assertTrue(read(fallback).strip().endswith("  line-in-fallback"))
            # Выгрузка берёт тот журнал, в который пишется сейчас.
            self.assertIn("line-in-fallback", read(exported))


if __name__ == "__main__":
    unittest.main()
