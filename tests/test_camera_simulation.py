"""Сквозная симуляция десяти камер через монитор, копирование и очередь GUI."""

import hashlib
import json
import os
from pathlib import Path
import queue
import sqlite3
import tempfile
import threading
import time
import unittest
from contextlib import ExitStack, closing
from unittest import mock

import usb_monitor as um


class CameraSimulationTest(unittest.TestCase):
    def test_ten_identical_ids_copy_only_first_and_preserve_duplicates(self):
        with tempfile.TemporaryDirectory() as directory, ExitStack() as patches:
            root = Path(directory)
            sources = [root / f"camera-{n}" for n in range(10)]
            expected = {}
            for n, source in enumerate(sources):
                source.mkdir()
                (source / ".astra_id").write_text("123456\n")
                data = bytes([n + 1]) * (1024 * 1024 + n * 1024)
                (source / "video.mp4").write_bytes(data)
                expected[n] = hashlib.sha256(data).hexdigest()
            destination = root / "archive"
            destination.mkdir()
            selected = {f"sd{n}": str(path) for n, path in enumerate(sources)}

            def partitions():
                return dict(selected)

            real_task = um.copy_task_linux
            completed = queue.Queue()

            def complete_task(*args, **kwargs):
                result = real_task(*args, **kwargs)
                completed.put(result)
                return result

            patches.enter_context(mock.patch.dict(os.environ, {"USB_BACKUP_DEST": str(destination)}))
            for name, value in {
                "DB_PATH": str(root / "devices.db"),
                "MOUNT_BASE": str(root / "mounts"),
                "_CONFIG_PATH": str(root / "config.json"),
                "MAX_WORKERS": 10,
                "_connected_devices": {},
                "_connected_device_ids": {},
                "_get_linux_partitions": partitions,
                "_get_drive_label_linux": lambda _: "RECORDER",
                "_get_device_serial_linux": lambda _: "IDENTICAL_FACTORY_ID",
                "copy_task_linux": complete_task,
            }.items():
                patches.enter_context(mock.patch.object(um, name, value))
            patches.enter_context(mock.patch.object(um.platform, "system", return_value="Linux"))
            patches.enter_context(mock.patch.object(um.os.path, "ismount", return_value=True))
            patches.enter_context(mock.patch.object(um.subprocess, "run"))

            progress = queue.Queue()
            stop = threading.Event()
            monitor_errors = []

            def run_monitor():
                try:
                    um.monitor_usb(0.05, stop, progress)
                except BaseException as error:
                    monitor_errors.append(error)

            monitor = threading.Thread(target=run_monitor)
            monitor.start()
            gui = None
            try:
                import gui as gui_module
                gui = gui_module.App.__new__(gui_module.App)
                gui.progress_queue = queue.Queue()
                gui.workers_data = {}
                gui.port_assignment = {}
                gui.C = dict(accent="blue", accent_warn="orange", accent_ok="green", bg_surface="black")
                gui.ports = [{"device_id": None, "preview": mock.Mock(), "status": mock.Mock()}
                             for _ in range(10)]
                gui.root = mock.Mock()
                gui.mon_status = mock.Mock()
                patches.enter_context(mock.patch.object(gui_module, "touch_copying_marker"))
            except ImportError:
                pass

            def await_terminal_events(count):
                terminal = []
                deadline = time.monotonic() + 15
                while len(terminal) < count:
                    remaining = deadline - time.monotonic()
                    self.assertGreater(remaining, 0, f"Не получены итоговые события: {terminal}")
                    event = progress.get(timeout=remaining)
                    if gui is not None:
                        gui.progress_queue.put(event)
                        gui._poll_queue()
                    if event[2] in {"done", "error"}:
                        terminal.append(event)
                return terminal

            try:
                terminal = await_terminal_events(10)
                results = [completed.get(timeout=10) for _ in range(10)]
                done = [event for event in terminal if event[2] == "done"]
                errors = [event for event in terminal if event[2] == "error"]
                self.assertEqual(len(done), 1)
                self.assertEqual(len(errors), 9)
                self.assertTrue(all("Дубликат Astra ID 123456" in event[5] for event in errors))
                self.assertEqual(sum(result[0] == 123456 for result in results), 1)
                self.assertEqual(sum(result[0] is None for result in results), 9)
                self.assertEqual(
                    [um._read_device_id_from_usb(str(path)) for path in sources],
                    [123456] * 10,
                )

                winner = int(done[0][6][2:])
                backup = destination / "Device123456" / "video.mp4"
                self.assertEqual(hashlib.sha256(backup.read_bytes()).hexdigest(), expected[winner])
                for n, source in enumerate(sources):
                    self.assertEqual((source / "video.mp4").exists(), n != winner)
                if gui is not None:
                    self.assertEqual(len(gui.port_assignment), 10)
                    states = [data["state_raw"] for data in gui.workers_data.values()]
                    self.assertEqual(states.count("done"), 1)
                    self.assertEqual(states.count("error"), 9)
                with closing(sqlite3.connect(um.DB_PATH)) as conn:
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 1)
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 1)
                print("SIMULATION_RESULT=" + json.dumps({
                    "connected": 10, "astra_id": 123456, "copied": 1,
                    "duplicates_rejected": 9, "markers_preserved": True,
                    "gui_logic_checked": gui is not None,
                }))
            finally:
                stop.set()
                monitor.join(timeout=10)
                self.assertFalse(monitor.is_alive())
                self.assertEqual(monitor_errors, [])


if __name__ == "__main__":
    unittest.main()
