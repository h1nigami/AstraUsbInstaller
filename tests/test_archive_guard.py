"""Архив не должен записываться на системный диск даже при наличии метки."""

import os
import tempfile
import time
import threading
from types import SimpleNamespace
import unittest
from unittest import mock

import usb_monitor as um


class SystemDiskGuardTest(unittest.TestCase):
    def test_system_directory_cannot_be_marked(self):
        with tempfile.TemporaryDirectory() as dest:
            self.assertFalse(um.ensure_dest_marker(dest))
            self.assertFalse(os.path.exists(os.path.join(dest, um.DEST_MARKER_FILE)))

    def test_existing_marker_cannot_authorize_system_disk(self):
        with tempfile.TemporaryDirectory() as dest:
            with open(os.path.join(dest, um.DEST_MARKER_FILE), "w"):
                pass
            with mock.patch.object(um, "_load_config", return_value={"backup_dest": dest}):
                self.assertFalse(um.dest_available())

    def test_default_destination_cannot_bypass_system_disk_guard(self):
        with tempfile.TemporaryDirectory() as dest, \
             mock.patch.object(um, "_load_config", return_value={}), \
             mock.patch.dict(os.environ, {"USB_BACKUP_DEST": dest}):
            self.assertFalse(um.dest_available())

    def test_direct_copy_cannot_create_archive_on_system_disk(self):
        with tempfile.TemporaryDirectory() as root:
            src = os.path.join(root, "source")
            dest = os.path.join(root, "archive")
            os.mkdir(src)
            video = os.path.join(src, "video.mp4")
            with open(video, "wb") as out:
                out.write(b"original")
            with self.assertRaises(OSError):
                um._copy_files(src, dest, "stamp", "1", 1, 8,
                               None, None, time.time())
            self.assertFalse(os.path.exists(dest))
            with open(video, "rb") as original:
                self.assertEqual(original.read(), b"original")

    def test_restart_cannot_authorize_shadow_directory(self):
        with tempfile.TemporaryDirectory() as dest, \
             mock.patch.object(um, "_load_config", return_value={"backup_dest": dest}), \
             mock.patch.object(um, "_init_db"), \
             mock.patch.object(um, "get_removable_drives", return_value=set()), \
             mock.patch.object(um, "_get_linux_partitions", return_value={}), \
             mock.patch.object(um, "MOUNT_BASE", dest):
            stop = threading.Event()
            stop.set()
            um.monitor_usb(stop_event=stop)
            self.assertFalse(os.path.exists(os.path.join(dest, um.DEST_MARKER_FILE)))
            self.assertFalse(um.dest_available())

    def test_export_cannot_write_payload_to_system_disk(self):
        with tempfile.TemporaryDirectory() as dest:
            source = os.path.join(dest, "original")
            target = os.path.join(dest, "export")
            with open(source, "wb") as out:
                out.write(b"original")
            with self.assertRaises(OSError):
                um._copy_archive_file(source, target)
            self.assertFalse(os.path.exists(target))

    def test_copy_task_refuses_system_archive_without_deleting_source(self):
        import queue
        with tempfile.TemporaryDirectory() as root:
            source = os.path.join(root, "camera")
            destination = os.path.join(root, "archive")
            os.makedirs(os.path.join(source, "DCIM"))
            os.mkdir(destination)
            video = os.path.join(source, "DCIM", "A11_1234567_222222_20260915120000_0001.mp4")
            with open(video, "wb") as out:
                out.write(b"original")
            events = queue.Queue()
            with mock.patch.object(um, "DB_PATH", os.path.join(root, "devices.db")), \
                 mock.patch.object(um, "_load_config", return_value={}), \
                 mock.patch.object(um, "get_dest_base", return_value=destination), \
                 mock.patch.object(um, "_get_drive_label_linux", return_value="CAM"), \
                 mock.patch.object(um, "_get_device_serial_linux", return_value="SERIAL"), \
                 mock.patch.object(um.platform, "system", return_value="Linux"):
                um._init_db().close()
                result = um.copy_task(source, source, "guard-test", None, None, progress_queue=events)
            self.assertEqual(result, (1234567, 0, 0))
            self.assertEqual(os.listdir(destination), [])
            with open(video, "rb") as original:
                self.assertEqual(original.read(), b"original")
            states = []
            while not events.empty():
                states.append(events.get_nowait()[2])
            self.assertEqual(states[-1], "error")


@unittest.skipIf(os.name == "nt", "Проверка устройств и дескрипторов Linux")
class LinuxArchiveGuardTest(unittest.TestCase):
    def test_missing_environment_subdirectory_is_created_only_on_allowed_disk(self):
        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(um, "_load_config", return_value={}):
            destination = os.path.join(root, "new", "archive")
            with mock.patch.dict(os.environ, {"USB_BACKUP_DEST": destination}):
                self.assertFalse(um.dest_available())
                self.assertFalse(os.path.exists(os.path.join(root, "new")))
                with mock.patch.object(um, "_require_archive_device"):
                    self.assertTrue(um.dest_available())
                    self.assertTrue(os.path.isdir(destination))

    def test_uuid_is_read_from_open_directory_device_not_mountpoint_name(self):
        with tempfile.TemporaryDirectory() as root:
            device = os.stat(root).st_dev
            actual = f"/dev/block/{os.major(device)}:{os.minor(device)}"
            with mock.patch.object(um, "_get_filesystem_uuid", side_effect=lambda path:
                                   "OPEN-DISK" if path == actual else "REPLACEMENT"), \
                 mock.patch.object(um, "_iter_mounts", return_value=[("/dev/replacement", root)]):
                self.assertTrue(um._dest_identity_matches(root, {"backup_fs_uuid": "OPEN-DISK"}))
                self.assertFalse(um._dest_identity_matches(root, {"backup_fs_uuid": "REPLACEMENT"}))

    def test_existing_symlink_is_not_counted_as_backed_up(self):
        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(um, "_require_archive_device"):
            source, destination = os.path.join(root, "source"), os.path.join(root, "dest")
            os.mkdir(source)
            os.mkdir(destination)
            video = os.path.join(source, "video.mp4")
            with open(video, "wb") as out:
                out.write(b"original")
            os.symlink(video, os.path.join(destination, "video.mp4"))
            copied, size, backed_up, failed = um._copy_files(
                source, destination, "stamp", "1", 1, 8, None, None, time.time())
            self.assertEqual((copied, size, backed_up, failed), (0, 0, set(), 1))
            um._delete_source_videos(source, backed_up)
            self.assertTrue(os.path.exists(video))

    def test_partition_and_lvm_resolve_to_physical_disk(self):
        with tempfile.TemporaryDirectory() as root:
            disk = os.path.join(root, "devices", "pci", "sda")
            partition = os.path.join(disk, "sda2")
            mapped = os.path.join(root, "devices", "virtual", "block", "dm-0")
            os.makedirs(partition)
            os.makedirs(os.path.join(mapped, "slaves"))
            for path, value in ((disk, "8:0"), (partition, "8:2"), (mapped, "253:0")):
                with open(os.path.join(path, "dev"), "w") as out:
                    out.write(value)
            with open(os.path.join(partition, "partition"), "w") as out:
                out.write("2")
            os.symlink(partition, os.path.join(mapped, "slaves", "sda2"))
            realpath = os.path.realpath
            with mock.patch.object(um.os.path, "realpath", side_effect=lambda path:
                                   mapped if path == "/sys/dev/block/253:0" else realpath(path)):
                self.assertEqual(um._physical_disks(os.makedev(253, 0)), {"8:0"})

    def test_another_partition_of_system_disk_is_denied(self):
        with mock.patch.object(um.os, "stat", return_value=SimpleNamespace(st_dev=1)), \
             mock.patch.object(um, "_physical_disks", side_effect=lambda dev: {"8:0"}):
            with self.assertRaises(OSError):
                um._require_archive_device(2)

    def test_separate_physical_disk_is_allowed(self):
        with mock.patch.object(um.os, "stat", return_value=SimpleNamespace(st_dev=1)), \
             mock.patch.object(um, "_physical_disks", side_effect=lambda dev: {"8:0" if dev == 1 else "8:16"}):
            um._require_archive_device(2)

    def test_unknown_physical_device_is_denied(self):
        with mock.patch.object(um.os, "stat", return_value=SimpleNamespace(st_dev=1)), \
             mock.patch.object(um, "_physical_disks", side_effect=OSError("unknown")):
            with self.assertRaises(OSError):
                um._require_archive_device(2)

    def test_wrong_uuid_is_denied_even_with_existing_marker(self):
        with tempfile.TemporaryDirectory() as dest, \
             mock.patch.object(um, "_require_archive_device"), \
             mock.patch.object(um, "_iter_mounts", return_value=[("/dev/sdb1", dest)]), \
             mock.patch.object(um, "_get_filesystem_uuid", return_value="OTHER"), \
             mock.patch.object(um, "_load_config", return_value={
                 "backup_dest": dest, "backup_fs_uuid": "EXPECTED", "backup_mount_relpath": ""}):
            self.assertTrue(um.ensure_dest_marker(dest))
            self.assertFalse(um.dest_available())
            self.assertFalse(um.remember_configured_dest(dest))

    def test_open_directory_survives_mountpoint_path_replacement(self):
        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(um, "_require_archive_device"):
            archive = os.path.join(root, "archive")
            detached = os.path.join(root, "detached")
            os.mkdir(archive)
            source = os.path.join(root, "source")
            with open(source, "wb") as out:
                out.write(b"data")
            with um._archive_directory(archive) as directory:
                os.rename(archive, detached)
                os.mkdir(archive)
                um._copy_archive_file(source, os.path.join(directory, "video.mp4"))
            self.assertEqual(os.listdir(archive), [])
            with open(os.path.join(detached, "video.mp4"), "rb") as result:
                self.assertEqual(result.read(), b"data")

    def test_symlink_and_hardlink_targets_are_not_overwritten(self):
        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(um, "_require_archive_device"):
            source = os.path.join(root, "source")
            victim = os.path.join(root, "victim")
            with open(source, "wb") as out:
                out.write(b"new")
            with open(victim, "wb") as out:
                out.write(b"preserve")
            for make_link in (os.symlink, os.link):
                target = os.path.join(root, "target")
                make_link(victim, target)
                try:
                    with self.assertRaises(FileExistsError):
                        um._copy_archive_file(source, target)
                    with open(victim, "rb") as original:
                        self.assertEqual(original.read(), b"preserve")
                finally:
                    os.unlink(target)

    def test_failed_copy_leaves_source_and_removes_partial_destination(self):
        def fail(src, dst, size):
            dst.write(b"partial")
            raise OSError("disk disconnected")

        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(um, "_require_archive_device"), \
             mock.patch.object(um.shutil, "copyfileobj", side_effect=fail):
            source = os.path.join(root, "source")
            target = os.path.join(root, "target")
            with open(source, "wb") as out:
                out.write(b"original")
            with self.assertRaises(OSError):
                um._copy_archive_file(source, target)
            self.assertFalse(os.path.exists(target))
            with open(source, "rb") as original:
                self.assertEqual(original.read(), b"original")


if __name__ == "__main__":
    unittest.main()
