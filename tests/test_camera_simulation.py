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
    def test_ten_unique_device_ids_copy_to_ten_separate_folders(self):
        with tempfile.TemporaryDirectory() as directory, ExitStack() as patches:
            root = Path(directory)
            sources = [root / f"camera-{n}" for n in range(10)]
            expected = {}
            for n, source in enumerate(sources):
                (source / "DCIM").mkdir(parents=True)
                data = bytes([n + 1]) * (1024 * 1024 + n * 1024)
                (source / "DCIM" / f"A11_{1234560 + n}_222222_20260915120000_0001.mp4").write_bytes(data)
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
                observed = []
                deadline = time.monotonic() + 15
                while len(terminal) < count:
                    remaining = deadline - time.monotonic()
                    self.assertGreater(remaining, 0, f"Не получены итоговые события: {terminal}")
                    event = progress.get(timeout=remaining)
                    observed.append(event)
                    if gui is not None:
                        gui.progress_queue.put(event)
                        gui._poll_queue()
                    if event[2] in {"done", "error"}:
                        terminal.append(event)
                return terminal, observed

            try:
                terminal, observed = await_terminal_events(10)
                results = [completed.get(timeout=10) for _ in range(10)]
                done = [event for event in terminal if event[2] == "done"]
                errors = [event for event in terminal if event[2] == "error"]
                self.assertEqual(len(done), 10)
                self.assertEqual(errors, [])
                self.assertEqual({result[0] for result in results}, {1234560 + n for n in range(10)})
                self.assertEqual({(event[0], event[1]) for event in done},
                                 {(1234560 + n, str(1234560 + n)) for n in range(10)})
                self.assertEqual({event[6] for event in observed if event[2] == "identifying"},
                                 {f"sd{n}" for n in range(10)})
                self.assertTrue(all(event[1] == "" for event in observed
                                    if event[2] == "identifying"))
                self.assertTrue(all(not (source / ".astra_id").exists() for source in sources))
                for n, source in enumerate(sources):
                    name = f"A11_{1234560 + n}_222222_20260915120000_0001.mp4"
                    backup = destination / f"Device{1234560 + n}" / "DCIM" / name
                    self.assertEqual(hashlib.sha256(backup.read_bytes()).hexdigest(), expected[n])
                    self.assertFalse((source / "DCIM" / name).exists())
                if gui is not None:
                    self.assertEqual(len(gui.port_assignment), 10)
                    self.assertTrue(all(data["device"] == str(device_id)
                                        for device_id, data in gui.workers_data.items()))
                    self.assertTrue(all(isinstance(device_id, int)
                                        for device_id in gui.workers_data))
                    states = [data["state_raw"] for data in gui.workers_data.values()]
                    self.assertEqual(states.count("done"), 10)
                    self.assertEqual(states.count("error"), 0)
                with closing(sqlite3.connect(um.DB_PATH)) as conn:
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 10)
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 10)
                print("SIMULATION_RESULT=" + json.dumps({
                    "connected": 10, "copied": 10, "device_ids": [1234560 + n for n in range(10)],
                    "markers_created": False,
                    "gui_logic_checked": gui is not None,
                }))
            finally:
                stop.set()
                monitor.join(timeout=10)
                self.assertFalse(monitor.is_alive())
                self.assertEqual(monitor_errors, [])


if __name__ == "__main__":
    unittest.main()
