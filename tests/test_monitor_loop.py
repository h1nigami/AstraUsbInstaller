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


def _forbidden_real_copy(*args, **kwargs):
    """Страховка: в настоящие съёмные диски тест попадать не должен вовсе."""
    raise AssertionError("тест полез в настоящие съёмные диски")


class MonitorLoopTest(unittest.TestCase):
    def test_detects_new_device_then_confirms_removal_after_debounce(self):
        seen_devices = []

        def fake_copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
            seen_devices.append(devname)
            return (0, 0, 0)

        # Poll sequence: absent -> appears -> still present -> disappears -> stays absent.
        poll_sequence = [[], ["sda1"], ["sda1"], [], []]

        def fake_get_partitions():
            if len(poll_sequence) > 1:
                return {d: None for d in poll_sequence.pop(0)}
            return {d: None for d in poll_sequence[0]}

        pq = queue.Queue()
        stop_event = threading.Event()
        interval = 0.05

        with tempfile.TemporaryDirectory() as mount_base, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            # Платформа и обе ветки обнаружения подменяются обязательно. Без
            # этого на Windows monitor_usb уходит в get_removable_drives и
            # copy_task_windows, берёт НАСТОЯЩИЕ вставленные карты, копирует их
            # и удаляет с них видео. Однажды так уехало 127 файлов с двух карт.
            with mock.patch.object(um.platform, "system", lambda: "Linux"), \
                 mock.patch.object(um, "get_removable_drives", lambda: set()), \
                 mock.patch.object(um, "copy_task_windows", _forbidden_real_copy), \
                 mock.patch.object(um, "MOUNT_BASE", mount_base), \
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

        def fake_copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
            seen_devices.append(devname)
            return (0, 0, 0)

        # Long poll interval relative to the blip: one missing poll, then back,
        # repeated forever until the test stops it.
        state = {"i": 0}

        def fake_get_partitions():
            state["i"] += 1
            # Present on every poll except the 3rd one (a single-poll blip).
            return {} if state["i"] == 3 else {"sda1": None}

        pq = queue.Queue()
        stop_event = threading.Event()
        interval = 0.2  # grace = 0.3s, comfortably longer than a single poll gap

        with tempfile.TemporaryDirectory() as mount_base, \
             tempfile.TemporaryDirectory() as data_dir:
            db_path = os.path.join(data_dir, "d.db")
            # Платформа и обе ветки обнаружения подменяются обязательно. Без
            # этого на Windows monitor_usb уходит в get_removable_drives и
            # copy_task_windows, берёт НАСТОЯЩИЕ вставленные карты, копирует их
            # и удаляет с них видео. Однажды так уехало 127 файлов с двух карт.
            with mock.patch.object(um.platform, "system", lambda: "Linux"), \
                 mock.patch.object(um, "get_removable_drives", lambda: set()), \
                 mock.patch.object(um, "copy_task_windows", _forbidden_real_copy), \
                 mock.patch.object(um, "MOUNT_BASE", mount_base), \
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


class BusGlitchTest(unittest.TestCase):
    """Дешёвый хаб при выдёргивании одного устройства передёргивает всю
    линейку. Разовая пропажа всех портов — сбой шины, а не десять
    отключений: иначе станция очищает экран и роняет все выгрузки."""

    def _run(self, gap_polls, bus_grace):
        interval = 0.05
        devs = ["sda1", "sdb1", "sdc1"]
        present = list(devs)
        polls = {"n": 0}

        def fake_get_partitions():
            polls["n"] += 1
            return {d: None for d in present}

        def fake_copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
            time.sleep(5)
            return (0, 0, 0)

        pq = queue.Queue()
        stop_event = threading.Event()
        with tempfile.TemporaryDirectory() as mount_base, \
             tempfile.TemporaryDirectory() as data_dir:
            with mock.patch.object(um.platform, "system", lambda: "Linux"), \
                 mock.patch.object(um, "get_removable_drives", lambda: set()), \
                 mock.patch.object(um, "copy_task_windows", _forbidden_real_copy), \
                 mock.patch.object(um, "BUS_GLITCH_GRACE", bus_grace), \
                 mock.patch.object(um, "MOUNT_BASE", mount_base), \
                 mock.patch.object(um, "DB_PATH", os.path.join(data_dir, "d.db")), \
                 mock.patch.object(um, "_get_linux_partitions", side_effect=fake_get_partitions), \
                 mock.patch.object(um, "copy_task_linux", fake_copy_task_linux):
                t = threading.Thread(target=um.monitor_usb,
                                     args=(interval, stop_event, pq), daemon=True)
                t.start()
                time.sleep(interval * 6)
                present.clear()                      # вся линейка пропала разом
                time.sleep(interval * gap_polls)
                present.extend(devs)                 # шина вернулась целиком
                time.sleep(interval * 8)
                stop_event.set()
                t.join(timeout=5)

        removed = []
        while True:
            try:
                raw = pq.get_nowait()
            except queue.Empty:
                break
            if raw[0] == "_removed_":
                removed.append(raw[1])
        return removed

    def test_whole_bus_blink_is_not_a_removal(self):
        removed = self._run(gap_polls=8, bus_grace=5.0)
        self.assertEqual(removed, [], "пропажа всей линейки — сбой шины, не отключения")

    def test_bus_gone_for_good_is_still_reported(self):
        removed = self._run(gap_polls=8, bus_grace=0.1)
        self.assertTrue(removed, "если линейки нет долго, отключения подтверждаются")
