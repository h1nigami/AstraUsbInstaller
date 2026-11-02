"""Падение GUI должно доходить до systemd без внутреннего перезапуска."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "Нужен Linux с bash")
class NativeLauncherTest(unittest.TestCase):
    def test_python_launcher_logs_gui_import_failure_before_exit(self):
        root = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            archive = directory / "archive"
            archive.mkdir()
            (archive / ".astra_dest").touch()
            shutil.copyfile(root / "usb_monitor.py", directory / "usb_monitor.py")
            (directory / "gui.py").write_text("raise ImportError('broken-gui-startup')\n")
            source = (root / "start_native.sh").read_text()
            loop = source[source.index("while true; do"):]
            script = "log() { :; }; _detect_display() { return 0; }; _setup_display_auth() { return 0; };\n" + loop
            env = dict(os.environ, USB_BACKUP_DEST=str(archive), USB_DB_PATH=str(directory / "d.db"))
            result = subprocess.run(["bash", "-c", script], cwd=tmp, env=env,
                                    capture_output=True, text=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)
            # Временная папка находится на системном разделе: станция выбирает запасной журнал.
            text = (directory / "data" / "app.log").read_text(encoding="utf-8")
            self.assertIn("broken-gui-startup", text)
            self.assertIn("Traceback", text)

    def test_launcher_preserves_gui_exit_status(self):
        root = Path(__file__).resolve().parents[1]
        scripts = ["start_native.sh"]
        # Архив Python-релиза не содержит модуль Avalonia.
        if (root / "avalonia").is_dir():
            scripts.append("avalonia/start_native.sh")
        for name in scripts:
            for status in (0, 42):
                with self.subTest(script=name, status=status), tempfile.TemporaryDirectory() as tmp:
                    directory = Path(tmp)
                    for binary in ("python3", "AstraUsb"):
                        path = directory / binary
                        path.write_text(f"#!/bin/sh\nexit {status}\n")
                        path.chmod(0o755)
                    # Подменяется только обнаружение X11; цикл запуска взят из рабочего скрипта.
                    source = (root / name).read_text()
                    loop = source[source.index("while true; do"):]
                    script = "log() { :; }; _detect_display() { return 0; }; _setup_display_auth() { return 0; };\n" + loop
                    env = dict(os.environ, APP_DIR=tmp, PATH=tmp + os.pathsep + os.environ["PATH"])
                    try:
                        result = subprocess.run(["bash", "-c", script], env=env, timeout=1,
                                                capture_output=True, text=True)
                    except subprocess.TimeoutExpired:
                        self.fail("Скрипт скрывает падение приложения внутренним перезапуском")
                    self.assertEqual(result.returncode, status, result.stderr)
