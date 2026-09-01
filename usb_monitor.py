import os
import shutil
import time
import subprocess
import json
import platform
import sys
import sqlite3
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta

try:
    from rich.progress import Progress, SpinnerColumn, BarColumn, TextColumn, TimeRemainingColumn
    HAS_RICH = True
except ImportError:
    HAS_RICH = False

DEST_BASE = os.environ.get("USB_BACKUP_DEST", os.path.join(os.path.dirname(os.path.abspath(__file__)), "USB_Backups"))
DB_PATH = os.environ.get("USB_DB_PATH", os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "devices.db"))
MOUNT_BASE = "/mnt/usb_backup"
# How long to wait for the desktop auto-mounter to claim a freshly attached
# device before we mount it ourselves. Reusing the system's own mount avoids a
# second concurrent read-write mount of a FAT/exFAT stick, which corrupts it
# (see _find_existing_mount). Headless/Docker has no auto-mounter, so this is a
# one-time bounded delay per device before self-mounting.
MOUNT_GRACE_SECONDS = float(os.environ.get("USB_MOUNT_GRACE", "4"))
MAX_WORKERS = int(os.environ.get("USB_MAX_WORKERS", "10"))
DEBUG = os.environ.get("USB_DEBUG", "0") == "1"
IS_TTY = sys.stdout.isatty()
USE_RICH = HAS_RICH and IS_TTY

_CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "config.json")
DEST_MARKER_FILE = ".astra_dest"
VERSION_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "VERSION")
_DEST_CFG_RELPATH = "backup_mount_relpath"
_DEST_CFG_UUID = "backup_fs_uuid"
_DEST_CFG_SERIAL = "backup_device_serial"
_DEST_CFG_KEYS = (_DEST_CFG_RELPATH, _DEST_CFG_UUID, _DEST_CFG_SERIAL)

VIDEO_EXTS = {".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".mpeg",
              ".m4v", ".3gp", ".ts", ".flv", ".webm", ".m2ts", ".vob", ".mts"}


def read_version(path=None):
    """Return (tag, date) from the VERSION file, or None when unavailable.

    The file is written by the release workflow and by install_native.sh; a
    missing or malformed file is normal for a source checkout and must never
    break the app.
    """
    try:
        with open(path or VERSION_FILE) as f:
            parts = f.read().split()
    except Exception:
        return None
    if len(parts) != 2:
        return None
    return parts[0], parts[1]


COPYING_MARKER = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                              "data", ".copying")


def touch_copying_marker():
    """Stamp the marker the updater reads to know a backup is in flight."""
    try:
        os.makedirs(os.path.dirname(COPYING_MARKER), exist_ok=True)
        with open(COPYING_MARKER, "w") as f:
            f.write("")
    except OSError:
        pass


def is_copying(path=None, max_age=60):
    """True while a backup is running. A stale or missing marker means idle —
    a crashed GUI must not block updates forever."""
    try:
        return (time.time() - os.path.getmtime(path or COPYING_MARKER)) < max_age
    except OSError:
        return False


def _load_config():
    try:
        with open(_CONFIG_PATH) as f:
            data = json.load(f)
            return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def _save_config(cfg):
    try:
        os.makedirs(os.path.dirname(_CONFIG_PATH), exist_ok=True)
        with open(_CONFIG_PATH, "w") as f:
            json.dump(cfg, f)
        return True
    except Exception:
        return False


def _config_backup_dest():
    """Return the destination explicitly chosen in the GUI (config.json), or None."""
    return _load_config().get("backup_dest", "") or None


def _iter_mounts():
    """Yield (source_device, mountpoint) pairs from /proc/mounts."""
    try:
        with open("/proc/mounts") as f:
            for line in f:
                fields = line.split()
                if len(fields) < 2:
                    continue
                yield (_unescape_mount_field(fields[0]),
                       _unescape_mount_field(fields[1]))
    except Exception:
        return


def _find_mount_for_path(path):
    """Return the most specific mounted filesystem containing ``path``."""
    if not path:
        return None
    try:
        target = os.path.realpath(path)
    except Exception:
        return None

    best = None
    for src, mountpoint in _iter_mounts():
        try:
            real_mp = os.path.realpath(mountpoint)
        except Exception:
            real_mp = mountpoint
        if target == real_mp or target.startswith(real_mp + os.sep):
            if best is None or len(real_mp) > len(best[1]):
                best = (src, real_mp)
    return best


def _get_filesystem_uuid(devpath):
    if not devpath or not devpath.startswith("/dev/"):
        return None
    try:
        result = subprocess.run(
            ["blkid", "-o", "value", "-s", "UUID", devpath],
            capture_output=True, text=True, check=True, timeout=5
        )
        val = result.stdout.strip()
        return val or None
    except Exception:
        return None


def describe_dest_path(dest_base):
    """Return config fields that let the destination survive mountpoint changes."""
    info = {"backup_dest": dest_base}
    if platform.system() == "Windows":
        return info

    mount = _find_mount_for_path(dest_base)
    if not mount:
        return info

    src, mountpoint = mount
    if not src.startswith("/dev/"):
        return info

    mount_root = os.path.realpath(mountpoint)
    rel = os.path.relpath(os.path.realpath(dest_base), mount_root)
    info[_DEST_CFG_RELPATH] = "" if rel == "." else rel

    fs_uuid = _get_filesystem_uuid(src)
    if fs_uuid:
        info[_DEST_CFG_UUID] = fs_uuid

    devname = os.path.basename(os.path.realpath(src))
    serial = _get_device_serial_linux(devname)
    if serial:
        info[_DEST_CFG_SERIAL] = serial

    return info


def remember_configured_dest(dest_base, update_path=False):
    """Persist destination metadata once the real filesystem is reachable."""
    cfg = _load_config()
    info = describe_dest_path(dest_base)

    for key in _DEST_CFG_KEYS:
        cfg.pop(key, None)
    for key in _DEST_CFG_KEYS:
        if key in info:
            cfg[key] = info[key]

    if update_path:
        cfg["backup_dest"] = dest_base

    return _save_config(cfg)


def _dest_device_matches(src, cfg):
    if not src or not src.startswith("/dev/"):
        return False

    want_uuid = cfg.get(_DEST_CFG_UUID)
    if want_uuid:
        return _get_filesystem_uuid(src) == want_uuid

    want_serial = cfg.get(_DEST_CFG_SERIAL)
    if not want_serial:
        return False

    devname = os.path.basename(os.path.realpath(src))
    return _get_device_serial_linux(devname) == want_serial


def _resolve_configured_dest(cfg):
    dest = cfg.get("backup_dest", "") or None
    if not dest:
        return None

    # Fast path: the originally selected path is still the live mounted one.
    if os.path.isdir(dest) and os.path.isfile(os.path.join(dest, DEST_MARKER_FILE)):
        return dest

    if platform.system() == "Windows":
        return dest

    rel = cfg.get(_DEST_CFG_RELPATH)
    if rel is None:
        return dest

    for src, mountpoint in _iter_mounts():
        if not _dest_device_matches(src, cfg):
            continue
        candidate = os.path.join(mountpoint, rel) if rel else mountpoint
        if os.path.isdir(candidate):
            return candidate

    return dest


def get_dest_base():
    """Return the active backup root: config.json > env var > default."""
    cfg = _load_config()
    path = _resolve_configured_dest(cfg)
    if path:
        return path
    return os.environ.get("USB_BACKUP_DEST",
                          os.path.join(os.path.dirname(os.path.abspath(__file__)), "USB_Backups"))


def ensure_dest_marker(dest_base):
    """Stamp ``dest_base`` with the destination marker file. True on success.

    The marker is written when the user picks the folder in the GUI, i.e.
    while the disk it lives on is actually mounted. Later backups require it:
    if the disk is gone, the same path is either missing or gets silently
    recreated by ``makedirs`` as a plain directory on the root/overlay
    filesystem — without the marker. Requiring the marker turns that silent
    write into a visible error instead of filling a shadow directory the
    user will never find on the real disk.
    """
    try:
        marker = os.path.join(dest_base, DEST_MARKER_FILE)
        if not os.path.isfile(marker):
            with open(marker, "w") as f:
                f.write("BestCam backup destination marker. Do not delete.\n")
        return True
    except OSError:
        return False


def dest_available():
    """True when the active backup destination may be written to.

    Env/default destinations keep the historical behaviour (created on
    demand). A destination chosen in the GUI must exist AND carry the marker
    written at selection time — see ensure_dest_marker().
    """
    cfg = _load_config()
    cfg_dest = cfg.get("backup_dest", "") or None
    if not cfg_dest:
        return True
    resolved = _resolve_configured_dest(cfg)
    return os.path.isfile(os.path.join(resolved, DEST_MARKER_FILE))


def _is_dest_path(mountpoint):
    """True when the backup destination lives on (or is) this mountpoint."""
    dest = os.path.realpath(get_dest_base())
    mp = os.path.realpath(mountpoint)
    return dest == mp or dest.startswith(mp + os.sep)


def _delete_source_videos(src_root, allowed=None):
    """Delete video files from the USB source after a successful backup.

    Only files whose source path is in ``allowed`` are removed — this guards
    against data loss when a copy failed (and was silently skipped): a video
    that was never backed up must never be deleted from the source. When
    ``allowed`` is None all videos are eligible (legacy behaviour).
    """
    deleted = 0
    for root, _dirs, files in os.walk(src_root):
        for name in files:
            if os.path.splitext(name)[1].lower() in VIDEO_EXTS:
                fp = os.path.join(root, name)
                if allowed is not None and fp not in allowed:
                    continue
                try:
                    os.remove(fp)
                    deleted += 1
                except OSError as e:
                    print(f"  Auto-delete skipped {fp}: {e}", flush=True)
    if deleted:
        print(f"  Auto-deleted {deleted} video file(s) from {src_root}", flush=True)
    return deleted


def cleanup_old_backup_videos(dest_base=None, older_than_days=30):
    """Delete video files in dest_base that are older than older_than_days.

    Returns (deleted_count, freed_bytes).
    """
    if dest_base is None:
        dest_base = get_dest_base()
    if not os.path.isdir(dest_base):
        return 0, 0

    cutoff = datetime.now() - timedelta(days=older_than_days)
    cutoff_ts = cutoff.timestamp()

    deleted = 0
    freed = 0
    for root, _dirs, files in os.walk(dest_base):
        for name in files:
            if os.path.splitext(name)[1].lower() not in VIDEO_EXTS:
                continue
            fp = os.path.join(root, name)
            try:
                mtime = os.path.getmtime(fp)
                if mtime < cutoff_ts:
                    size = os.path.getsize(fp)
                    os.remove(fp)
                    deleted += 1
                    freed += size
            except OSError as e:
                print(f"  Cleanup skipped {fp}: {e}", flush=True)

    if deleted:
        print(f"  Auto-cleanup: removed {deleted} video file(s), freed {_format_size(freed)} "
              f"(older than {older_than_days} days)", flush=True)
    return deleted, freed


def _format_size(bytes_val):
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if bytes_val < 1024:
            return f"{bytes_val:.1f} {unit}"
        bytes_val /= 1024
    return f"{bytes_val:.1f} PB"


def format_filter_dt(year, mon, day, hour, minute, second="00"):
    """Build a datetime string for range-filtering ``backups.started_at``.

    ``started_at`` is stored via ``datetime.isoformat()`` which uses a ``T``
    separator (e.g. ``2026-06-30T10:00:00``). The filter MUST use the same
    separator, otherwise the lexicographic comparison breaks: a space (0x20)
    sorts before ``T`` (0x54), so an upper bound built with a space wrongly
    excludes every backup taken on that calendar day.
    """
    return f"{year}-{mon}-{day}T{hour}:{minute}:{second}"


def _format_time(seconds):
    m, s = divmod(int(seconds), 60)
    h, m = divmod(m, 60)
    if h:
        return f"{h}:{m:02d}:{s:02d}"
    return f"{m}:{s:02d}"


_docker_progress_cache = {}

def _docker_progress(label, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time):
    key = label
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
        f"[{datetime.now().strftime('%H:%M:%S')}] {label}: "
        f"{pct:5.1f}% | {copied_files}/{total_files} files "
        f"| {_format_size(copied_bytes)}/{_format_size(total_bytes)} "
        f"| ETA {eta_str} | {fname}"
    )
    print(line, flush=True)


def _connect():
    """Open a fresh SQLite connection for the calling thread.

    Each backup worker uses its own connection (with a busy timeout) instead
    of sharing one across the thread pool, which is not safe for concurrent
    writes and silently dropped backup records under load.
    """
    return sqlite3.connect(DB_PATH, timeout=30)


def _init_db():
    conn = sqlite3.connect(DB_PATH, check_same_thread=False)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS devices (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            serial      TEXT UNIQUE NOT NULL,
            label       TEXT DEFAULT '',
            person      TEXT DEFAULT '',
            name        TEXT DEFAULT '',
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
    try:
        conn.execute("ALTER TABLE devices ADD COLUMN name TEXT DEFAULT ''")
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


def _get_device_name(conn, device_id):
    if not conn or device_id is None:
        return ""
    row = conn.execute("SELECT name FROM devices WHERE id = ?", (device_id,)).fetchone()
    return (row[0] or "") if row else ""


def _friendly_device_label(device_id, name):
    """Human-facing label for a device: its custom name when set, else the
    bare number. The backup folder is named Device{id} regardless and is
    never renamed."""
    return name if name else str(device_id)


def _create_device(conn, serial, label, devname):
    now = datetime.now().isoformat()
    # devices.serial is UNIQUE NOT NULL: a real serial is unusable to more
    # than one device anyway, but when the hardware exposes none at all we
    # must not insert the same "" for every such drive — that collides on
    # the second one and crashes the backup worker with an IntegrityError.
    serial = serial or f"NOSERIAL_{devname or 'usb'}_{now}"
    cur = conn.execute(
        "INSERT INTO devices (serial, label, first_seen, last_seen) VALUES (?, ?, ?, ?)",
        (serial, label or devname or "USB", now, now),
    )
    conn.commit()
    did = cur.lastrowid
    print(f"  New device registered: {did} ({label or devname or serial})", flush=True)
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
            if file == DEVICE_ID_FILE:
                continue  # internal marker, never copied — keep totals honest
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


def _parse_lsblk_tree(data):
    """Return dict mapping USB partition (or whole-disk) devname to mountpoint
    (None if the device is not mounted).

    Pure helper over parsed ``lsblk -J`` output so it can be unit-tested
    without invoking lsblk. A USB disk with partitions yields its partitions
    (each exactly once); a USB disk without partitions yields the disk itself.
    """
    result = {}

    def walk(devices, parent_is_usb=False):
        for dev in devices:
            is_usb = dev.get("tran") == "usb" or parent_is_usb
            children = dev.get("children", [])
            dtype = dev.get("type")
            if is_usb and dtype == "part":
                result[dev["name"]] = dev.get("mountpoint") or None
            elif is_usb and dtype == "disk":
                # Partitions are collected via recursion only (so a disk with
                # partitions is not double-counted); the disk itself is added
                # only when it carries no partition (whole-disk filesystem or
                # whole-disk container such as LUKS).
                if not any(c.get("type") == "part" for c in children):
                    result[dev["name"]] = dev.get("mountpoint") or None
            for child in children:
                walk([child], is_usb)

    walk(data.get("blockdevices", []))
    return result


def _get_lsblk_partitions():
    try:
        result = subprocess.run(
            ["lsblk", "-J", "-o", "NAME,TRAN,TYPE,MOUNTPOINT"],
            capture_output=True, text=True, check=True, timeout=5
        )
        return _parse_lsblk_tree(json.loads(result.stdout))
    except Exception:
        return {}


def _get_sys_block_partitions():
    """Return dict mapping USB devname → None (sysfs has no mountpoint info)."""
    result = {}
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
            if found:
                for p in found:
                    result[p] = None
            else:
                result[dev] = None
    except Exception:
        pass
    return result


def _unescape_mount_field(field):
    """Decode the octal escapes (\\040 space, \\011 tab, \\012 nl, \\134 \\)
    that /proc/mounts uses in the device/mountpoint fields."""
    return (field.replace("\\040", " ").replace("\\011", "\t")
                 .replace("\\012", "\n").replace("\\134", "\\"))


def _find_existing_mount(devname):
    """Return an existing mountpoint of ``/dev/<devname>`` if the device is
    already mounted anywhere (e.g. by the desktop auto-mounter under
    ``/run/user/<uid>/media/...``), else ``None``.

    Mounting a vfat/exFAT stick a *second* time read-write while the desktop
    still holds its own mount lets two uncoordinated FAT caches flush over each
    other and corrupt the filesystem — after which the stick reports 0 B and
    refuses to mount. Preferring the system's existing mount avoids that.

    Reads ``/proc/mounts`` directly (no external tool) so it works headless and
    is cheap to poll.
    """
    dev = f"/dev/{devname}"
    try:
        realdev = os.path.realpath(dev)
    except Exception:
        realdev = dev
    try:
        for src, mountpoint in _iter_mounts():
            if src == dev or os.path.realpath(src) == realdev:
                return mountpoint
    except Exception:
        pass
    return None


def _wait_for_system_mount(devname, timeout):
    """Poll _find_existing_mount for up to ``timeout`` seconds, returning the
    mountpoint as soon as the system mounts the device, else ``None``.

    Gives the desktop auto-mounter a short grace period to claim a just-attached
    device so we reuse its mount instead of racing to create a conflicting
    second one. Returns early the moment a mount appears (desktop case is
    typically well under a second); on headless/Docker it times out and the
    caller self-mounts.
    """
    deadline = time.time() + max(0.0, timeout)
    while True:
        mp = _find_existing_mount(devname)
        if mp and os.path.ismount(mp):
            return mp
        if time.time() >= deadline:
            return None
        time.sleep(0.5)


def _is_own_mount(mountpoint):
    """True when ``mountpoint`` lives under MOUNT_BASE — i.e. one we created and
    may safely unmount. The desktop auto-mounter's own mounts must never be torn
    down by us."""
    if not mountpoint:
        return False
    base = os.path.realpath(MOUNT_BASE)
    mp = os.path.realpath(mountpoint)
    return mp == base or mp.startswith(base + os.sep)


def _mount_device(devname):
    mountpoint = os.path.join(MOUNT_BASE, devname.replace("/", "_"))
    if os.path.ismount(mountpoint):
        # Already mounted — e.g. the destination disk, which is deliberately
        # kept mounted across backups (see copy_task_linux).
        return mountpoint
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


def _copy_files(src_root, dest_root, timestamp, progress_label, total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=None):
    copied_files = 0
    copied_bytes = 0
    failed = 0
    # Source paths that are now safely present at the destination — either just
    # copied or already identical. Only these may be auto-deleted from source.
    backed_up = set()
    last_emit_t = 0.0
    for root, dirs, files in os.walk(src_root):
        rel_path = os.path.relpath(root, src_root)
        if rel_path == ".":
            rel_path = ""
        dest_dir = os.path.join(dest_root, rel_path) if rel_path else dest_root
        try:
            os.makedirs(dest_dir, exist_ok=True)
        except OSError as e:
            # Destination vanished mid-copy (e.g. the disk was pulled) —
            # everything in this directory counts as failed, nothing here may
            # be auto-deleted from the source.
            failed += sum(1 for f in files if f != DEVICE_ID_FILE)
            print(f"  Copy failed into {dest_dir}: {e}", flush=True)
            continue
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
                        backed_up.add(src_file)  # identical copy already exists
                        continue
                    base, ext = os.path.splitext(file_name)
                    dst_file = os.path.join(dest_dir, f"{base}_{timestamp}{ext}")
                file_size = os.path.getsize(src_file)
                shutil.copy2(src_file, dst_file)
                copied_files += 1
                copied_bytes += file_size
                backed_up.add(src_file)
                if USE_RICH and progress_obj:
                    progress_obj.update(task_id, advance=file_size)
                elif not IS_TTY:
                    _docker_progress(progress_label, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time)
                if emit_fn is not None:
                    now = time.time()
                    if now - last_emit_t >= 1.0:
                        emit_fn("copying", copied_bytes, total_bytes, "")
                        last_emit_t = now
            except Exception as e:
                # Copy failed for this file — deliberately NOT added to
                # backed_up, so it will be preserved on the source.
                failed += 1
                print(f"  Copy failed {src_file}: {e}", flush=True)
    return copied_files, copied_bytes, backed_up, failed


def copy_task(drive_path, mountpoint, devname, progress_obj, task_id, should_unmount=False, progress_queue=None):
    is_linux = platform.system() != "Windows"
    label = _get_drive_label_linux(mountpoint) if is_linux else get_drive_label_windows(drive_path.replace(":\\", ""))

    if is_linux:
        serial = _get_device_serial_linux(devname)
    else:
        serial = _get_device_serial_windows(drive_path.replace(":\\", ""))

    # Each worker thread owns its connection; sharing one across the pool is
    # not safe for concurrent writes.
    conn = _connect()
    try:
        device_id = _resolve_device_id(conn, mountpoint, serial, label or "", devname)
        # display_id names the backup folder and must stay stable across
        # renames; friendly is the human-facing label shown in messages/GUI.
        display_id = f"Device{device_id}"
        friendly = _friendly_device_label(device_id, _get_device_name(conn, device_id))
        started_at = datetime.now()

        ts = started_at.strftime("%Y%m%d_%H%M%S")
        dest_base = get_dest_base()

        def _emit(state, current=0, total=0, msg=""):
            if progress_queue is not None:
                try:
                    progress_queue.put_nowait((device_id, friendly, state, current, total, msg, devname))
                except Exception:
                    pass

        if not dest_available():
            # The configured destination is not reachable (its disk is not
            # mounted). Creating the path anyway would silently back up into a
            # shadow directory on the root filesystem, so refuse loudly and,
            # critically, never reach the source auto-delete below.
            msg = f"Диск назначения недоступен: {dest_base}"
            _emit("error", 0, 0, msg)
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[red]{msg}", total=1, completed=1)
            else:
                print(f"[{started_at.strftime('%H:%M:%S')}] {msg} — {friendly} не скопирован", flush=True)
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        dest = os.path.join(dest_base, display_id)
        os.makedirs(dest, exist_ok=True)

        _emit("scanning", 0, 0, f"Scanning {friendly}...")

        if USE_RICH and progress_obj:
            progress_obj.update(task_id, description=f"[cyan]Scanning {friendly}...")
        else:
            print(f"[{started_at.strftime('%H:%M:%S')}] Scanning {friendly} ({label or 'no label'})...", flush=True)

        total_files, total_bytes = _scan_drive(mountpoint)

        if total_files == 0:
            msg = f"Empty: {friendly}"
            _emit("done", 0, 0, msg)
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[yellow]{msg}", total=1, completed=1)
            else:
                print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        _emit("copying", 0, total_bytes, f"Copying {friendly}...")

        if USE_RICH and progress_obj:
            progress_obj.update(task_id, description=f"[green]{friendly} ({_format_size(total_bytes)})", total=total_bytes, completed=0)
        else:
            print(f"[{datetime.now().strftime('%H:%M:%S')}] {friendly}: {total_files} files, {_format_size(total_bytes)}", flush=True)

        start_time = time.time()
        copied_files, copied_bytes, backed_up, failed = _copy_files(
            mountpoint, dest, ts, friendly, total_files, total_bytes,
            progress_obj, task_id, start_time, emit_fn=_emit)

        # Only delete videos that were actually backed up successfully.
        _delete_source_videos(mountpoint, backed_up)

        if should_unmount:
            _unmount(mountpoint)

        finished_at = datetime.now()
        if failed:
            msg = f"Ошибки: {friendly} — {failed} файл(ов) не скопировано ({copied_files} успешно)"
            _emit("error", copied_bytes, total_bytes, msg)
        else:
            msg = f"Done: {friendly} ({copied_files} files, {_format_size(copied_bytes)})"
            _emit("done", copied_bytes, total_bytes, f"Done: {friendly}")

        if USE_RICH and progress_obj:
            color = "red" if failed else "green"
            progress_obj.update(task_id, description=f"[{color}]{msg}")
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
    finally:
        conn.close()


def copy_task_windows(drive_letter, progress_obj, task_id, progress_queue=None):
    drive_path = f"{drive_letter}:\\"
    return copy_task(drive_path, drive_path, drive_letter, progress_obj, task_id, progress_queue=progress_queue)


def copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
    should_unmount = False
    if not (mountpoint and os.path.ismount(mountpoint)):
        # The lsblk mountpoint captured at detection can be stale: the desktop
        # auto-mounter may have (or may be about to) mount the device since
        # then. Prefer a mount the system already owns, giving it a short grace
        # period to appear. Creating our own *second* read-write mount while the
        # desktop also holds one lets two uncoordinated FAT caches corrupt the
        # stick (0 B, refuses to remount). Only when no system mount shows up
        # (headless / Docker) do we mount it ourselves and own the unmount.
        existing = _wait_for_system_mount(devname, MOUNT_GRACE_SECONDS)
        if existing:
            mountpoint = existing
        else:
            mp = _mount_device(devname)
            if mp is None:
                if USE_RICH and progress_obj:
                    progress_obj.update(task_id, description=f"[red]Mount failed: {devname}", total=1, completed=1)
                else:
                    print(f"[{datetime.now().strftime('%H:%M:%S')}] Mount failed: {devname}", flush=True)
                return 0, 0, 0
            mountpoint = mp
            should_unmount = _is_own_mount(mp)
    if _is_dest_path(mountpoint):
        # This drive hosts the backup destination — it is not a source to back
        # up, and it must STAY mounted: unmounting it here (and rmdir'ing the
        # mountpoint) is what used to make later backups silently recreate the
        # path as a plain directory on the root filesystem, so the interface
        # reported success while the real disk stayed empty.
        cfg_dest = _config_backup_dest()
        resolved_dest = get_dest_base()
        if cfg_dest and os.path.isdir(resolved_dest):
            ensure_dest_marker(resolved_dest)
            remember_configured_dest(resolved_dest, update_path=False)
        print(f"  Destination drive connected, keeping mounted: {mountpoint}", flush=True)
        if progress_queue is not None:
            try:
                progress_queue.put_nowait(("_status_", "", "info", 0, 0,
                                           f"Диск назначения подключён: {os.path.basename(mountpoint)}", ""))
            except Exception:
                pass
        return 0, 0, 0
    return copy_task(devname, mountpoint, devname, progress_obj, task_id, should_unmount, progress_queue)


def _make_submit_fn(progress_queue=None):
    def _submit(executor, dev, mountpoint, progress_obj, task_id):
        if platform.system() == "Windows":
            return executor.submit(copy_task_windows, dev, progress_obj, task_id, progress_queue)
        return executor.submit(copy_task_linux, dev, mountpoint, progress_obj, task_id, progress_queue)
    return _submit


def monitor_usb(interval=2, stop_event=None, progress_queue=None):
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass
    system = platform.system()
    is_linux = system != "Windows"

    _init_db().close()  # ensure schema + migrations; workers open their own conn

    cfg_dest = _config_backup_dest()
    if cfg_dest:
        resolved_dest = get_dest_base()
        if os.path.isdir(resolved_dest):
            # Configs saved before the marker existed get stamped here, while
            # the destination is actually reachable. Also backfill the
            # filesystem UUID / relative path so the same disk is recognised
            # even if native mode mounts it under /mnt/usb_backup later.
            ensure_dest_marker(resolved_dest)
            remember_configured_dest(resolved_dest, update_path=False)
        else:
            print(f"WARNING: backup destination is not available yet: {resolved_dest} "
                  f"(backups will fail until its disk is mounted)", flush=True)

    print(f"USB Monitor | Platform: {system} | Workers: {MAX_WORKERS} | DB: {DB_PATH}", flush=True)
    print("Waiting for USB devices... (Ctrl+C to stop)", flush=True)

    executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)
    active = {}  # dev → future
    submit = _make_submit_fn(progress_queue)

    if is_linux:
        os.makedirs(MOUNT_BASE, exist_ok=True)
        known = _get_linux_partitions()  # dict: devname → mountpoint
    else:
        known = get_removable_drives()

    for dev in sorted(known):
        mp = known[dev] if is_linux else None
        print(f"  Connected: {dev}", flush=True)
        active[dev] = submit(executor, dev, mp, None, None)

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
            current = _get_linux_partitions() if is_linux else get_removable_drives()

            known_keys = set(known) if is_linux else known
            current_keys = set(current) if is_linux else current

            # Devices missing from this poll but still in known
            candidate_removed = known_keys - current_keys

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
                if is_linux:
                    known.pop(dev, None)
                else:
                    known.discard(dev)
                active.pop(dev, None)
                dn = os.path.basename(dev)
                if progress_queue is not None:
                    try:
                        progress_queue.put_nowait(("_removed_", dn, "", 0, 0, "", ""))
                    except Exception:
                        pass

            # New devices: present in current but not yet in known
            new_devices = sorted(current_keys - known_keys)

            for dev in new_devices:
                if is_linux:
                    known[dev] = current[dev]
                else:
                    known.add(dev)
                pending_removals.pop(dev, None)
                mp = current[dev] if is_linux else None
                print(f"  New USB: {dev}", flush=True)
                active[dev] = submit(executor, dev, mp, None, None)

    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        executor.shutdown(wait=False)


if __name__ == "__main__":
    monitor_usb()
