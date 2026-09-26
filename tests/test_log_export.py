"""Тесты выгрузки логов для диагностики (кнопка в Настройках).

Проверяют чистые функции usb_monitor: запись stdout в файл и сборку
пакета app.log + version.txt + summary.txt без персональных данных.
"""

import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class LogExportTest(unittest.TestCase):
    def test_export_creates_bundle_with_log_version_summary(self):
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
                bundle = um.export_logs(out)

            files = sorted(os.listdir(bundle))
            self.assertEqual(files, ["app.log", "summary.txt", "version.txt"])
            with open(os.path.join(bundle, "app.log")) as f:
                self.assertIn("USB Monitor", f.read())
            with open(os.path.join(bundle, "summary.txt")) as f:
                summary = f.read()
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
                bundle = um.export_logs(tmp)
            self.assertTrue(os.path.isfile(os.path.join(bundle, "summary.txt")))

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


if __name__ == "__main__":
    unittest.main()
