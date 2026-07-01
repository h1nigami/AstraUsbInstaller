"""Integration test for monitor_usb()'s attach/detach polling loop,
including the removal-debounce guarantee (a device must be missing for
>= 1.5x the poll interval before it is confirmed removed).

Real _get_linux_partitions()/copy_task_linux() are replaced with test
doubles so this runs without real USB hardware or mounting privileges.
"""

import os
import sys
import queue
import threading
import time
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class MonitorLoopTest(unittest.TestCase):
    def test_detects_new_device_then_confirms_removal_after_debounce(self):
        seen_devices = []

        def fake_copy_task_linux(device_path, progress_obj, task_id, progress_queue=None):
            seen_devices.append(device_path)
            return (0, 0, 0)

        # Poll sequence: absent -> appears -> still present -> disappears -> stays absent.
        poll_sequence = [[], ["sda1"], ["sda1"], [], []]

        def fake_get_partitions():
            if len(poll_sequence) > 1:
                return poll_sequence.pop(0)
            return poll_sequence[0]

        pq = queue.Queue()
        stop_event = threading.Event()
        interval = 0.05

        with tempfile.TemporaryDirectory() as mount_base, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "MOUNT_BASE", mount_base), \
                 mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "_get_linux_partitions", side_effect=fake_get_partitions), \
                 mock.patch.object(um, "copy_task_linux", fake_copy_task_linux):
                t = threading.Thread(
                    target=um.monitor_usb, args=(interval, stop_event, pq), daemon=True)
                t.start()
                try:
                    deadline = time.time() + 5
                    while "sda1" not in seen_devices and time.time() < deadline:
                        time.sleep(0.02)
                    self.assertIn("sda1", seen_devices, "new device must be submitted for backup")

                    removed_msg = None
                    deadline = time.time() + 5
                    while time.time() < deadline:
                        try:
                            msg = pq.get(timeout=0.3)
                        except queue.Empty:
                            continue
                        if msg[0] == "_removed_":
                            removed_msg = msg
                            break
                    self.assertIsNotNone(removed_msg, "expected a removal notification")
                    self.assertEqual(removed_msg[1], "sda1")
                finally:
                    stop_event.set()
                    t.join(timeout=5)
                    self.assertFalse(t.is_alive(), "monitor_usb must stop when stop_event is set")

    def test_flapping_device_is_not_removed_before_grace_period(self):
        """A device that disappears for a single poll and immediately comes
        back must never trigger a removal notification (debounce)."""
        seen_devices = []

        def fake_copy_task_linux(device_path, progress_obj, task_id, progress_queue=None):
            seen_devices.append(device_path)
            return (0, 0, 0)

        # Long poll interval relative to the blip: one missing poll, then back,
        # repeated forever until the test stops it.
        state = {"i": 0}

        def fake_get_partitions():
            state["i"] += 1
            # Present on every poll except the 3rd one (a single-poll blip).
            return [] if state["i"] == 3 else ["sda1"]

        pq = queue.Queue()
        stop_event = threading.Event()
        interval = 0.2  # grace = 0.3s, comfortably longer than a single poll gap

        with tempfile.TemporaryDirectory() as mount_base, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            with mock.patch.object(um, "MOUNT_BASE", mount_base), \
                 mock.patch.object(um, "DB_PATH", db_path), \
                 mock.patch.object(um, "_get_linux_partitions", side_effect=fake_get_partitions), \
                 mock.patch.object(um, "copy_task_linux", fake_copy_task_linux):
                t = threading.Thread(
                    target=um.monitor_usb, args=(interval, stop_event, pq), daemon=True)
                t.start()
                try:
                    deadline = time.time() + 3
                    while "sda1" not in seen_devices and time.time() < deadline:
                        time.sleep(0.02)
                    self.assertIn("sda1", seen_devices)

                    # Let several more polls happen (covering the blip) without
                    # ever seeing a removal notification.
                    time.sleep(interval * 5)
                    self.assertTrue(pq.empty(), "a single-poll blip must not be reported as removed")
                finally:
                    stop_event.set()
                    t.join(timeout=5)


if __name__ == "__main__":
    unittest.main(verbosity=2)
