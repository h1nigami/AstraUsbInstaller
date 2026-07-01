"""Tests for OS-facing helpers in usb_monitor.py, with subprocess/ctypes
mocked out so they run on any Linux dev box without real USB hardware."""

import os
import sys
import json
import subprocess
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


def _cp(stdout="", returncode=0):
    return subprocess.CompletedProcess(args=[], returncode=returncode, stdout=stdout, stderr="")


class SerialLookupLinuxTest(unittest.TestCase):
    def test_udevadm_id_serial(self):
        with mock.patch.object(um.subprocess, "run", return_value=_cp("ID_SERIAL=ABC123\n")):
            self.assertEqual(um._get_device_serial_linux("sda1"), "ABC123")

    def test_udevadm_id_serial_short_fallback(self):
        with mock.patch.object(um.subprocess, "run", return_value=_cp("ID_SERIAL_SHORT=XYZ\n")):
            self.assertEqual(um._get_device_serial_linux("sda1"), "XYZ")

    def test_falls_back_to_lsblk_when_udevadm_fails(self):
        lsblk_out = json.dumps({"blockdevices": [
            {"name": "sda", "children": [{"name": "sda1", "serial": "LSBLKSER"}]}
        ]})

        def fake_run(cmd, **kwargs):
            if cmd[0] == "udevadm":
                raise subprocess.CalledProcessError(1, cmd)
            if cmd[0] == "lsblk":
                return _cp(lsblk_out)
            raise AssertionError(f"unexpected command {cmd}")

        with mock.patch.object(um.subprocess, "run", side_effect=fake_run):
            self.assertEqual(um._get_device_serial_linux("sda1"), "LSBLKSER")

    def test_falls_back_to_by_id_symlink(self):
        def fake_run(cmd, **kwargs):
            raise subprocess.CalledProcessError(1, cmd)

        with mock.patch.object(um.subprocess, "run", side_effect=fake_run), \
             mock.patch.object(um.os, "listdir", return_value=["usb-Vendor_Model_SERIALX"]), \
             mock.patch.object(um.os.path, "realpath", return_value="/dev/sda1"):
            self.assertEqual(um._get_device_serial_linux("sda1"), "usb-Vendor_Model_SERIALX")

    def test_returns_none_when_every_lookup_fails(self):
        def fake_run(cmd, **kwargs):
            raise subprocess.CalledProcessError(1, cmd)

        with mock.patch.object(um.subprocess, "run", side_effect=fake_run), \
             mock.patch.object(um.os, "listdir", side_effect=OSError):
            self.assertIsNone(um._get_device_serial_linux("sda1"))


class DriveLabelLinuxTest(unittest.TestCase):
    def test_finds_label_for_matching_mountpoint(self):
        lsblk_out = json.dumps({"blockdevices": [
            {"name": "sda", "children": [
                {"name": "sda1", "label": "MYLABEL", "mountpoint": "/mnt/usb_backup/sda1"}
            ]}
        ]})
        with mock.patch.object(um.subprocess, "run", return_value=_cp(lsblk_out)):
            self.assertEqual(um._get_drive_label_linux("/mnt/usb_backup/sda1"), "MYLABEL")

    def test_no_match_returns_empty_string(self):
        with mock.patch.object(um.subprocess, "run", side_effect=OSError):
            self.assertEqual(um._get_drive_label_linux("/mnt/usb_backup/sda1"), "")


class PartitionDiscoveryTest(unittest.TestCase):
    def test_get_lsblk_partitions_uses_parse_tree(self):
        data = {"blockdevices": [
            {"name": "sda", "tran": "usb", "type": "disk",
             "children": [{"name": "sda1", "type": "part"}]}
        ]}
        with mock.patch.object(um.subprocess, "run", return_value=_cp(json.dumps(data))):
            self.assertEqual(um._get_lsblk_partitions(), ["sda1"])

    def test_get_lsblk_partitions_returns_empty_on_error(self):
        with mock.patch.object(um.subprocess, "run", side_effect=OSError):
            self.assertEqual(um._get_lsblk_partitions(), [])

    def test_get_sys_block_partitions_swallows_errors(self):
        with mock.patch.object(um.os, "listdir", side_effect=OSError):
            self.assertEqual(um._get_sys_block_partitions(), [])

    def test_get_linux_partitions_prefers_lsblk(self):
        with mock.patch.object(um, "_get_lsblk_partitions", return_value=["sda1"]), \
             mock.patch.object(um, "_get_sys_block_partitions", return_value=["sdb1"]):
            self.assertEqual(um._get_linux_partitions(), ["sda1"])

    def test_get_linux_partitions_falls_back_to_sys_block(self):
        with mock.patch.object(um, "_get_lsblk_partitions", return_value=[]), \
             mock.patch.object(um, "_get_sys_block_partitions", return_value=["sdb1"]):
            self.assertEqual(um._get_linux_partitions(), ["sdb1"])


class MountUnmountTest(unittest.TestCase):
    def test_mount_device_success(self):
        with tempfile.TemporaryDirectory() as mb:
            with mock.patch.object(um, "MOUNT_BASE", mb), \
                 mock.patch.object(um.subprocess, "run", return_value=_cp()) as run:
                mp = um._mount_device("sda1")
            self.assertEqual(mp, os.path.join(mb, "sda1"))
            run.assert_called_once()

    def test_mount_device_falls_back_to_blkid_fstype(self):
        with tempfile.TemporaryDirectory() as mb:
            def fake_run(cmd, **kwargs):
                if cmd[0] == "mount" and "-t" not in cmd:
                    raise subprocess.CalledProcessError(1, cmd, stderr="wrong fs type")
                if cmd[0] == "blkid":
                    return _cp("vfat\n")
                if cmd[0] == "mount" and "-t" in cmd:
                    return _cp()
                raise AssertionError(f"unexpected command {cmd}")

            with mock.patch.object(um, "MOUNT_BASE", mb), \
                 mock.patch.object(um.subprocess, "run", side_effect=fake_run):
                mp = um._mount_device("sda1")
            self.assertEqual(mp, os.path.join(mb, "sda1"))

    def test_mount_device_total_failure_returns_none(self):
        with tempfile.TemporaryDirectory() as mb:
            def fake_run(cmd, **kwargs):
                raise subprocess.CalledProcessError(1, cmd, stderr="no medium found")

            with mock.patch.object(um, "MOUNT_BASE", mb), \
                 mock.patch.object(um.subprocess, "run", side_effect=fake_run):
                self.assertIsNone(um._mount_device("sda1"))

    def test_unmount_removes_dir_on_success(self):
        with tempfile.TemporaryDirectory() as base:
            mp = os.path.join(base, "mnt")
            os.makedirs(mp)
            with mock.patch.object(um.subprocess, "run", return_value=_cp()):
                um._unmount(mp)
            self.assertFalse(os.path.exists(mp))

    def test_unmount_swallows_errors(self):
        with mock.patch.object(um.subprocess, "run", side_effect=OSError):
            um._unmount("/nonexistent/path")  # must not raise


class WindowsFallbackTest(unittest.TestCase):
    """These helpers are only meaningful on Windows; on Linux ctypes.windll
    does not exist, exercising the except-branch fallback they ship for
    exactly that situation."""

    def test_get_removable_drives_is_empty_on_non_windows(self):
        self.assertEqual(um.get_removable_drives(), set())

    def test_get_device_serial_windows_fallback(self):
        self.assertEqual(um._get_device_serial_windows("E"), "WIN_E")

    def test_get_drive_label_windows_fallback(self):
        self.assertEqual(um.get_drive_label_windows("E"), "")


if __name__ == "__main__":
    unittest.main(verbosity=2)
