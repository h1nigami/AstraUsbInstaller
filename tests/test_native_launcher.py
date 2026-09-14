"""Падение GUI должно доходить до systemd без внутреннего перезапуска."""

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(os.name == "posix" and shutil.which("bash"), "Нужен Linux с bash")
class NativeLauncherTest(unittest.TestCase):
    def test_launcher_preserves_gui_exit_status(self):
        root = Path(__file__).resolve().parents[1]
        for name in ("start_native.sh", "avalonia/start_native.sh"):
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
