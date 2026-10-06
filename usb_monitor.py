import os
import shutil
import time
import subprocess
import errno
import json
import platform
import sys
import sqlite3
import threading
import stat
import tempfile
import traceback
from contextlib import contextmanager
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
# Ждём системное монтирование, чтобы не создать второе подключение FAT/exFAT
# на запись. Без автомонтирования задержка ограничена этим интервалом.
MOUNT_GRACE_SECONDS = float(os.environ.get("USB_MOUNT_GRACE", "4"))
MAX_WORKERS = int(os.environ.get("USB_MAX_WORKERS", "10"))
# Сколько ждать возврата шины, когда из опроса разом пропали все устройства.
# Дешёвый многопортовый хаб при выдёргивании одной карты передёргивает всю
# линейку: порты пропадают на секунду-другую целиком. Человек так не делает —
# он вынимает по одному, поэтому одновременное исчезновение всех считаем
# сбоем шины. Дольше этого срока — верим, что хаб действительно отключили.
BUS_GLITCH_GRACE = float(os.environ.get("USB_BUS_GLITCH_GRACE", "20"))
# Сколько подряд идущих отказов чтения считать потерей носителя. Когда хаб
# сбрасывает порт, ядро отдаёт ошибку на каждом файле: перебирать после этого
# всю карту бессмысленно — это минуты впустую и простыня в журнале вместо
# одного внятного сообщения оператору.
IO_ERRORS_TO_GIVE_UP = int(os.environ.get("USB_IO_ERRORS_LIMIT", "5"))
# Сколько ждать возвращения карты, которую сбросило шиной, и сколько раз
# пробовать. Хаб отдаёт устройство обратно под новым именем, и по журналам
# станции на это уходит до минуты. Ждать дешевле, чем бросать работу: копия
# продолжается с того же места, а оператор видит одну плитку, а не цирк.
CARD_RETURN_WAIT = float(os.environ.get("USB_CARD_RETURN_WAIT", "120"))
CARD_RETURN_RETRIES = int(os.environ.get("USB_CARD_RETURN_RETRIES", "3"))
# Носитель отвечает ошибкой — сброшен шиной, но ещё числится подключённым.
# EREMOTEIO есть не на всех платформах, поэтому берём его осторожно.
_LOST_DEVICE_ERRNOS = {errno.EIO, errno.ENODEV, errno.ENXIO,
                       getattr(errno, "EREMOTEIO", errno.EIO)}
# Файлы просто исчезли: так выглядит физически выдернутая карта — точка
# монтирования пустеет. Само по себе это ещё не потеря носителя (файл мог
# исчезнуть между сканированием и копированием), поэтому проверяется отдельно.
_GONE_ERRNOS = {errno.ENOENT, getattr(errno, "ESTALE", errno.ENOENT)}
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


class DeviceLost(OSError):
    """Носитель пропал или сброшен шиной — читать с него больше нечего."""


# Безопасное извлечение: оператор останавливает копирование перед тем, как
# вынимать регистраторы. Тогда выдёргивать нечего — нет ни одного открытого
# чтения, и хаб не сбрасывает соседние порты.
_safe_removal = False
_stop_generation = 0
_worker_local = threading.local()
_submitted_jobs = set()
_interrupted_devices = set()
_failed_unmounts = set()
_foreign_mounts = set()


def set_safe_removal(on):
    """Включить или снять остановку копирования для извлечения карт."""
    global _safe_removal, _stop_generation
    with _operations_lock:
        if on and not _safe_removal:
            _stop_generation += 1
        _safe_removal = bool(on)
    print(f"  Безопасное извлечение: {'включено' if _safe_removal else 'снято'}", flush=True)


def safe_removal_active():
    return _safe_removal


def _stop_requested():
    return (_safe_removal or
            getattr(_worker_local, "generation", _stop_generation) != _stop_generation)


def safe_removal_status():
    with _operations_lock:
        if not _safe_removal:
            return "running", "Копирование разрешено"
        if _operation_readers or _operation_writer or any(not future.done() for future in _submitted_jobs):
            return "stopping", "Останавливаем задания и освобождаем носители"
        for mounts in (_failed_unmounts, _foreign_mounts):
            mounts.intersection_update(path for path in tuple(mounts) if os.path.ismount(path))
        if _failed_unmounts:
            return "blocked", "Не удалось размонтировать носитель. Извлекать его пока нельзя"
        if _foreign_mounts:
            return "blocked", "Копирование остановлено. Выполните безопасное извлечение в системе"
        return "ready", "Задания остановлены. Можно извлекать регистраторы"


@contextmanager
def _worker_guard(devname):
    with operation_guard():
        outer = not hasattr(_worker_local, "generation")
        if outer:
            _worker_local.generation = _stop_generation
            _worker_local.completed = False
        try:
            yield
        finally:
            if outer:
                if _stop_requested() and not _worker_local.completed:
                    with _operations_lock:
                        _interrupted_devices.add(devname)
                del _worker_local.generation
                del _worker_local.completed


_awaiting_cards = {}
_awaiting_lock = threading.Lock()


def _await_card_register(fs_uuid, seconds):
    """Пометить карту как ожидаемую: пока метка здесь, новый воркер на неё
    не поднимается — её доигрывает тот, кто уже начал."""
    if fs_uuid:
        with _awaiting_lock:
            _awaiting_cards[fs_uuid] = time.time() + seconds


def _await_card_clear(fs_uuid):
    if fs_uuid:
        with _awaiting_lock:
            _awaiting_cards.pop(fs_uuid, None)


def _card_is_awaited(fs_uuid):
    if not fs_uuid:
        return False
    with _awaiting_lock:
        until = _awaiting_cards.get(fs_uuid)
        if until and until > time.time():
            return True
        _awaiting_cards.pop(fs_uuid, None)
        return False


def _find_card_by_uuid(fs_uuid):
    """Имя устройства, под которым сейчас видна карта с этой меткой."""
    if not fs_uuid:
        return None
    try:
        devices = _get_linux_partitions()
    except OSError:
        return None  # опрос не удался — ждём дальше
    for devname in devices:
        if _get_filesystem_uuid(f"/dev/{devname}") == fs_uuid:
            return devname
    return None


def _wait_for_card(fs_uuid, timeout, stop_check=None):
    """Дождаться возвращения той же карты под любым именем устройства."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if _stop_requested() or (stop_check is not None and stop_check()):
            return None
        devname = _find_card_by_uuid(fs_uuid)
        if devname:
            return devname
        time.sleep(2)
    return None


def _mountpoint_for(devname):
    """Точка монтирования устройства: чужая, если система уже смонтировала,
    иначе своя. Возвращает (точка, надо ли отмонтировать самим)."""
    existing = _wait_for_system_mount(devname, MOUNT_GRACE_SECONDS)
    if existing:
        own = _is_own_mount(existing)
        if not own:
            with _operations_lock:
                _foreign_mounts.add(existing)
        return existing, own
    if _stop_requested():
        return None, False
    mp = _mount_device(devname)
    if mp is None:
        return None, False
    return mp, _is_own_mount(mp)


def _source_gone(src_root):
    """Носителя больше нет? Пропавший одиночный файл — ещё не потеря карты."""
    try:
        return not os.listdir(src_root)
    except OSError:
        return True


def read_version(path=None):
    """Return (tag, date) from the VERSION file, or None when unavailable.

    The file is written by the release workflow and by install_native.sh; a
    missing or malformed file is normal for a source checkout and must never
    break the app.
    """
    try:
        with open(path or VERSION_FILE) as f:
            parts = f.read().split()
    except (OSError, UnicodeError):
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
    except OSError as error:
        # Без метки апдейтер считает станцию свободной и может перезапустить её посреди копирования.
        _log_once("copying_marker", f"Не удалось обновить метку копирования {COPYING_MARKER}: {error}")
        return
    _log_once("copying_marker", None)


def is_copying(path=None, max_age=60):
    """True while a backup is running. A stale or missing marker means idle —
    a crashed GUI must not block updates forever."""
    try:
        return (time.time() - os.path.getmtime(path or COPYING_MARKER)) < max_age
    except OSError:
        return False


LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "app.log")
LOG_MAX_BYTES = 1024 * 1024
ARCHIVE_LOG_NAME = "astra-usb-monitor.log"

_log_once_last = {}


def _log_once(key, msg):
    """Писать сообщение о сбое, только когда оно меняется; None — сбой прошёл.

    Опрос идёт каждые 2 с, и повторяющийся сбой иначе забил бы журнал.
    """
    if msg is None:
        _log_once_last.pop(key, None)
    elif _log_once_last.get(key) != msg:
        _log_once_last[key] = msg
        print(msg, flush=True)


_orig_stdout = None
_orig_stderr = None
_log_fh = None
_log_path = None


class _LogTee:
    """Дублирует print в файл. Один flush на запись, чтобы строки не терялись при падении."""

    def __init__(self, stream, fh):
        self._stream = stream
        self._fh = fh
        self._failing = False
        self._line_start = True

    def write(self, data):
        self._stream.write(data)
        # В файле у каждой строки своя дата: в systemd её ставит journald, здесь — мы.
        stamped = []
        for part in data.splitlines(keepends=True):
            if self._line_start and part.strip():
                stamped.append(datetime.now().strftime("%d.%m.%Y %H:%M:%S  "))
            stamped.append(part)
            self._line_start = part.endswith("\n")
        try:
            self._fh.write("".join(stamped))
            self._fh.flush()
        except ValueError:
            # Старый файл закрыт при переезде журнала: следующая строка уйдёт в новый.
            return
        except OSError as error:
            # Сказать один раз и только в исходный поток (systemd): файл больше не пишется.
            if not self._failing:
                self._failing = True
                self._stream.write(f"Запись журнала в файл остановилась: {error}\n")
            return
        self._failing = False

    def writelines(self, lines):
        for line in lines:
            self.write(line)

    def flush(self):
        try:
            self._stream.flush()
            self._fh.flush()
        except (OSError, ValueError):
            pass

    def isatty(self):
        try:
            return self._stream.isatty()
        except (AttributeError, ValueError):
            return False


def setup_file_logging(path=None):
    """Дублировать stdout/stderr в файл. Повторный вызов с другим путём переносит запись туда."""
    global _orig_stdout, _orig_stderr, _log_fh, _log_path
    target = path or LOG_PATH
    if _orig_stdout is not None and target == _log_path:
        return True
    try:
        os.makedirs(os.path.dirname(target), exist_ok=True)
        if os.path.isfile(target) and os.path.getsize(target) > LOG_MAX_BYTES:
            with open(target, "rb") as f:
                f.seek(-LOG_MAX_BYTES // 2, os.SEEK_END)
                tail = f.read()
            with open(target, "wb") as f:
                f.write(tail)
        fh = open(target, "a", encoding="utf-8", errors="replace")
    except OSError as error:
        print(f"Журнал не пишется в файл {target}: {error}", flush=True)
        return False
    old_fh, _log_fh, _log_path = _log_fh, fh, target
    if _orig_stdout is None:
        _orig_stdout, _orig_stderr = sys.stdout, sys.stderr
        sys.stdout = _LogTee(_orig_stdout, fh)
        sys.stderr = _LogTee(_orig_stderr, fh)
    else:
        sys.stdout._fh = sys.stderr._fh = fh
        try:
            old_fh.close()
        except OSError:
            pass
    return True


def update_log_location(start=False):
    """Писать журнал в корень архива, пока его диск на месте, иначе в data/app.log.

    Запись в файл включает только старт (start=True). Смена папки архива и
    возврат её диска лишь переносят уже включённый журнал.
    """
    if not start and _orig_stdout is None:
        return False
    try:
        target = os.path.join(get_dest_base(), ARCHIVE_LOG_NAME) if dest_available() else LOG_PATH
    except Exception:
        target = LOG_PATH
    return setup_file_logging(target)


def restore_stdout():
    """Вернуть stdout/stderr. Нужно только тестам; в бою не вызывается."""
    global _orig_stdout, _orig_stderr, _log_fh, _log_path
    if _orig_stdout is not None:
        sys.stdout, sys.stderr = _orig_stdout, _orig_stderr
        _orig_stdout, _orig_stderr = None, None
    if _log_fh is not None:
        try:
            _log_fh.close()
        except OSError:
            pass
        _log_fh = None
    _log_path = None


def collect_diagnostics(db_path=None):
    """Сводка для выгрузки: только счётчики, без имён, серийников и людей."""
    info = {
        "version": "unknown",
        "platform": platform.system(),
        "devices": 0,
        "backups": 0,
        "archive": get_dest_base(),
        "archive_bytes": 0,
    }
    ver = read_version()
    if ver:
        info["version"] = f"{ver[0]} {ver[1]}"
    try:
        conn = _connect(db_path)
    except sqlite3.Error:
        conn = None
    if conn is not None:
        try:
            tables = {row[0] for row in conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")}
            if "devices" in tables:
                info["devices"] = conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0]
            if "backups" in tables:
                info["backups"] = conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0]
        except sqlite3.Error:
            pass
        finally:
            conn.close()
    total = 0
    try:
        for root, _dirs, files in os.walk(info["archive"]):
            for name in files:
                try:
                    total += os.path.getsize(os.path.join(root, name))
                except OSError:
                    pass
    except OSError:
        pass
    info["archive_bytes"] = total
    return info


def export_logs(dest_dir, db_path=None, log_path=None):
    """Собрать app.log + version.txt + summary.txt в подпапку dest_dir.

    Возвращает путь пакета. Персональных данных (имена, люди, серийники)
    в пакете нет — только счётчики.
    """
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    bundle = os.path.join(dest_dir, f"bestcam-logs-{stamp}")
    os.makedirs(bundle, exist_ok=False)
    src_log = log_path or _log_path or LOG_PATH
    try:
        if os.path.isfile(src_log):
            shutil.copy2(src_log, os.path.join(bundle, "app.log"))
        else:
            with open(os.path.join(bundle, "app.log"), "w") as f:
                f.write("Журнал пуст: станция перезапускалась до включения записи в файл.\n")
    except OSError as e:
        raise OSError(f"Не удалось скопировать журнал: {e}") from e
    ver = read_version()
    try:
        with open(os.path.join(bundle, "version.txt"), "w") as f:
            f.write(f"{ver[0]} {ver[1]}\n" if ver else "unknown\n")
        d = collect_diagnostics(db_path)
        with open(os.path.join(bundle, "summary.txt"), "w") as f:
            f.write(
                "BestCam USB Backup Manager — диагностика\n"
                f"version: {d['version']}\n"
                f"platform: {d['platform']}\n"
                f"devices: {d['devices']}\n"
                f"backups: {d['backups']}\n"
                f"archive: {d['archive']}\n"
                f"archive_bytes: {d['archive_bytes']}\n"
            )
    except OSError as e:
        raise OSError(f"Не удалось записать сводку: {e}") from e
    return bundle


_operations_lock = threading.RLock()
_operation_readers = 0
_operation_writer = False


class StationBusy(OSError):
    """Общая блокировка занята: операцию можно повторить позже."""


@contextmanager
def operation_guard(exclusive=False):
    """Не допускать обслуживания архива одновременно с копированием."""
    global _operation_readers, _operation_writer
    fd = None
    with _operations_lock:
        if _operation_writer or (exclusive and _operation_readers):
            raise StationBusy("Станция занята другой операцией")
        if os.name != "nt":
            import fcntl
            path = DB_PATH + ".operations.lock"
            os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
            fd = os.open(path, os.O_CREAT | os.O_RDWR, 0o600)
            try:
                fcntl.flock(fd, (fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH) | fcntl.LOCK_NB)
            except BlockingIOError as error:
                os.close(fd)
                raise StationBusy("Станция занята другой операцией") from error
            except OSError:
                os.close(fd)
                raise
        if exclusive:
            _operation_writer = True
        else:
            _operation_readers += 1
    try:
        yield fd
    finally:
        with _operations_lock:
            if fd is not None:
                os.close(fd)
            if exclusive:
                _operation_writer = False
            else:
                _operation_readers -= 1


@contextmanager
def _maintenance_archive(path):
    with _archive_directory(path) as directory:
        cfg = _load_config()
        marker = os.path.join(directory, DEST_MARKER_FILE)
        if (cfg.get("_config_unreadable") or not _dest_identity_matches(directory, cfg)
                or not stat.S_ISREG(os.stat(marker, follow_symlinks=False).st_mode)):
            raise OSError("Не удалось подтвердить диск архива")
        yield directory


def _device_archive_paths(directory, device_id=None):
    names = [f"Device{device_id}"] if device_id is not None else os.listdir(directory)
    for name in names:
        path = os.path.join(directory, name)
        if (name.startswith("Device") and _positive_id(name[6:])
                and not os.path.islink(path) and os.path.isdir(path)):
            yield path


def factory_reset(db_path=None, dest_base=None, config_path=None):
    """Удалить только папки DeviceN и историю после проверки диска архива."""
    with operation_guard(exclusive=True):
        if is_copying():
            raise OSError("Сброс невозможен: идёт сканирование или копирование")
        dest = dest_base or get_dest_base()
        with _maintenance_archive(dest) as directory:
            entries = 0
            for path in _device_archive_paths(directory):
                archive_device = os.stat(directory).st_dev
                for root, _dirs, _files in os.walk(path):
                    if os.stat(root).st_dev != archive_device:
                        raise OSError("В папке устройства смонтирован другой диск")
                shutil.rmtree(path)
                entries += 1
            devices = backups = 0
            conn = sqlite3.connect(db_path or DB_PATH)
            try:
                tables = {row[0] for row in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'")}
                if "devices" in tables:
                    devices = conn.execute("SELECT COUNT(*) FROM devices").fetchone()[0]
                    conn.execute("DELETE FROM devices")
                if "backups" in tables:
                    backups = conn.execute("SELECT COUNT(*) FROM backups").fetchone()[0]
                    conn.execute("DELETE FROM backups")
                conn.commit()
            finally:
                conn.close()
        if config_path is not None:
            with _config_lock:
                try:
                    os.remove(config_path)
                except FileNotFoundError:
                    pass
        with _device_id_lock:
            _connected_device_ids.clear()
        return {"devices": devices, "backups": backups, "entries": entries}


_config_lock = threading.RLock()


def _load_config(path=None):
    try:
        with open(path or _CONFIG_PATH, encoding="utf-8") as stream:
            data = json.load(stream)
        if not isinstance(data, dict):
            raise ValueError("ожидался объект JSON")
        if "exit_password" in data and (not isinstance(data["exit_password"], str) or not data["exit_password"]):
            raise ValueError("неверный exit_password")
        for key in ("lock_timeout_minutes", "auto_cleanup_days"):
            if key in data and (type(data[key]) is not int or data[key] < 0):
                raise ValueError(f"неверный {key}")
        if "auto_cleanup_enabled" in data and type(data["auto_cleanup_enabled"]) is not bool:
            raise ValueError("неверный auto_cleanup_enabled")
        for key in ("backup_dest", *_DEST_CFG_KEYS):
            if key in data and not isinstance(data[key], str):
                raise ValueError(f"неверный {key}")
    except FileNotFoundError:
        return {}
    except (OSError, ValueError, UnicodeError) as error:
        # Иначе оператор видит только «диск архива недоступен».
        _log_once(("config", path), f"Настройки {path or _CONFIG_PATH} не читаются: {error}")
        return {"_config_unreadable": True}
    _log_once(("config", path), None)
    return data


def _save_config(cfg, path=None, repair=False):
    path = os.path.abspath(path or _CONFIG_PATH)
    temporary = None
    with _config_lock:
        try:
            if cfg.get("_config_unreadable") or (not repair and _load_config(path).get("_config_unreadable")):
                return False
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=os.path.dirname(path),
                                             delete=False) as stream:
                temporary = stream.name
                json.dump(cfg, stream, ensure_ascii=False)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
            return True
        except (OSError, ValueError, TypeError):
            return False
        finally:
            if temporary and os.path.exists(temporary):
                os.unlink(temporary)


def update_config(updates, remove_keys=(), path=None, repair=False):
    with _config_lock:
        cfg = _load_config(path)
        if cfg.get("_config_unreadable"):
            if not repair:
                return False
            cfg = {}
        for key in remove_keys:
            cfg.pop(key, None)
        cfg.update(updates)
        return _save_config(cfg, path, repair=repair)


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
    except (OSError, ValueError) as error:
        _log_once("proc_mounts", f"Не удалось прочитать /proc/mounts: {error}")
        return
    _log_once("proc_mounts", None)


def _find_mount_for_path(path):
    """Return the most specific mounted filesystem containing ``path``."""
    if not path:
        return None
    try:
        target = os.path.realpath(path)
    except (OSError, ValueError):
        return None

    best = None
    for src, mountpoint in _iter_mounts():
        try:
            real_mp = os.path.realpath(mountpoint)
        except (OSError, ValueError):
            real_mp = mountpoint
        if target == real_mp or target.startswith(real_mp.rstrip(os.sep) + os.sep):
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
    except (OSError, subprocess.SubprocessError):
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
    if not _archive_path_allowed(dest_base):
        return False
    if not update_path and not _dest_identity_matches(dest_base, _load_config()):
        return False
    info = describe_dest_path(dest_base)
    updates = {key: info[key] for key in _DEST_CFG_KEYS if key in info}
    if update_path:
        updates["backup_dest"] = dest_base
    return update_config(updates, remove_keys=_DEST_CFG_KEYS)


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
    if (os.path.isdir(dest) and os.path.isfile(os.path.join(dest, DEST_MARKER_FILE))
            and _dest_identity_matches(dest, cfg)):
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


def _physical_disks(device):
    """Физические диски файловой системы, включая разделы, LVM и RAID."""
    pending = [os.path.realpath(f"/sys/dev/block/{os.major(device)}:{os.minor(device)}")]
    seen, disks = set(), set()
    while pending:
        path = pending.pop()
        if path in seen:
            continue
        seen.add(path)
        with open(os.path.join(path, "dev")) as src:
            number = src.read().strip()
        slaves = os.listdir(os.path.join(path, "slaves")) if os.path.isdir(os.path.join(path, "slaves")) else []
        if slaves:
            pending.extend(os.path.realpath(os.path.join(path, "slaves", name)) for name in slaves)
        elif os.path.isfile(os.path.join(path, "partition")):
            pending.append(os.path.dirname(path))
        else:
            # Loop, RAM и прочие виртуальные устройства не доказывают отдельный диск.
            if "/virtual/" in path or not number:
                raise OSError("Не удалось определить физический диск архива")
            disks.add(number)
    if not disks:
        raise OSError("Не удалось определить физический диск архива")
    return disks


def _require_archive_device(device):
    """Запретить архив на системном диске; неизвестное устройство тоже запрещено."""
    if os.name == "nt":
        if device == os.stat(os.environ["SystemRoot"]).st_dev:
            raise OSError("Запись архива на системный диск запрещена")
        return
    system_devices = {os.stat("/").st_dev}
    for path in ("/usr", "/var", "/boot"):
        try:
            system_devices.add(os.stat(path).st_dev)
        except FileNotFoundError:
            pass
    if device in system_devices:
        raise OSError("Запись архива на системный диск запрещена")
    archive_disks = _physical_disks(device)
    for system_device in system_devices:
        if archive_disks & _physical_disks(system_device):
            raise OSError("Запись архива на раздел системного диска запрещена")


@contextmanager
def _archive_directory(path, create=False):
    """Открытый каталог удерживает монтирование при записи на Linux."""
    path = os.path.abspath(path)
    missing = []
    while not os.path.lexists(path):
        if not create:
            raise OSError("Диск архива не подключён")
        parent, name = os.path.split(path)
        if parent == path:
            raise OSError("Диск архива не подключён")
        missing.append(name)
        path = parent
    if os.name == "nt":
        _require_archive_device(os.stat(path).st_dev)
        for name in reversed(missing):
            path = os.path.join(path, name)
            os.makedirs(path, exist_ok=True)
            _require_archive_device(os.stat(path).st_dev)
        yield path
        return
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        device = os.fstat(fd).st_dev
        _require_archive_device(device)
        for name in reversed(missing):
            try:
                os.mkdir(name, dir_fd=fd)
            except FileExistsError:
                pass
            child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
            os.close(fd)
            fd = child
            if os.fstat(fd).st_dev != device:
                raise OSError("Диск назначения сменился")
        yield f"/proc/self/fd/{fd}"
    finally:
        os.close(fd)


def _archive_path_allowed(path, create=False):
    try:
        with _archive_directory(path, create=create):
            pass
    except (OSError, KeyError, ValueError) as error:
        _log_once(("archive", path), f"Папка архива {path} не подходит: {error}")
        return False
    _log_once(("archive", path), None)
    return True


def _dest_identity_matches(path, cfg):
    if os.name == "nt" or not (cfg.get(_DEST_CFG_UUID) or cfg.get(_DEST_CFG_SERIAL)):
        return True
    try:
        device = os.stat(path).st_dev
        return _dest_device_matches(f"/dev/block/{os.major(device)}:{os.minor(device)}", cfg)
    except OSError as error:
        _log_once(("archive", path), f"Не удалось проверить диск архива {path}: {error}")
        return False


def _copy_archive_file(source, destination):
    """Создать новую копию без перезаписи ссылок и с проверкой открытого файла."""
    with _archive_directory(os.path.dirname(destination)) as directory:
        target = os.path.join(directory, os.path.basename(destination))
        with open(source, "rb") as src, open(target, "xb") as dst:
            try:
                _require_archive_device(os.fstat(dst.fileno()).st_dev)
                shutil.copyfileobj(src, dst, 1024 * 1024)
                dst.flush()
                os.fsync(dst.fileno())
                info = os.fstat(src.fileno())
                if os.name != "nt":
                    os.fchmod(dst.fileno(), stat.S_IMODE(info.st_mode))
                os.utime(dst.fileno() if os.utime in os.supports_fd else target,
                         ns=(info.st_atime_ns, info.st_mtime_ns))
            # Любой сбой, даже Ctrl+C: убрать недописанный файл и пробросить дальше.
            except BaseException:
                dst.close()
                os.unlink(target)
                raise


def ensure_dest_marker(dest_base):
    """Создать метку только на проверенном несистемном диске."""

    try:
        with _archive_directory(dest_base) as directory:
            marker = os.path.join(directory, DEST_MARKER_FILE)
            if os.path.lexists(marker):
                return stat.S_ISREG(os.stat(marker, follow_symlinks=False).st_mode)
            with open(marker, "x") as f:
                _require_archive_device(os.fstat(f.fileno()).st_dev)
                f.write("BestCam backup destination marker. Do not delete.\n")
        return True
    except (OSError, KeyError, ValueError) as error:
        _log_once(("archive", dest_base), f"Папка архива {dest_base} не подходит: {error}")
        return False


def dest_available():
    """Проверить реальный диск; метка не разрешает запись на системный раздел."""
    cfg = _load_config()
    if cfg.get("_config_unreadable"):
        return False
    cfg_dest = cfg.get("backup_dest", "") or None
    resolved = _resolve_configured_dest(cfg) if cfg_dest else get_dest_base()
    return (_archive_path_allowed(resolved, create=not cfg_dest) and _dest_identity_matches(resolved, cfg)
            and (not cfg_dest or os.path.isfile(os.path.join(resolved, DEST_MARKER_FILE))))


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
                    print(f"  Не удалось удалить исходный файл {fp}: {e}", flush=True)
    if deleted:
        print(f"  Удалено исходных видео: {deleted} ({src_root})", flush=True)
    return deleted


def cleanup_old_backup_videos(dest_base=None, older_than_days=30, device_id=None):
    """Очистить видео в DeviceN, удерживая проверенный диск и exclusive-блокировку."""
    if device_id is not None:
        device_id = _positive_id(str(device_id))
        if device_id is None:
            raise ValueError("Неверный ID регистратора")
    if older_than_days is None and device_id is None:
        raise ValueError("Для полной очистки укажите ID регистратора")
    cutoff = ((datetime.now() - timedelta(days=older_than_days)).timestamp()
              if older_than_days is not None else float("inf"))
    deleted = freed = 0
    with operation_guard(exclusive=True):
        with _maintenance_archive(dest_base or get_dest_base()) as directory:
            archive_device = os.stat(directory).st_dev
            for path in _device_archive_paths(directory, device_id):
                for root, _dirs, files in os.walk(path):
                    with _archive_directory(root) as opened:
                        if os.stat(opened).st_dev != archive_device:
                            raise OSError("В папке устройства смонтирован другой диск")
                        for name in files:
                            if os.path.splitext(name)[1].lower() not in VIDEO_EXTS:
                                continue
                            fp = os.path.join(opened, name)
                            info = os.stat(fp, follow_symlinks=False)
                            if stat.S_ISREG(info.st_mode) and info.st_mtime < cutoff:
                                os.remove(fp)
                                deleted += 1
                                freed += info.st_size
    if deleted:
        print(f"Очистка архива: удалено видео {deleted}, освобождено {_format_size(freed)}", flush=True)
    return deleted, freed


def _format_size(bytes_val):
    for unit in ("Б", "КБ", "МБ", "ГБ", "ТБ"):
        if bytes_val < 1024:
            return f"{bytes_val:.1f} {unit}"
        bytes_val /= 1024
    return f"{bytes_val:.1f} ПБ"


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


_log_progress_cache = {}

def _log_progress(label, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time):
    key = label
    now = time.time()
    last = _log_progress_cache.get(key, {"time": 0, "pct": -1})
    pct = (copied_bytes / total_bytes * 100) if total_bytes else 0
    elapsed = now - start_time
    eta = (elapsed / (pct / 100) - elapsed) if pct > 0.5 else 0

    if pct < last["pct"] + 5 and elapsed < 30 and now - last["time"] < 10:
        if pct == 100 and last.get("done"):
            return
        if pct > 0 and pct < 100:
            return

    _log_progress_cache[key] = {"time": now, "pct": pct, "done": pct >= 100}
    eta_str = _format_time(eta) if pct > 0.5 else "--:--"
    fname = file_name[:45] if file_name else ""
    line = (
        f"{label}: "
        f"{pct:5.1f}% | {copied_files}/{total_files} файлов "
        f"| {_format_size(copied_bytes)}/{_format_size(total_bytes)} "
        f"| осталось {eta_str} | {fname}"
    )
    print(line, flush=True)


def _connect(db_path=None, timeout=30):
    """Open a fresh SQLite connection for the calling thread.

    Each backup worker uses its own connection (with a busy timeout) instead
    of sharing one across the thread pool, which is not safe for concurrent
    writes and silently dropped backup records under load.
    """
    return sqlite3.connect(db_path or DB_PATH, timeout=timeout)


# ponytail: запросы интерфейса идут на потоке Tk, поэтому ждать базу столько
# же, сколько ждут воркеры, нельзя — киоск замирает целиком. Отказ человек
# видит в диалоге и повторяет. Убрать потолок можно, только уведя запросы
# вкладки «Устройства» в отдельный поток.
GUI_DB_TIMEOUT = 5


def _init_db():
    conn = sqlite3.connect(DB_PATH, check_same_thread=False)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS devices (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            serial      TEXT UNIQUE NOT NULL,
            label       TEXT DEFAULT '',
            person      TEXT DEFAULT '',
            name        TEXT DEFAULT '',
            id_source   TEXT DEFAULT '',
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
    columns = {row[1] for row in conn.execute("PRAGMA table_info(devices)")}
    for column in ("person", "name", "id_source"):
        if column not in columns:
            conn.execute(f"ALTER TABLE devices ADD COLUMN {column} TEXT DEFAULT ''")

    # Устройства, потерянные прежней ошибкой регистрации: бэкапы на них есть,
    # а строки нет. Без неё устройство не видно в списке, ему нельзя задать имя,
    # и поиск не находит его файлы — он связывает файлы с устройствами джойном.
    try:
        conn.execute("""
            INSERT INTO devices (id, serial, label, first_seen, last_seen)
            SELECT b.device_id, 'RECOVERED_' || b.device_id, '',
                   MIN(b.started_at), MAX(b.finished_at)
            FROM backups b
            LEFT JOIN devices d ON d.id = b.device_id
            WHERE d.id IS NULL
            GROUP BY b.device_id
        """)
    except sqlite3.Error as error:
        print(f"Не удалось восстановить устройства из истории выгрузок: {error}", flush=True)

    conn.commit()
    return conn


SERVICE_ID_FILES = {".astra_id", ".bestcam_id"}
# ponytail: одна блокировка регистрации; разделить по БД при работе с несколькими станциями.
_device_id_lock = threading.Lock()
_connected_device_ids = {}
_connected_devices = {}
# Режим ожидания офлайн-обновления: новые флешки забирает watcher GUI
# (ищет архив релиза), монитор их не монтирует и не бэкапит.
_offline_hold = False


def set_offline_hold(active):
    """Включить/выключить приём новых устройств монитором."""
    global _offline_hold
    _offline_hold = bool(active)


def _positive_id(value):
    if not value or not value.isascii() or not value.isdigit():
        return None
    digits = value.lstrip("0")
    if not digits or len(digits) > 19:
        return None
    number = int(digits)
    return number if 0 < number <= 9223372036854775807 else None


def _id_from_latest_log(mountpoint):
    log_dir = os.path.join(mountpoint, "LOG")
    if not os.path.isdir(log_dir):
        return None
    try:
        files = sorted((entry.path for entry in os.scandir(log_dir)
                        if entry.is_file() and entry.name.lower().endswith(".txt")),
                       key=lambda path: (os.path.getmtime(path), os.path.basename(path)), reverse=True)
        if not files:
            return None
        device_id = None
        with open(files[0], encoding="utf-8") as stream:
            for line in stream:
                if "#ID:" in line:
                    tokens = line.partition("#ID:")[2].split()
                    device_id = _positive_id(tokens[0] if tokens else "")
        return device_id
    except (OSError, UnicodeError) as error:
        raise OSError("Не удалось прочитать журнал регистратора") from error
    return None


def _id_from_latest_recording(mountpoint):
    dcim = os.path.join(mountpoint, "DCIM")
    if not os.path.isdir(dcim):
        return None
    latest = None
    walk_errors = []
    for root, _, files in os.walk(dcim, onerror=walk_errors.append):
        for name in files:
            if os.path.splitext(name)[1].lower() not in VIDEO_EXTS:
                continue
            parts = os.path.splitext(name)[0].split("_")
            if (len(parts) < 5 or not parts[0].isascii() or not parts[0].isalnum()
                    or not parts[1].isascii() or not parts[1].isdigit()
                    or not parts[2].isascii() or not parts[2].isdigit()
                    or not parts[4].isascii() or not parts[4].isdigit()):
                continue
            try:
                when = datetime.strptime(parts[3], "%Y%m%d%H%M%S")
            except ValueError:
                continue
            key = (when, int(parts[4]))
            val = _positive_id(parts[1])
            if val is not None:
                if latest is None or key > latest[0]:
                    latest = (key, val)
    if walk_errors:
        raise OSError("Не удалось прочитать записи регистратора") from walk_errors[0]
    return latest[1] if latest else None


def _read_device_id(mountpoint):
    log_id = _id_from_latest_log(mountpoint)
    recording_id = _id_from_latest_recording(mountpoint)
    if log_id and recording_id and log_id != recording_id:
        raise OSError("Разные ID в журнале и записях регистратора")
    device_id = log_id or recording_id
    if not device_id:
        raise OSError("ID регистратора не найден")
    return device_id


def _register_id_from_device(conn, device_id, serial, label, now):
    """Регистрирует ID, дополняя общий USB-серийник при конфликте."""
    for candidate in (serial or f"DEVICE_ID_{device_id}", f"{serial or 'DEVICE_ID'}#{device_id}"):
        try:
            conn.execute(
                "INSERT INTO devices (id, serial, label, first_seen, last_seen, id_source)"
                " VALUES (?, ?, ?, ?, ?, 'device')",
                (device_id, candidate, label, now, now),
            )
            return
        except sqlite3.IntegrityError:
            continue
    raise sqlite3.IntegrityError(f"Не удалось зарегистрировать ID {device_id}")


def _resolve_device_id(conn, mountpoint, serial, label, devname):
    database = os.path.realpath(conn.execute("PRAGMA database_list").fetchone()[2])
    owner = (database, devname)
    # Метка файловой системы — единственный надёжный признак «та же карта»:
    # USB-серийники у регистраторов заводские и совпадают у всех до единого.
    # Читается до замка: это подпроцесс blkid.
    fs_uuid = _get_filesystem_uuid(f"/dev/{devname}")
    with _device_id_lock:
        present = _connected_devices.get(database)
        if present is not None and devname not in present:
            raise OSError("Устройство отключено")
        # Отмечаем попытку до чтения USB, чтобы отключение отменяло даже зависшее чтение.
        reservation = (None, object(), fs_uuid)
        _connected_device_ids[owner] = reservation
    try:
        device_id = _read_device_id(mountpoint)
        with _device_id_lock:
            present = _connected_devices.get(database)
            if ((present is not None and devname not in present)
                    or _connected_device_ids.get(owner) is not reservation):
                raise OSError("Устройство отключено")
            # Дубликат — это другое СЕЙЧАС подключённое устройство с тем же ID.
            # Заявка под именем, которого на шине уже нет, — след самого себя:
            # после сброса хабом устройство возвращается под новым именем, и
            # без этой проверки оно объявляло дубликатом собственную старую
            # заявку и навсегда оставалось красным.
            # Старая заявка с той же меткой файловой системы — это сама карта,
            # вернувшаяся после сброса под новым именем: ядро какое-то время
            # держит оба имени, поэтому «владелец ещё на шине» здесь не помогает.
            # Без метки различить нечем — считаем дубликатом, как раньше.
            returned = []
            for key, claim in _connected_device_ids.items():
                if key[0] != database or key == owner or claim[0] != device_id:
                    continue
                if fs_uuid and len(claim) > 2 and claim[2] == fs_uuid:
                    returned.append(key)
                else:
                    raise OSError(f"Дубликат ID устройства {device_id}")
            for key in returned:
                _connected_device_ids.pop(key, None)
                print(f"  Карта вернулась под новым именем: {key[1]} -> {devname}", flush=True)
            reservation = (device_id, reservation[1], fs_uuid)
            _connected_device_ids[owner] = reservation
        # Запись в БД идёт уже без общего замка: sqlite сериализует писателей
        # сам и ждёт до 30 с, а замок нужен циклу опроса USB каждые две
        # секунды. От гонки за один ID защищает заявка выше.
        now = datetime.now().isoformat()
        row = conn.execute("SELECT id_source FROM devices WHERE id = ?",
                           (device_id,)).fetchone()
        if row and row[0] != "device":
            raise OSError(f"ID {device_id} занят прежней записью")
        if not row:
            _register_id_from_device(conn, device_id, serial, label or devname, now)
        conn.execute("UPDATE devices SET last_seen = ?, label = ? WHERE id = ?",
                     (now, label or devname, device_id))
        conn.commit()
        with _device_id_lock:
            present = _connected_devices.get(database)
            if ((present is not None and devname not in present)
                    or _connected_device_ids.get(owner) is not reservation):
                raise OSError("Устройство отключено")
        return device_id
    # Любая ошибка, даже неожиданная: снимаем заявку на ID и пробрасываем дальше.
    except Exception:
        with _device_id_lock:
            if _connected_device_ids.get(owner) is reservation:
                _connected_device_ids.pop(owner, None)
        raise


def _update_connected_devices(devices):
    if isinstance(devices, dict):
        with _operations_lock:
            _foreign_mounts.intersection_update(path for path in tuple(_foreign_mounts)
                                                if os.path.ismount(path))
            _foreign_mounts.update(path for path in devices.values()
                                   if path and os.path.ismount(path) and not _is_dest_path(path))
    database = os.path.realpath(DB_PATH)
    with _device_id_lock:
        _connected_devices[database] = set(devices)


def _release_device_id(devname):
    with _device_id_lock:
        _connected_device_ids.pop((os.path.realpath(DB_PATH), devname), None)


def _get_device_name(conn, device_id):
    if not conn or device_id is None:
        return ""
    row = conn.execute("SELECT name FROM devices WHERE id = ?", (device_id,)).fetchone()
    return (row[0] or "") if row else ""


def _friendly_device_label(device_id, name):
    label = f"ID {device_id}"
    return f"{label} · {name}" if name else label


def _short_device_label(device_id, name):
    """Голая подпись: имя, если задано, иначе числовой ID. Без префиксов."""
    return name or str(device_id)


def _repair_archive_ownership(root, device_dir=None):
    if platform.system() == "Windows" or not root:
        return
    root = os.path.realpath(root)
    if device_dir and (os.path.islink(device_dir) or not os.path.isdir(device_dir)):
        return
    candidates = [device_dir] if device_dir else [
        entry.path for entry in os.scandir(root)
        if entry.is_dir(follow_symlinks=False)
        and entry.name.startswith("Device")
        and entry.name[6:].isascii()
        and entry.name[6:].isdigit()
        and int(entry.name[6:]) > 0
    ]
    for path in candidates:
        path = os.path.realpath(path)
        name = os.path.basename(path)
        suffix = name[6:] if name.startswith("Device") else ""
        if (os.path.dirname(path) != root or not suffix.isascii()
                or not suffix.isdigit() or int(suffix) <= 0):
            continue
        subprocess.run(
            ["chown", "-R", f"--reference={root}", "--", path],
            check=False, capture_output=True,
        )


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
    except (OSError, subprocess.SubprocessError):
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
    except (OSError, subprocess.SubprocessError, ValueError):
        pass
    try:
        target = os.path.realpath(f"/dev/{devname}")
        for entry in os.listdir("/dev/disk/by-id/"):
            if os.path.realpath(f"/dev/disk/by-id/{entry}") == target and "usb-" in entry:
                return entry
    except OSError:
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
    except (AttributeError, OSError):
        return f"WIN_{drive_letter}"


def _scan_drive(drive_path):
    total_files = 0
    total_bytes = 0
    errors = []
    for root, dirs, files in os.walk(drive_path, onerror=errors.append):
        for file in files:
            if file in SERVICE_ID_FILES:
                continue  # internal marker, never copied — keep totals honest
            total_files += 1
            try:
                total_bytes += os.path.getsize(os.path.join(root, file))
            except OSError as error:
                errors.append(error)
    if errors:
        print(f"  {drive_path}: при подсчёте не прочитано {len(errors)}, первое: {errors[0]}", flush=True)
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
    except (OSError, subprocess.SubprocessError, ValueError):
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
    except (AttributeError, OSError):
        return ""


def _get_linux_partitions():
    parts = _get_lsblk_partitions()
    if parts:
        return parts
    fallback = _get_sys_block_partitions()
    if parts is None and fallback is None:
        raise OSError("Не удалось опросить USB-устройства")
    return fallback or {}


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
        parts = _parse_lsblk_tree(json.loads(result.stdout))
    except (OSError, subprocess.SubprocessError, ValueError) as error:
        _log_once("lsblk", f"lsblk не ответил: {error}")
        return None
    _log_once("lsblk", None)
    return parts


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
                except OSError:
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
    except (OSError, ValueError) as error:
        _log_once("sys_block", f"Не удалось прочитать /sys/block: {error}")
        return None
    _log_once("sys_block", None)
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
    except (OSError, ValueError):
        realdev = dev
    try:
        for src, mountpoint in _iter_mounts():
            if src == dev or os.path.realpath(src) == realdev:
                return mountpoint
    except (OSError, ValueError):
        pass
    return None


def _wait_for_system_mount(devname, timeout):
    """Дождаться системного монтирования или вернуть None по таймауту."""
    deadline = time.time() + max(0.0, timeout)
    while True:
        if _stop_requested():
            return None
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
        except (OSError, subprocess.SubprocessError):
            pass
        print(f"Ошибка монтирования /dev/{devname}: {detail}", flush=True)
        return None


def _unmount(mountpoint):
    try:
        subprocess.run(["umount", mountpoint], check=True, capture_output=True)
    except (OSError, subprocess.SubprocessError) as error:
        with _operations_lock:
            _failed_unmounts.add(mountpoint)
        print(f"Не удалось размонтировать {mountpoint}: {error}", flush=True)
        return False
    with _operations_lock:
        _failed_unmounts.discard(mountpoint)
        _foreign_mounts.discard(mountpoint)
    try:
        os.rmdir(mountpoint)
    except OSError:
        pass
    return True


def _copy_files(src_root, dest_root, timestamp, progress_label, total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=None):
    with _archive_directory(dest_root, create=True) as directory:
        return _copy_files_open(src_root, directory, timestamp, progress_label,
                                total_files, total_bytes, progress_obj, task_id,
                                start_time, emit_fn)


def _copy_files_open(src_root, dest_root, timestamp, progress_label, total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=None):
    copied_files = 0
    copied_bytes = 0
    failed = 0
    # Source paths that are now safely present at the destination — either just
    # copied or already identical. Only these may be auto-deleted from source.
    backed_up = set()
    # Отказов чтения подряд: сброшенный шиной носитель валит их пачкой.
    lost_in_row = 0
    last_emit_t = 0.0
    walk_errors = []
    for root, dirs, files in os.walk(src_root, onerror=walk_errors.append):
        rel_path = os.path.relpath(root, src_root)
        if rel_path == ".":
            rel_path = ""
        dest_dir = os.path.join(dest_root, rel_path) if rel_path else dest_root
        try:
            with _archive_directory(dest_dir, create=True) as dest_dir:
                for file_name in files:
                    if _stop_requested():
                        return copied_files, copied_bytes, backed_up, failed
                    if file_name in SERVICE_ID_FILES:
                        continue
                    src_file = os.path.join(root, file_name)
                    dst_file = os.path.join(dest_dir, file_name)
                    try:
                        if os.path.lexists(dst_file):
                            src_stat = os.stat(src_file)
                            dst_stat = os.stat(dst_file, follow_symlinks=False)
                            if not stat.S_ISREG(dst_stat.st_mode):
                                raise OSError("Архивный файл не должен быть ссылкой")
                            _require_archive_device(dst_stat.st_dev)
                            if src_stat.st_size == dst_stat.st_size and abs(src_stat.st_mtime - dst_stat.st_mtime) < 1:
                                backed_up.add(src_file)  # identical copy already exists
                                continue
                            base, ext = os.path.splitext(file_name)
                            dst_file = os.path.join(dest_dir, f"{base}_{timestamp}{ext}")
                        file_size = os.path.getsize(src_file)
                        _copy_archive_file(src_file, dst_file)
                        copied_files += 1
                        copied_bytes += file_size
                        backed_up.add(src_file)
                        lost_in_row = 0
                        if USE_RICH and progress_obj:
                            progress_obj.update(task_id, advance=file_size)
                        elif not IS_TTY:
                            _log_progress(progress_label, copied_files, total_files, copied_bytes, total_bytes, file_name, start_time)
                        if emit_fn is not None:
                            now = time.time()
                            if now - last_emit_t >= 1.0:
                                emit_fn("copying", copied_bytes, total_bytes, "")
                                last_emit_t = now
                    except (OSError, KeyError, ValueError) as e:
                        # Copy failed for this file — deliberately NOT added to
                        # backed_up, so it will be preserved on the source.
                        failed += 1
                        print(f"  Не скопирован {src_file}: {e}", flush=True)
                        code = e.errno     if isinstance(e, OSError) else None
                        if code in _LOST_DEVICE_ERRNOS:
                            lost_in_row += 1
                        elif code in _GONE_ERRNOS and _source_gone(src_root):
                            lost_in_row += 1
                        else:
                            lost_in_row = 0
                        if lost_in_row >= IO_ERRORS_TO_GIVE_UP:
                            raise DeviceLost(
                                f"{src_root}: {lost_in_row} отказов чтения подряд") from e
        except DeviceLost:
            raise
        except OSError as error:
            failed += sum(1 for name in files if name not in SERVICE_ID_FILES)
            print(f"  Не удалось записать в {dest_dir}: {error}", flush=True)
    if walk_errors:
        failed += len(walk_errors)
        print(f"  Обход источника прерван: {walk_errors[0]}", flush=True)
    return copied_files, copied_bytes, backed_up, failed


def copy_task(drive_path, mountpoint, devname, progress_obj, task_id, should_unmount=False, progress_queue=None):
    with _worker_guard(devname):
        if _stop_requested():
            if should_unmount:
                _unmount(mountpoint)
            return None, 0, 0
        return _copy_task(drive_path, mountpoint, devname, progress_obj, task_id, should_unmount, progress_queue)


def _copy_task(drive_path, mountpoint, devname, progress_obj, task_id, should_unmount=False, progress_queue=None):
    is_linux = platform.system() != "Windows"
    label = _get_drive_label_linux(mountpoint) if is_linux else get_drive_label_windows(drive_path.replace(":\\", ""))

    if is_linux:
        serial = _get_device_serial_linux(devname)
    else:
        serial = _get_device_serial_windows(drive_path.replace(":\\", ""))

    # Each worker thread owns its connection; sharing one across the pool is
    # not safe for concurrent writes.
    conn = _connect()
    friendly = None
    # Метка карты, запомненная до копирования. В finally по ней снимается
    # отметка «ждём возврата»: к концу работы имя устройства могло уже
    # исчезнуть или смениться, и спрашивать метку по имени поздно.
    fs_uuid = None
    try:
        if progress_queue is not None:
            progress_queue.put_nowait((f"identity:{devname}", "", "identifying", 0, 0,
                                       "Определение ID", devname))
        try:
            device_id = _resolve_device_id(conn, mountpoint, serial, label or "", devname)
        except (OSError, sqlite3.Error) as error:
            msg = f"Ошибка регистрации {devname}: {error}"
            print(msg, flush=True)
            if progress_queue is not None:
                progress_queue.put_nowait((f"identity:{devname}", "", "error", 0, 0,
                                           f"Ошибка определения ID: {error}", devname))
            if should_unmount:
                _unmount(mountpoint)
            return None, 0, 0
        # Имя папки задаётся ID устройства; главное окно показывает имя, если задано.
        # Имя перечитывается при каждом событии: переименование посреди
        # копирования не должно затираться следующим сообщением прогресса.
        display_id = f"Device{device_id}"
        _device_name = _get_device_name(conn, device_id)
        friendly = _friendly_device_label(device_id, _device_name)
        started_at = datetime.now()

        ts = started_at.strftime("%Y%m%d_%H%M%S")
        dest_base = get_dest_base()

        def _label():
            try:
                return _short_device_label(device_id, _get_device_name(conn, device_id))
            except sqlite3.Error:
                return str(device_id)

        def _emit(state, current=0, total=0, msg=""):
            if state == "done":
                _worker_local.completed = True
            if progress_queue is not None:
                progress_queue.put_nowait((device_id, _label(), state, current, total, msg, devname))

        def _still_same_device():
            try:
                if _read_device_id(mountpoint) != device_id:
                    raise OSError("Носитель сменился после определения ID")
            except OSError as error:
                _emit("error", 0, 0, str(error))
                return False
            return True

        if not _still_same_device():
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        if not dest_available():
            # The configured destination is not reachable (its disk is not
            # mounted). Creating the path anyway would silently back up into a
            # shadow directory on the root filesystem, so refuse loudly and,
            # critically, never reach the source auto-delete below.
            msg = f"Диск архива недоступен или является системным: {dest_base}"
            _emit("error", 0, 0, msg)
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[red]{msg}", total=1, completed=1)
            else:
                print(f"{msg} — {friendly} не скопирован", flush=True)
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        dest = os.path.join(dest_base, display_id)

        _emit("scanning", 0, 0, f"Сканирование ID {_label()}")

        if USE_RICH and progress_obj:
            progress_obj.update(task_id, description=f"[cyan]{friendly}: сканирование")
        else:
            print(f"{friendly}: сканирование ({label or 'без метки'})", flush=True)

        total_files, total_bytes = _scan_drive(mountpoint)

        if not _still_same_device():
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        def _fail(error):
            """Ошибка вне пофайловой обработки: показать её оператору.

            Исключение отсюда всплывает в future и молча теряется в цикле
            монитора, поэтому устройство навсегда зависало на прошлом статусе.
            """
            msg = f"Ошибка копирования: {error}"
            print(f"{friendly}: {msg}", flush=True)
            _emit("error", 0, 0, msg)
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0

        if _stop_requested():
            if should_unmount:
                _unmount(mountpoint)
            _emit("stopped", 0, total_bytes, "Копирование остановлено")
            return device_id, 0, 0

        if total_files == 0:
            if should_unmount and not _unmount(mountpoint):
                _emit("error", 0, 0, "Не удалось размонтировать носитель")
                return device_id, 0, 0
            msg = f"{friendly}: файлов нет"
            _emit("done", 0, 0, f"Готово: ID {_label()}")
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[yellow]{msg}", total=1, completed=1)
            else:
                print(f"{msg}", flush=True)
            return device_id, 0, 0

        _emit("copying", 0, total_bytes, f"Копирование ID {_label()}")

        if USE_RICH and progress_obj:
            progress_obj.update(task_id, description=f"[green]{friendly} ({_format_size(total_bytes)})", total=total_bytes, completed=0)
        else:
            print(f"{friendly}: файлов {total_files}, {_format_size(total_bytes)}", flush=True)

        start_time = time.time()
        # Карту может сбросить шиной посреди копирования: хаб передёргивает
        # соседние порты, когда из него вынимают другое устройство. Бросать
        # работу нельзя — оператор не виноват, что хаб так себя ведёт. Ждём
        # ту же карту (узнаём по метке файловой системы, имя устройства при
        # этом меняется) и продолжаем с того же места: уже скопированные
        # файлы пропускаются по размеру и времени.
        fs_uuid = _get_filesystem_uuid(f"/dev/{devname}") if is_linux else None
        attempt = 0
        try:
            while True:
                try:
                    with _archive_directory(dest_base) as directory:
                        if not _dest_identity_matches(directory, _load_config()):
                            raise OSError("Подключён другой диск архива")
                        copied_files, copied_bytes, backed_up, failed = _copy_files(
                            mountpoint, os.path.join(directory, display_id), ts, friendly,
                            total_files, total_bytes, progress_obj, task_id, start_time, emit_fn=_emit)
                    break
                except DeviceLost as lost:
                    attempt += 1
                    if not fs_uuid or attempt > CARD_RETURN_RETRIES:
                        raise
                    print(f"{friendly}: карту сбросило "
                          f"шиной ({lost}), ждём возвращения", flush=True)
                    _emit("detached", 0, total_bytes,
                          "Устройство переподключается, копирование продолжится")
                    _await_card_register(fs_uuid, CARD_RETURN_WAIT)
                    returned = _wait_for_card(fs_uuid, CARD_RETURN_WAIT)
                    if _stop_requested():
                        _await_card_clear(fs_uuid)
                        if should_unmount:
                            _unmount(mountpoint)
                        _emit("stopped", 0, total_bytes, "Копирование остановлено")
                        return device_id, 0, 0
                    if not returned:
                        _await_card_clear(fs_uuid)
                        raise
                    if should_unmount and not _unmount(mountpoint):
                        return _fail(OSError("Не удалось размонтировать носитель"))
                    mountpoint, should_unmount = _mountpoint_for(returned)
                    if mountpoint is None:
                        _await_card_clear(fs_uuid)
                        raise
                    devname = returned
                    print(f"{friendly}: карта вернулась "
                          f"как {returned}, копирование продолжается", flush=True)
                    _emit("copying", 0, total_bytes, f"Копирование ID {_label()}")
        except DeviceLost as error:
            # Носитель сброшен шиной или выдернут. Дальше читать нечего, а с
            # источника ничего не удаляем: доехавшее останется и на карте.
            msg = (f"Устройство отключилось или сброшено шиной: {friendly} — "
                   f"копирование прервано, карта не изменена")
            print(f"{msg} ({error})", flush=True)
            # Не «ошибка», а «переподключение»: при сбросе порта карта
            # возвращается через несколько секунд и продолжает работу. Красная
            # вспышка на весь экран кричит оператору «сломалось», хотя ничего
            # не сломалось. Не вернётся — плитка погаснет по своему сроку.
            _emit("detached", 0, 0, "Устройство переподключается...")
            if USE_RICH and progress_obj:
                progress_obj.update(task_id, description=f"[red]{msg}", total=1, completed=1)
            if should_unmount:
                _unmount(mountpoint)
            return device_id, 0, 0
        except (OSError, KeyError, ValueError) as error:
            return _fail(error)
        if _archive_path_allowed(dest):
            _repair_archive_ownership(dest_base, dest)

        stopped = _stop_requested()
        # Карту могли подменить не до копирования, а прямо во время него:
        # пути из backed_up тогда относятся к ушедшей карте, а удаление
        # пошло бы по совпадающим путям уже на новой.
        same_device = stopped or _still_same_device()
        # При остановке сохраняем оригиналы до следующего полного копирования.
        if not stopped and same_device:
            _delete_source_videos(mountpoint, backed_up)

        if should_unmount and not _unmount(mountpoint):
            _emit("error", copied_bytes, total_bytes, "Не удалось размонтировать носитель")
            return device_id, copied_files, copied_bytes

        finished_at = datetime.now()
        if failed:
            msg = f"Ошибки: {friendly} — {failed} файл(ов) не скопировано ({copied_files} успешно)"
            _emit("error", copied_bytes, total_bytes, f"Не скопировано: {failed} файл(ов)")
        elif stopped:
            msg = f"{friendly}: копирование остановлено, файлов {copied_files}"
            _emit("stopped", copied_bytes, total_bytes, "Копирование остановлено")
        elif not same_device:
            msg = f"Носитель сменился: {friendly} — исходные файлы сохранены"
            _emit("error", copied_bytes, total_bytes, "Носитель сменился, файлы сохранены")
        else:
            msg = f"{friendly}: готово, файлов {copied_files}, {_format_size(copied_bytes)}"
            _emit("done", copied_bytes, total_bytes, f"Готово: ID {_label()}")

        if USE_RICH and progress_obj:
            color = "red" if failed or not same_device else "yellow" if stopped else "green"
            progress_obj.update(task_id, description=f"[{color}]{msg}")
        else:
            print(f"{msg} -> {dest}", flush=True)

        try:
            conn.execute(
                "INSERT INTO backups (device_id, dest_path, total_files, total_bytes, started_at, finished_at) VALUES (?, ?, ?, ?, ?, ?)",
                (device_id, dest, copied_files, copied_bytes, started_at.isoformat(), finished_at.isoformat()),
            )
            conn.commit()
        except sqlite3.Error as error:
            # Файлы уже скопированы и удалены с источника, а без строки в
            # backups поиск по вкладке их не найдёт — молчать тут нельзя.
            print(f"  Сеанс не записан в базу: {error}", flush=True)
            _emit("error", copied_bytes, total_bytes,
                  "Копия сделана, но не записана в базу")

        return device_id, copied_files, copied_bytes
    finally:
        if friendly is not None:
            _log_progress_cache.pop(friendly, None)
        if is_linux:
            _await_card_clear(fs_uuid or _get_filesystem_uuid(f"/dev/{devname}"))
        conn.close()


def copy_task_windows(drive_letter, progress_obj, task_id, progress_queue=None):
    drive_path = f"{drive_letter}:\\"
    return copy_task(drive_path, drive_path, drive_letter, progress_obj, task_id, progress_queue=progress_queue)


def copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
    with _worker_guard(devname):
        if _stop_requested():
            return None, 0, 0
        return _copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue)


def _copy_task_linux(devname, mountpoint, progress_obj, task_id, progress_queue=None):
    # Ту же карту уже доигрывает воркер, потерявший её при сбросе шины:
    # второй на неё не поднимаем, иначе два потока полезут в одну папку.
    if _card_is_awaited(_get_filesystem_uuid(f"/dev/{devname}")):
        print(f"  Карта {devname} возвращается к прежней выгрузке", flush=True)
        return 0, 0, 0
    should_unmount = False
    if not (mountpoint and os.path.ismount(mountpoint)):
        # The lsblk mountpoint captured at detection can be stale: the desktop
        # auto-mounter may have (or may be about to) mount the device since
        # then. Prefer a mount the system already owns, giving it a short grace
        # period to appear. Creating our own *second* read-write mount while the
        # desktop also holds one lets two uncoordinated FAT caches corrupt the
        # stick (0 B, refuses to remount). Only when no system mount shows up
        # (headless mode) do we mount it ourselves and own the unmount.
        existing = _wait_for_system_mount(devname, MOUNT_GRACE_SECONDS)
        if existing:
            mountpoint = existing
        else:
            if _stop_requested():
                return None, 0, 0
            mp = _mount_device(devname)
            if mp is None:
                if USE_RICH and progress_obj:
                    progress_obj.update(task_id, description=f"[red]{devname}: не удалось смонтировать", total=1, completed=1)
                else:
                    print(f"{devname}: не удалось смонтировать, повторим при следующем опросе", flush=True)
                # Регистратор отдаёт USB-диск раньше карты («не найден носитель»).
                # OSError ловит _run: показывает ошибку и ставит устройство на повтор.
                raise OSError(f"Не удалось смонтировать {devname}")
            mountpoint = mp
            should_unmount = _is_own_mount(mp)
    should_unmount = should_unmount or _is_own_mount(mountpoint)
    if _is_dest_path(mountpoint):
        # This drive hosts the backup destination — it is not a source to back
        # up, and it must STAY mounted: unmounting it here (and rmdir'ing the
        # mountpoint) is what used to make later backups silently recreate the
        # path as a plain directory on the root filesystem, so the interface
        # reported success while the real disk stayed empty.
        cfg_dest = _config_backup_dest()
        resolved_dest = get_dest_base()
        if (cfg_dest and _dest_identity_matches(resolved_dest, _load_config())
                and ensure_dest_marker(resolved_dest)):
            remember_configured_dest(resolved_dest, update_path=False)
        update_log_location()
        print(f"  Подключён диск архива, остаётся смонтированным: {mountpoint}", flush=True)
        if progress_queue is not None:
            progress_queue.put_nowait(("_status_", "", "info", 0, 0,
                                       f"Диск назначения подключён: {os.path.basename(mountpoint)}", ""))
        return 0, 0, 0
    if not should_unmount:
        with _operations_lock:
            _foreign_mounts.add(mountpoint)
    return copy_task(devname, mountpoint, devname, progress_obj, task_id, should_unmount, progress_queue)


def _make_submit_fn(progress_queue=None):
    def _run(dev, mountpoint, progress_obj, task_id):
        if _safe_removal:
            with _operations_lock:
                _interrupted_devices.add(dev)
            return None, 0, 0
        try:
            if platform.system() == "Windows":
                return copy_task_windows(dev, progress_obj, task_id, progress_queue)
            return copy_task_linux(dev, mountpoint, progress_obj, task_id, progress_queue)
        except OSError as error:
            with _operations_lock:
                _interrupted_devices.add(dev)
            if progress_queue is not None:
                progress_queue.put_nowait((f"identity:{dev}", "", "error", 0, 0, str(error), dev))
            return None, 0, 0
        # Последний рубеж воркера: сюда должна попасть и ошибка, которую никто не ждал.
        except Exception as error:
            # Не повторяем: ошибка в коде повторилась бы на каждом опросе.
            print(f"  {dev}: сбой воркера\n{traceback.format_exc()}", flush=True)
            if progress_queue is not None:
                progress_queue.put_nowait((f"identity:{dev}", "", "error", 0, 0,
                                           f"Сбой воркера: {error}", dev))
            return None, 0, 0

    def _finished(future):
        with _operations_lock:
            _submitted_jobs.discard(future)

    def _submit(executor, dev, mountpoint, progress_obj, task_id):
        with _operations_lock:
            future = executor.submit(_run, dev, mountpoint, progress_obj, task_id)
            _submitted_jobs.add(future)
            future.add_done_callback(_finished)
            return future
    return _submit


def monitor_usb(interval=2, stop_event=None, progress_queue=None):
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except AttributeError:
        pass
    system = platform.system()
    is_linux = system != "Windows"

    _init_db().close()  # ensure schema + migrations; workers open their own conn

    cfg_dest = _config_backup_dest()
    if cfg_dest:
        resolved_dest = get_dest_base()
        if (_dest_identity_matches(resolved_dest, _load_config())
                and ensure_dest_marker(resolved_dest)):
            remember_configured_dest(resolved_dest, update_path=False)
        else:
            print(f"Внимание: папка архива пока недоступна: {resolved_dest} "
                  f"(копирование не пойдёт, пока её диск не смонтирован)", flush=True)

    archive_root = get_dest_base()
    if not _load_config().get("_config_unreadable") and _archive_path_allowed(archive_root):
        _repair_archive_ownership(archive_root)

    print(f"Станция запущена: {system}, потоков {MAX_WORKERS}, база {DB_PATH}", flush=True)
    print("Ожидание USB-устройств (Ctrl+C — остановить)", flush=True)

    executor = ThreadPoolExecutor(max_workers=MAX_WORKERS)
    active = {}  # dev → future
    submit = _make_submit_fn(progress_queue)

    if is_linux:
        os.makedirs(MOUNT_BASE, exist_ok=True)
        try:
            known = _get_linux_partitions()  # dict: devname → mountpoint
        except OSError:
            known = {}
    else:
        known = get_removable_drives()

    _update_connected_devices(known)
    if _safe_removal:
        known = {} if is_linux else set()
    # Метка файловой системы принятых карт. По ней после сбоя шины отличаем
    # вернувшуюся карту от новой, вставленной под тем же именем устройства.
    known_fs = {}
    for dev in sorted(known):
        mp = known[dev] if is_linux else None
        if is_linux:
            known_fs[dev] = _get_filesystem_uuid(f"/dev/{dev}")
        print(f"  Подключено при запуске: {dev}", flush=True)
        active[dev] = submit(executor, dev, mp, None, None)

    # dev → timestamp of first consecutive miss; cleared when device reappears
    pending_removals = {}
    # Когда из опроса пропала вся линейка разом; None — шина в порядке.
    bus_glitch_since = None

    def _forget(dev):
        """Подтвердить отключение: забыть устройство и сообщить интерфейсу."""
        with _operations_lock:
            _interrupted_devices.discard(dev)
        pending_removals.pop(dev, None)
        known_fs.pop(dev, None)
        _release_device_id(dev)
        if is_linux:
            known.pop(dev, None)
        else:
            known.discard(dev)
        dn = os.path.basename(dev)
        if progress_queue is not None:
            progress_queue.put_nowait(("_removed_", dn, "", 0, 0, "", ""))

    try:
        while True:
            if stop_event and stop_event.is_set():
                break
            time.sleep(interval)

            done = [dev for dev, f in active.items() if f.done()]
            for dev in done:
                active.pop(dev)  # ошибки воркера уже записал и показал _run

            now_t = time.time()
            try:
                current = _get_linux_partitions() if is_linux else get_removable_drives()
            except OSError:
                # Опрос не удался: это не отключение. Причину уже написал _log_once.
                continue

            known_keys = set(known) if is_linux else known
            current_keys = set(current) if is_linux else current

            # Из опроса разом пропали все, кто был. Это сбой шины, а не
            # отключение всей линейки руками: пока ждём возврата, состояние
            # не публикуем (иначе воркеры решат, что их карты вынули) и
            # отключений не подтверждаем.
            if len(known_keys) > 1 and not (current_keys & known_keys):
                if bus_glitch_since is None:
                    bus_glitch_since = now_t
                    print(f"  Из опроса пропала вся линейка ({len(known_keys)} устройств) — "
                          f"ждём возврата шины", flush=True)
                    # Интерфейс должен знать про сбой: пока шина не вернулась,
                    # гасить плитки нельзя — карты вернутся под другими именами.
                    if progress_queue is not None:
                        progress_queue.put_nowait(("_bus_", "glitch", "", 0, 0, "", ""))
                if now_t - bus_glitch_since < BUS_GLITCH_GRACE:
                    continue
            elif bus_glitch_since is not None:
                print("  Шина вернулась, устройства на месте", flush=True)
                bus_glitch_since = None
                if progress_queue is not None:
                    progress_queue.put_nowait(("_bus_", "ok", "", 0, 0, "", ""))
                # «Сбоем шины» считается и честная замена всей линейки: оператор
                # вынул все камеры и за время ожидания вставил новые, а ядро
                # выдало им те же имена. Такие карты — новые, а не вернувшиеся:
                # иначе их не выгрузили бы, а плитки показывали бы старые.
                if is_linux:
                    for dev in sorted(current_keys & set(known)):
                        was = known_fs.get(dev)
                        now = _get_filesystem_uuid(f"/dev/{dev}")
                        if was and now and was != now:
                            print(f"  Под именем {dev} уже другая карта — "
                                  f"прежняя отключена", flush=True)
                            _forget(dev)
                    known_keys = set(known)

            _update_connected_devices(current)

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
                _forget(dev)

            # New devices: present in current but not yet in known
            with _operations_lock:
                _interrupted_devices.intersection_update(current_keys)
                retry_devices = _interrupted_devices & current_keys - set(active)
            new_devices = sorted(((current_keys - known_keys) | retry_devices) - set(active))

            if _safe_removal and new_devices:
                # Пока идёт извлечение, новые карты не трогаем: оператор как
                # раз ими и занят.
                new_devices = []

            for dev in new_devices:
                if _offline_hold:
                    print(f"  Новое USB-устройство отложено ради офлайн-обновления: {dev}", flush=True)
                    continue
                with _operations_lock:
                    _interrupted_devices.discard(dev)
                if is_linux:
                    known[dev] = current[dev]
                    known_fs[dev] = _get_filesystem_uuid(f"/dev/{dev}")
                else:
                    known.add(dev)
                pending_removals.pop(dev, None)
                mp = current[dev] if is_linux else None
                print(f"  Новое USB-устройство: {dev}", flush=True)
                active[dev] = submit(executor, dev, mp, None, None)

    except KeyboardInterrupt:
        print("\nОстановлено.")
    finally:
        _update_connected_devices(set())
        for dev in known:
            _release_device_id(dev)
        was_paused = _safe_removal
        set_safe_removal(True)
        executor.shutdown(wait=True)
        set_safe_removal(was_paused)


if __name__ == "__main__":
    monitor_usb()
