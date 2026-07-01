"""Tests for gui.py's non-GUI config helpers (exit-password persistence).

gui.py imports tkinter at module scope, which some minimal dev/CI boxes
don't have installed. That import is attempted here in isolation and the
whole class is skipped (not failed) if tkinter is unavailable, so this file
is safe to include in `python3 -m unittest discover` everywhere while still
providing real coverage on machines that do have python3-tk (as the Docker
image and desktop app do).
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
