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
    def test_ten_identical_ids_copy_separately_and_survive_reconnect(self):
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
            selection_lock = threading.Lock()
            selected = {f"sd{n}": str(path) for n, path in enumerate(sources)}

            def partitions():
                with selection_lock:
                    return dict(selected)

            barrier = threading.Barrier(10)
            real_copy = um._copy_files
            real_task = um.copy_task_linux
            completed = queue.Queue()

            def simultaneous_copy(*args, **kwargs):
                barrier.wait(timeout=10)
                return real_copy(*args, **kwargs)

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
                "_copy_files": simultaneous_copy,
                "copy_task_linux": complete_task,
            }.items():
                patches.enter_context(mock.patch.object(um, name, value))
            patches.enter_context(mock.patch.object(um.platform, "system", return_value="Linux"))
            patches.enter_context(mock.patch.object(um.os.path, "ismount", return_value=True))

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

            def await_events(state, count):
                events = []
                matched = set()
                deadline = time.monotonic() + 15
                while len(matched) < count:
                    remaining = deadline - time.monotonic()
                    self.assertGreater(remaining, 0, f"Не получены события {state}: {matched}")
                    event = progress.get(timeout=remaining)
                    self.assertNotEqual(event[2], "error", event)
                    events.append(event)
                    if gui is not None:
                        gui.progress_queue.put(event)
                        gui._poll_queue()
                    if event[2] == state or event[0] == state:
                        matched.add(event[1] if state == "_removed_" else event[6])
                return events

            try:
                first_events = await_events("done", 10)
                for _ in range(10):
                    completed.get(timeout=10)
                first_ids = [um._read_device_id_from_usb(str(path)) for path in sources]
                self.assertEqual(len(set(first_ids)), 10)
                self.assertEqual(first_ids.count(123456), 1)
                self.assertEqual(len({event[0] for event in first_events}), 10)
                for n, device_id in enumerate(first_ids):
                    backup = destination / f"Device{device_id}" / "video.mp4"
                    self.assertEqual(hashlib.sha256(backup.read_bytes()).hexdigest(), expected[n])
                    self.assertFalse((sources[n] / "video.mp4").exists())
                    totals = {e[4] for e in first_events if e[0] == device_id and e[2] == "copying"}
                    self.assertEqual(totals, {1024 * 1024 + n * 1024})
                if gui is not None:
                    self.assertEqual(len(gui.port_assignment), 10)
                    self.assertTrue(all(data["state_raw"] == "done" for data in gui.workers_data.values()))

                with selection_lock:
                    selected.clear()
                await_events("_removed_", 10)
                if gui is not None:
                    self.assertEqual(gui.workers_data, {})
                    self.assertEqual(gui.port_assignment, {})

                for n, source in enumerate(sources):
                    (source / "next.mp4").write_bytes(f"second-session-camera-{n}".encode())
                with selection_lock:
                    selected.update({f"new{9 - n}": str(path) for n, path in enumerate(sources)})
                await_events("done", 10)
                for _ in range(10):
                    completed.get(timeout=10)
                self.assertEqual([um._read_device_id_from_usb(str(path)) for path in sources], first_ids)
                self.assertEqual(len(list(destination.iterdir())), 10)
                for n, device_id in enumerate(first_ids):
                    backup = destination / f"Device{device_id}"
                    self.assertEqual(hashlib.sha256((backup / "video.mp4").read_bytes()).hexdigest(), expected[n])
                    self.assertEqual((backup / "next.mp4").read_text(), f"second-session-camera-{n}")
                    self.assertFalse((sources[n] / "next.mp4").exists())
                with closing(sqlite3.connect(um.DB_PATH)) as conn:
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0], 10)
                    self.assertEqual(conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0], 20)
                if gui is not None:
                    self.assertEqual(len(gui.port_assignment), 10)
                print("SIMULATION_RESULT=" + json.dumps({
                    "devices": 10, "initial_marker": 123456, "assigned_ids": first_ids,
                    "backup_folders": 10, "backup_sessions": 20, "verified_files": 20,
                    "ids_preserved_after_reconnect": True, "gui_logic_checked": gui is not None,
                }))
            finally:
                stop.set()
                monitor.join(timeout=10)
                self.assertFalse(monitor.is_alive())
                self.assertEqual(monitor_errors, [])


if __name__ == "__main__":
    unittest.main()
