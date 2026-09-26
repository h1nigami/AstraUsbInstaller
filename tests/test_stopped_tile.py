import queue
import unittest
from unittest import mock

try:
    import tkinter
except ImportError:
    gui = None
else:
    import gui


@unittest.skipIf(gui is None, "Tkinter недоступен")
class StoppedTileTest(unittest.TestCase):
    def test_stopped_event_renders_neutral_tile_with_actual_progress(self):
        app = gui.App.__new__(gui.App)
        app.root = mock.Mock()
        app.progress_queue = queue.Queue()
        app.workers_data = {}
        app.port_assignment = {}
        app.C = {"bg_surface": "gray", "accent_ok": "green"}
        preview, status = mock.Mock(), mock.Mock()
        app.ports = [{"device_id": None, "preview": preview, "status": status}]
        app.progress_queue.put((123, "123", "stopped", 25, 100,
                                "Копирование остановлено", "test-device"))
        app._poll_queue()
        self.assertEqual(app.workers_data[123]["state"], "Остановлено")
        self.assertFalse(gui._is_busy(app.workers_data))
        self.assertEqual(preview.configure.call_args.kwargs["bg"], "gray")
        shown = status.configure.call_args.kwargs["text"]
        self.assertIn("Копирование остановлено", shown)
        self.assertIn("25%", shown)
        self.assertNotIn("Готово", shown)
