import os
import shutil
import time
import subprocess
import json
import platform
import sys
import sqlite3
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime

try:
    from rich.progress import Progress, SpinnerColumn, BarColumn, TextColumn, TimeRemainingColumn
    HAS_RICH = True
except ImportError:
    HAS_RICH = False

DEST_BASE = os.environ.get("USB_BACKUP_DEST", os.path.join(os.path.dirname(os.path.abspath(__file__)), "USB_Backups"))
DB_PATH = os.environ.get("USB_DB_PATH", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "devices.db"))
MOUNT_BASE = "/mnt/usb_backup"
MAX_WORKERS = int(os.environ.get("USB_MAX_WORKERS", "10"))
DEBUG = os.environ.get("USB_DEBUG", "0") == "1"
IS_TTY = sys.stdout.isatty()
USE_RICH = HAS_RICH and IS_TTY

_CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "config.json")

VIDEO_EXTS = {".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".mpeg",
              ".m4v", ".3gp", ".ts", ".flv", ".webm", ".m2ts", ".vob", ".mts"}


def get_dest_base():
    """Return the active backup root: config.json > env var > default."""
    try:
        with open(_CONFIG_PATH) as f:
            path = json.load(f).get("backup_dest", "")
        if path:
            return path
    except Exception:
        pass
    return os.environ.get("USB_BACKUP_DEST",
                          os.path.join(os.path.dirname(os.path.abspath(__file__)), "USB_Backups"))


def _delete_source_videos(src_root):
    """Delete video files from the USB source after a successful backup."""
    deleted = 0
    for root, _dirs, files in os.walk(src_root):
        for name in files:
            if os.path.splitext(name)[1].lower() in VIDEO_EXTS:
                fp = os.path.join(root, name)
                try:
                    os.remove(fp)
                    deleted += 1
                except OSError as e:
                    print(f"  Auto-delete skipped {fp}: {e}", flush=True)
    if deleted:
        print(f"  Auto-deleted {deleted} video file(s) from {src_root}", flush=True)
    return deleted


def _format_size(bytes_val):
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if bytes_val < 1024:
            return f"{bytes_val:.1f} {unit}"
        bytes_val /= 1024
    return f"{bytes_val:.1f} PB"


def _format_time(seconds):
    m, s = divmod(int(seconds), 60)
    h, m = divmod(m, 60)
    if h:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m}:{s:02d}"


_docker_progress_cache = {}

def _docker_progress(dev_id, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time):
    key = dev_id
    now = time.time()
    last = _docker_progress_cache.get(key, {"time": 0, "pct": -1})
    pct = (copied_bytes / total_bytes * 100) if total_bytes else 0
    elapsed = now - start_time
    eta = (elapsed / (pct / 100) - elapsed) if pct > 0.5 else 0

    if pct < last["pct"] + 5 and elapsed < 30 and now - last["time"] < 10:
        if pct == 100 and last.get("done"):
            return
        if pct > 0 and pct < 100:
            return

    _docker_progress_cache[key] = {"time": now, "pct": pct, "done": pct >= 100}
    eta_str = _format_time(eta) if pct > 0.5 else "--:--"
    fname = file_name[:45] if file_name else ""
    line = (
        f"[{datetime.now().strftime('%H:%M:%S')}] Device{dev_id}: "
        f"{pct:5.1f}% | {copied_files}/{total_files} files "
        f"| {_format_size(copied_bytes)}/{_format_size(total_bytes)} "
        f"| ETA {eta_str} | {fname}"
    )
    print(line, flush=True)


def _init_db():
    conn = sqlite3.connect(DB_PATH, check_same_thread=False)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS devices (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            serial      TEXT UNIQUE NOT NULL,
            label       TEXT DEFAULT '',
            person      TEXT DEFAULT '',
            first_seen  TEXT NOT NULL,
            last_seen   TEXT NOT NULL
        )
    """)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS backups (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            device_id   INTEGER NOT NULL REFERENCES devices(id),
            dest_path   TEXT NOT NULL,
            total_files INTEGER DEFAULT 0,
            total_bytes INTEGER DEFAULT 0,
            started_at  TEXT NOT NULL,
            finished_at TEXT NOT NULL
        )
    """)
    try:
        conn.execute("ALTER TABLE devices ADD COLUMN person TEXT DEFAULT ''")
    except Exception:
        pass
    conn.commit()
    return conn


DEVICE_ID_FILE = ".astra_id"


def _read_device_id_from_usb(mountpoint):
    if not mountpoint:
        return None
    path = os.path.join(mountpoint, DEVICE_ID_FILE)
    try:
        with open(path) as f:
            val = f.read().strip()
            if val.isdigit():
                return int(val)
    except Exception:
        pass
    return None


def _write_device_id_to_usb(mountpoint, device_id):
    if not mountpoint:
        return
    path = os.path.join(mountpoint, DEVICE_ID_FILE)
    try:
        with open(path, "w") as f:
            f.write(f"{device_id}\n")
    except Exception:
        pass


def _resolve_device_id(conn, mountpoint, serial, label, devname):
    id_from_usb = _read_device_id_from_usb(mountpoint)
    if id_from_usb is not None:
        now = datetime.now().isoformat()
        conn.execute(
            "INSERT OR IGNORE INTO devices (id, serial, label, first_seen, last_seen) VALUES (?, ?, ?, ?, ?)",
            (id_from_usb, serial or "", label or devname, now, now),
        )
        conn.execute("UPDATE devices SET last_seen = ?, label = ? WHERE id = ?",
                     (now, label or devname, id_from_usb))
        conn.commit()
        return id_from_usb

    if serial:
        db_id = _get_device_id_by_serial(conn, serial)
        if db_id is not None:
            _write_device_id_to_usb(mountpoint, db_id)
            return db_id

    new_id = _create_device(conn, serial, label, devname)
    _write_device_id_to_usb(mountpoint, new_id)
    return new_id


def _get_device_id_by_serial(conn, serial):
    if not serial or not conn:
        return None
    cur = conn.execute("SELECT id FROM devices WHERE serial = ?", (serial,))
    row = cur.fetchone()
    return row[0] if row else None


def _create_device(conn, serial, label, devname):
    now = datetime.now().isoformat()
    cur = conn.execute(
        "INSERT INTO devices (serial, label, first_seen, last_seen) VALUES (?, ?, ?, ?)",
        (serial or "", label or devname or "USB", now, now),
    )
    conn.commit()
    did = cur.lastrowid
    print(f"  New device registered: Device{did} ({label or devname or serial})", flush=True)
    return did


def _get_device_serial_linux(devname):
    try:
        result = subprocess.run(
            ["udevadm", "info", "--query=property", f"/dev/{devname}"],
            capture_output=True, text=True, check=True, timeout=5
        )
        for line in result.stdout.splitlines():
            if line.startswith("ID_SERIAL="):
                val = line.split("=", 1)[1].strip()
                if val:
                    return val
            if line.startswith("ID_SERIAL_SHORT="):
                val = line.split("=", 1)[1].strip()
                if val:
                    return val
    except Exception:
        pass
    try:
        result = subprocess.run(
            ["lsblk", "-J", "-o", "NAME,SERIAL"],
            capture_output=True, text=True, check=True, timeout=5
        )
        data = json.loads(result.stdout)

        def walk(devices):
            for dev in devices:
                if dev.get("name") == devname and dev.get("serial"):
                    return dev["serial"]
                for child in dev.get("children", []):
                    if child.get("name") == devname and child.get("serial"):
                        return child["serial"]
                    res = walk([child])
                    if res:
                        return res
            return None
        serial = walk(data.get("blockdevices", []))
        if serial:
            return serial
    except Exception:
        pass
    try:
        target = os.path.realpath(f"/dev/{devname}")
        for entry in os.listdir("/dev/disk/by-id/"):
            if os.path.realpath(f"/dev/disk/by-id/{entry}") == target and "usb-" in entry:
                return entry
    except Exception:
        pass
    return None


def _get_device_serial_windows(drive_letter):
    try:
        import ctypes
        serial = ctypes.c_ulong()
        ctypes.windll.kernel32.GetVolumeInformationW(
            f"{drive_letter}:\\", None, 0, ctypes.byref(serial), None, None, None, 0
        )
        return f"WIN_{serial.value:08X}"
    except Exception:
        return f"WIN_{drive_letter}"


def _scan_drive(drive_path):
    total_files = 0
    total_bytes = 0
    for root, dirs, files in os.walk(drive_path):
        for file in files:
            total_files += 1
            try:
                total_bytes += os.path.getsize(os.path.join(root, file))
            except Exception:
                pass
    return total_files, total_bytes


def _get_drive_label_linux(mountpoint):
    try:
        result = subprocess.run(
            ["lsblk", "-J", "-o", "NAME,LABEL,MOUNTPOINT"],
            capture_output=True, text=True, check=True, timeout=5
        )
        data = json.loads(result.stdout)
        for dev in data.get("blockdevices", []):
            for child in dev.get("children", []):
                if child.get("mountpoint") == mountpoint and child.get("label"):
                    return child["label"]
    except Exception:
        pass
    return ""


def get_removable_drives():
    if platform.system() == "Windows":
        import ctypes
        import string
        drives = []
        for letter in string.ascii_uppercase:
            drive_type = ctypes.windll.kernel32.GetDriveTypeW(f"{letter}:\\")
            if drive_type == 2:
                drives.append(letter)
        return set(drives)
    return set()


def get_drive_label_windows(drive_letter):
    try:
        import ctypes
        buf = ctypes.create_unicode_buffer(256)
        ctypes.windll.kernel32.GetVolumeInformationW(
            f"{drive_letter}:\\", buf, 256, None, None, None, None, 0
        )
        return buf.value or ""
    except Exception:
        return ""


def _get_linux_partitions():
    parts = _get_lsblk_partitions()
    if parts:
        return parts
    return _get_sys_block_partitions()


def _get_lsblk_partitions():
    parts = []
    try:
        result = subprocess.run(
            ["lsblk", "-J", "-o", "NAME,TRAN,TYPE"],
            capture_output=True, text=True, check=True, timeout=5
        )
        data = json.loads(result.stdout)

        def walk(devices, parent_is_usb=False):
            for dev in devices:
                is_usb = dev.get("tran") == "usb" or parent_is_usb
                children = dev.get("children", [])
                if is_usb and dev.get("type") == "part":
                    parts.append(dev["name"])
                elif is_usb and dev.get("type") == "disk":
                    sub = [c["name"] for c in children if c.get("type") == "part"]
                    parts.extend(sub) if sub else parts.append(dev["name"])
                for child in children:
                    walk([child], is_usb)
        walk(data.get("blockdevices", []))
    except Exception:
        pass
    return parts


def _get_sys_block_partitions():
    parts = []
    try:
        for dev in os.listdir("/sys/block"):
            devpath = os.path.join("/sys/block", dev)
            if not os.path.isdir(devpath):
                continue
            removable_path = os.path.join(devpath, "removable")
            if not os.path.exists(removable_path):
                continue
            with open(removable_path) as f:
                if f.read().strip() != "1":
                    continue
            uevent_path = os.path.join(devpath, "uevent")
            if not os.path.exists(uevent_path):
                continue
            with open(uevent_path) as f:
                uevent = f.read().lower()
                is_usb = "usb" in uevent or "DEVTYPE=partition" in uevent
            if not is_usb:
                try:
                    subsystem = os.path.realpath(os.path.join(devpath, "device", "subsystem"))
                    if "usb" not in subsystem:
                        continue
                except Exception:
                    continue
            found = []
            for entry in os.listdir(devpath):
                if entry.startswith(dev) and entry != dev:
                    ep = os.path.join(devpath, entry, "uevent")
                    if os.path.exists(ep):
                        with open(ep) as f:
                            if "DEVTYPE=partition" in f.read():
                                found.append(entry)
            parts.extend(found) if found else parts.append(dev)
    except Exception:
        pass
    return parts


def _mount_device(devname):
    mountpoint = os.path.join(MOUNT_BASE, devname.replace("/", "_"))
    os.makedirs(mountpoint, exist_ok=True)
    try:
        subprocess.run(["mount", f"/dev/{devname}", mountpoint], check=True, capture_output=True, text=True)
        return mountpoint
    except subprocess.CalledProcessError as e:
        detail = e.stderr.strip()
        try:
            blk = subprocess.run(["blkid", "-o", "value", "-s", "TYPE", f"/dev/{devname}"],
                                  capture_output=True, text=True, check=True, timeout=5)
            fstype = blk.stdout.strip()
            if fstype:
                subprocess.run(["mount", "-t", fstype, f"/dev/{devname}", mountpoint],
                                check=True, capture_output=True, text=True)
                return mountpoint
        except Exception:
            pass
        print(f"Mount error /dev/{devname}: {detail}", flush=True)
        return None


def _unmount(mountpoint):
    try:
        subprocess.run(["umount", mountpoint], check=True, capture_output=True)
        os.rmdir(mountpoint)
    except Exception:
        pass


def _copy_files(src_root, dest_root, timestamp, device_id, total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=None):
    copied_files = 0
    copied_bytes = 0
    last_emit_t = 0.0
    for root, dirs, files in os.walk(src_root):
        rel_path = os.path.relpath(root, src_root)
        if rel_path == ".":
            rel_path = ""
        dest_dir = os.path.join(dest_root, rel_path) if rel_path else dest_root
        os.makedirs(dest_dir, exist_ok=True)
        for file_name in files:
            if file_name == DEVICE_ID_FILE:
                continue
            src_file = os.path.join(root, file_name)
            dst_file = os.path.join(dest_dir, file_name)
            try:
                if os.path.exists(dst_file):
                    src_stat = os.stat(src_file)
                    dst_stat = os.stat(dst_file)
                    if src_stat.st_size == dst_stat.st_size and abs(src_stat.st_mtime - dst_stat.st_mtime) < 1:
                        continue
                    base, ext = os.path.splitext(file_name)
                    dst_file = os.path.join(dest_dir, f"{base}_{timestamp}{ext}")
                file_size = os.path.getsize(src_file)
                shutil.copy2(src_file, dst_file)
                copied_files += 1
                copied_bytes += file_size
                if USE_RICH and progress_obj:
                    progress_obj.update(task_id, advance=file_size)
                elif not IS_TTY:
                    _docker_progress(device_id, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time)
                if emit_fn is not None:
                    now = time.time()
                    if now - last_emit_t >= 1.0:
                        emit_fn("copying", copied_bytes, total_bytes, "")
                        last_emit_t = now
            except Exception:
                pass
    return copied_files, copied_bytes


def copy_task(drive_path, mountpoint, devname, progress_obj, task_id, should_unmount=False, conn=None, progress_queue=None):
    is_linux = platform.system() != "Windows"
    label = _get_drive_label_linux(mountpoint) if is_linux else get_drive_label_windows(drive_path.replace(":\\", ""))

    if is_linux:
        serial = _get_device_serial_linux(devname)
    else:
        serial = _get_device_serial_windows(drive_path.replace(":\\", ""))

    device_id = _resolve_device_id(conn, mountpoint, serial, label or "", devname)
    display_id = f"Device{device_id}"
    started_at = datetime.now()

    ts = started_at.strftime("%Y%m%d_%H%M%S")
    dest = os.path.join(get_dest_base(), display_id)
    os.makedirs(dest, exist_ok=True)

    def _emit(state, current=0, total=0, msg=""):
        if progress_queue is not None:
            try:
                progress_queue.put_nowait((device_id, display_id, state, current, total, msg, devname))
            except Exception:
                pass

    _emit("scanning", 0, 0, f"Scanning {display_id}...")

    if USE_RICH and progress_obj:
        progress_obj.update(task_id, description=f"[cyan]Scanning {display_id}...")
    else:
        print(f"[{started_at.strftime('%H:%M:%S')}] Scanning {display_id} ({label or 'no label'})...", flush=True)

    total_files, total_bytes = _scan_drive(mountpoint)

    if total_files == 0:
        msg = f"Empty: {display_id}"
        _emit("done", 0, 0, msg)
        if USE_RICH and progress_obj:
            progress_obj.update(task_id, description=f"[yellow]{msg}", total=1, completed=1)
        else:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)
        if should_unmount:
            _unmount(mountpoint)
        return device_id, 0, 0

    _emit("copying", 0, total_bytes, f"Copying {display_id}...")

    if USE_RICH and progress_obj:
        progress_obj.update(task_id, description=f"[green]{display_id} ({_format_size(total_bytes)})", total=total_bytes, completed=0)
    else:
        print(f"[{datetime.now().strftime('%H:%M:%S')}] {display_id}: {total_files} files, {_format_size(total_bytes)}", flush=True)

    start_time = time.time()
    copied_files, copied_bytes = _copy_files(mountpoint, dest, ts, device_id, total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=_emit)

    _delete_source_videos(mountpoint)

    if should_unmount:
        _unmount(mountpoint)

    finished_at = datetime.now()
    _emit("done", copied_bytes, total_bytes, f"Done: {display_id}")

    msg = f"Done: {display_id} ({copied_files} files, {_format_size(copied_bytes)})"
    if USE_RICH and progress_obj:
        progress_obj.update(task_id, description=f"[green]{msg}")
    else:
        print(f"[{finished_at.strftime('%H:%M:%S')}] {msg} -> {dest}", flush=True)

    try:
        conn.execute(
            "INSERT INTO backups (device_id, dest_path, total_files, total_bytes, started_at, finished_at) VALUES (?, ?, ?, ?, ?, ?)",
            (device_id, dest, copied_files, copied_bytes, started_at.isoformat(), finished_at.isoformat()),
        )
        conn.commit()
    except Exception:
        pass

    return device_id, copied_files, copied_bytes


def copy_task_windows(drive_letter, progress_obj, task_id, conn, progress_queue=None):
    drive_path = f"{drive_letter}:\\"
    return copy_task(drive_path, drive_path, drive_letter, progress_obj, task_id, conn=conn, progress_queue=progress_queue)


def copy_task_linux(device_path, progress_obj, task_id, conn, progress_queue=None):
    should_unmount = False
    mountpoint = device_path
    if not os.path.ismount(device_path):
        mp = _mount_device(device_path)
        if mp is None:
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[red]Mount failed: {device_path}", total=1, completed=1)
            else:
                print(f"[{datetime.now().strftime('%H:%M:%S')}] Mount failed: {device_path}", flush=True)
            return 0, 0, 0
        mountpoint = mp
        should_unmount = True
    devname = os.path.basename(mountpoint)
    return copy_task(device_path, mountpoint, devname, progress_obj, task_id, should_unmount, conn, progress_queue)


def _make_submit_fn(conn, progress_queue=None):
    def _submit(executor, dev, progress_obj, task_id):
        if platform.system() == "Windows":
            return executor.submit(copy_task_windows, dev, progress_obj, task_id, conn, progress_queue)
        return executor.submit(copy_task_linux, dev, progress_obj, task_id, conn, progress_queue)
    return _submit


def monitor_usb(interval=2, stop_event=None, progress_queue=None):
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass
    system = platform.system()
    is_linux = system != "Windows"

    conn = _init_db()
    print(f"USB Monitor | Platform: {system} | Workers: {MAX_WORKERS} | DB: {DB_PATH}", flush=True)
    print("Waiting for USB devices... (Ctrl+C to stop)", flush=True)

    executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)
    active = {}  # dev → future
    submit = _make_submit_fn(conn, progress_queue)

    if is_linux:
        os.makedirs(MOUNT_BASE, exist_ok=True)
        known = set(_get_linux_partitions())
    else:
        known = get_removable_drives()

    for dev in sorted(known):
        print(f"  Connected: {dev}", flush=True)
        active[dev] = submit(executor, dev, None, None)

    # dev → timestamp of first consecutive miss; cleared when device reappears
    pending_removals = {}

    try:
        while True:
            if stop_event and stop_event.is_set():
                break
            time.sleep(interval)

            done = [dev for dev, f in active.items() if f.done()]
            for dev in done:
                fut = active.pop(dev)
                try:
                    fut.result()
                except Exception:
                    pass

            now_t = time.time()
            current = set(_get_linux_partitions()) if is_linux else get_removable_drives()

            # Devices missing from this poll but still in known
            candidate_removed = known - current

            # Devices that came back — clear their pending counter
            for dev in list(pending_removals):
                if dev not in candidate_removed:
                    pending_removals.pop(dev, None)

            # Record first-miss timestamp for newly disappearing devices
            for dev in candidate_removed:
                if dev not in pending_removals:
                    pending_removals[dev] = now_t

            # Confirm removal only after 1.5× the poll interval has elapsed
            grace = interval * 1.5
            confirmed_removed = {dev for dev, t in pending_removals.items()
                                 if now_t - t >= grace}

            for dev in confirmed_removed:
                pending_removals.pop(dev, None)
                known.discard(dev)
                active.pop(dev, None)
                dn = os.path.basename(dev)
                if progress_queue is not None:
                    try:
                        progress_queue.put_nowait(("_removed_", dn, "", 0, 0, "", ""))
                    except Exception:
                        pass

            # New devices: present in current but not yet in known
            new_devices = sorted(current - known)
            known.update(new_devices)

            for dev in new_devices:
                pending_removals.pop(dev, None)
                print(f"  New USB: {dev}", flush=True)
                active[dev] = submit(executor, dev, None, None)

    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        executor.shutdown(wait=False)
        conn.close()


if __name__ == "__main__":
    monitor_usb()
