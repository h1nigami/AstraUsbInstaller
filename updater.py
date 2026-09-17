#!/usr/bin/env python3
"""Автообновление киоска с релизов GitHub.

Запускается systemd-таймером, а не из GUI: install_native.sh в конце
перезапускает сервис приложения, и обновление, идущее внутри этого сервиса,
убило бы само себя посреди подмены файлов.
"""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import usb_monitor

REPO = "h1nigami/AstraUsbInstaller"
LATEST_URL = f"https://api.github.com/repos/{REPO}/releases/latest"
APP_DIR = os.path.dirname(os.path.abspath(__file__))
PREV_DIR = APP_DIR + ".prev"
# Вне APP_DIR: откат восстанавливает APP_DIR из PREV_DIR, а тег провалившегося
# релиза должен пережить этот откат, иначе тот же релиз ставится заново на
# каждом тике таймера.
FAILED_TAG_FILE = APP_DIR + ".failed"
SERVICE = "astra-usb-monitor"


#: Имя архива Python-версии: его пишет release.yml.
ARCHIVE_PREFIX = "astra-usb-monitor-"


def pick_asset(release):
    """Извлечь (tarball_url, sha256_url) из ответа релиза, или None.

    Оба файла обязательны: без контрольной суммы устанавливать ничего не будем.
    """
    tag = release.get("tag_name")
    if (not isinstance(tag, str) or not re.fullmatch(r"v1\.[0-9]+(?:\.[0-9]+)*", tag)
            or release.get("prerelease") or release.get("draft")):
        return None
    urls = {
        a.get("name", ""): a.get("browser_download_url")
        for a in release.get("assets", [])
    }
    name = f"{ARCHIVE_PREFIX}{tag}.tar.gz"
    archive, checksum = urls.get(name), urls.get(name + ".sha256")
    return (archive, checksum) if archive and checksum else None


def needs_update(current, latest):
    """Сравнить теги на неравенство, не на порядок: цель — перейти
    на то, что GitHub отметил как latest, поэтому отозванный релиз
    исправляется публикацией предыдущего вместо трудных переговоров с хостом."""
    if not latest:
        return False
    return current != latest


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_sha256(text):
    """Файл суммы приходит либо голым хешем, либо строкой '<hex>  <имя файла>'."""
    return text.split()[0] if text.split() else ""


def _fetch(url, timeout=15):
    req = urllib.request.Request(url, headers={"User-Agent": "astra-usb-monitor"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def _log(msg):
    print(f"[updater] {msg}", flush=True)


def _service_healthy():
    """Сервис активен и не циклится в перезапусках."""
    try:
        active = subprocess.run(["systemctl", "is-active", "--quiet", SERVICE],
                                 timeout=10).returncode == 0
        shown = subprocess.run(["systemctl", "show", "-p", "NRestarts", "--value", SERVICE],
                                capture_output=True, text=True, timeout=10)
        if shown.returncode != 0:
            return False
        restarts = int(shown.stdout.strip())
    except Exception:
        return False
    return active and restarts == 0


def _backup_app_dir(app_dir, prev_dir):
    """Снять копию кода приложения в prev_dir.

    data/ (база устройств) и USB_Backups/ (сами резервные копии с флешек)
    в копию не попадают — там гигабайты, копирование зависло бы и забило
    диск. Предыдущая копия перед этим удаляется.
    """
    shutil.rmtree(prev_dir, ignore_errors=True)
    shutil.copytree(app_dir, prev_dir, symlinks=True,
                     ignore=shutil.ignore_patterns("data", "USB_Backups",
                                                    "__pycache__", "*.pyc"))


def _restore_app_dir(prev_dir, app_dir):
    """Вернуть код из prev_dir поверх app_dir.

    app_dir целиком не удаляется и не пересоздаётся — в prev_dir нет ни
    data/, ни USB_Backups/, поэтому снос app_dir уничтожил бы базу устройств
    и сами резервные копии.
    """
    shutil.copytree(prev_dir, app_dir, symlinks=True, dirs_exist_ok=True)


def _read_failed_tag(path=None):
    """Тег релиза, который уже пробовали ставить и откатили, или None."""
    try:
        with open(path or FAILED_TAG_FILE) as f:
            return f.read().strip() or None
    except Exception:
        return None


def _write_failed_tag(tag, path=None):
    try:
        with open(path or FAILED_TAG_FILE, "w") as f:
            f.write(tag)
    except Exception:
        pass


def _clear_failed_tag(path=None):
    try:
        os.remove(path or FAILED_TAG_FILE)
    except Exception:
        pass


#: Маска имени архива офлайн-обновления в корне флешки.
OFFLINE_ARCHIVE_RE = re.compile(r"astra-usb-monitor-(v1\.[0-9]+(?:\.[0-9]+)*)\.tar\.gz")

#: Спул: сюда GUI складывает архив с флешки, сервис забирает его без сети.
OFFLINE_SPOOL_DIR = APP_DIR + ".offline"


def _version_key(tag):
    return tuple(int(part) for part in tag[1:].split("."))


def find_offline_archives(directory):
    """Найти архивы обновления в корне directory: [(tag, tarball)], сначала новее.

    Имена строго по маске Python-релизов; чужое игнорируется. Пустой список —
    это и есть «обновление не найдено».
    """
    found = []
    try:
        names = os.listdir(directory)
    except Exception:
        return []
    for name in names:
        match = OFFLINE_ARCHIVE_RE.fullmatch(name)
        if not match:
            continue
        path = os.path.join(directory, name)
        if os.path.isfile(path):
            found.append((match.group(1), path))
    found.sort(key=lambda item: _version_key(item[0]), reverse=True)
    return found


def _verify_checksum_file(tarball_path, sha_path):
    """None, если сумма сошлась, иначе текст ошибки."""
    try:
        with open(sha_path) as stream:
            expected = parse_sha256(stream.read())
    except Exception:
        return "рядом нет файла контрольной суммы (.sha256)"
    if not expected:
        return "файл контрольной суммы пуст"
    try:
        actual = sha256_of(tarball_path)
    except Exception as error:
        return f"архив не читается: {error}"
    if actual != expected:
        return "контрольная сумма не сошлась — архив битый"
    return None


def check_offline_package(tarball_path):
    """(tag, None), если пакет с флешки годен, иначе (None, текст ошибки)."""
    name = os.path.basename(tarball_path)
    match = OFFLINE_ARCHIVE_RE.fullmatch(name)
    if not match:
        return None, "это не архив обновления"
    error = _verify_checksum_file(tarball_path, tarball_path + ".sha256")
    if error:
        return None, error
    return match.group(1), None


def stage_offline_package(tarball_path, spool_dir=None):
    """Скопировать архив и сумму с флешки в спул для сервиса.

    Возвращает (tag, None) или (None, текст ошибки). Саму установку
    по-прежнему выполняет внешний сервис, а не вызывающий процесс.
    """
    tag, error = check_offline_package(tarball_path)
    if error:
        return None, error
    dest = spool_dir or OFFLINE_SPOOL_DIR
    try:
        os.makedirs(dest, exist_ok=True)
        shutil.copyfile(tarball_path, os.path.join(dest, "release.tar.gz"))
        shutil.copyfile(tarball_path + ".sha256",
                        os.path.join(dest, "release.tar.gz.sha256"))
        with open(os.path.join(dest, "tag"), "w") as stream:
            stream.write(tag)
    except Exception as error:
        shutil.rmtree(dest, ignore_errors=True)
        return None, f"не удалось подготовить обновление: {error}"
    return tag, None


def _take_offline_spool(spool_dir=None):
    """(tag, tarball) из спула или None. Спул чистит вызывающий."""
    directory = spool_dir or OFFLINE_SPOOL_DIR
    try:
        with open(os.path.join(directory, "tag")) as stream:
            tag = stream.read().strip()
    except Exception:
        return None
    if not re.fullmatch(r"v1\.[0-9]+(?:\.[0-9]+)*", tag or ""):
        return None
    tarball = os.path.join(directory, "release.tar.gz")
    if not os.path.isfile(tarball):
        return None
    return tag, tarball


def _clear_offline_spool(spool_dir=None):
    shutil.rmtree(spool_dir or OFFLINE_SPOOL_DIR, ignore_errors=True)


def _extract_src_dir(archive, tmp):
    unpacked = os.path.join(tmp, "src")
    with tarfile.open(archive) as tar:
        tar.extractall(unpacked)
    roots = [os.path.join(unpacked, name) for name in os.listdir(unpacked)]
    if len(roots) == 1 and os.path.isdir(roots[0]):
        return roots[0]
    return unpacked


def _rollback():
    """
    ponytail: откатывается только APP_DIR — юниты systemd
    (astra-usb-monitor.service, astra-usb-update.{service,timer}) и
    udev-правило не восстанавливаются. Обе версии сейчас используют один
    и тот же ExecStart, поэтому это не страшно; но релиз, меняющий unit-файл
    или точку входа, так откатить не получится — понадобится ручной визит.
    """
    _log("новая версия не поднялась — откат на предыдущую")
    _restore_app_dir(PREV_DIR, APP_DIR)
    subprocess.run(["systemctl", "restart", SERVICE], timeout=120)


def _apply(src_dir, tag):
    """Снять копию текущей установки, запустить установщик, проверить,
    при неудаче откатиться.

    Установщик может зависнуть (TimeoutExpired) или не найтись (нет bash/
    systemctl) — оба случая ловятся общим except, а не только неверный код
    возврата, иначе точка останется на середине подмены файлов и без отката.
    """
    _backup_app_dir(APP_DIR, PREV_DIR)

    try:
        subprocess.run(["systemctl", "reset-failed", SERVICE], timeout=30)
        installer = os.path.join(src_dir, "install_native.sh")
        result = subprocess.run(["bash", installer], cwd=src_dir, timeout=1800)
        if result.returncode != 0:
            raise RuntimeError(f"установщик вернул {result.returncode}")

        # Проверяем импорт до ожидания здоровья службы.
        probe = subprocess.run([sys.executable, "-c", "import gui, usb_monitor, main"],
                               cwd=APP_DIR, timeout=60)
        if probe.returncode != 0:
            raise RuntimeError("новая версия не импортируется")

        time.sleep(60)
        if not _service_healthy():
            raise RuntimeError("сервис не поднялся после обновления")
        installed = usb_monitor.read_version()
        installed_tag = installed[0] if installed else None
        if installed_tag != tag:
            raise RuntimeError(f"после установки VERSION даёт {installed_tag}, а не {tag}")
    except Exception as e:
        _log(f"установка не удалась ({e})")
        _write_failed_tag(tag)
        _rollback()
        return 1

    shutil.rmtree(PREV_DIR, ignore_errors=True)

    _clear_failed_tag()

    _log("обновление установлено")
    return 0


def main(spool_dir=None):
    current = usb_monitor.read_version()
    current_tag = current[0] if current else None

    offline = _take_offline_spool(spool_dir)
    if offline is not None:
        tag, tarball = offline
        error = _verify_checksum_file(
            tarball, os.path.join(spool_dir or OFFLINE_SPOOL_DIR,
                                  "release.tar.gz.sha256"))
        if error:
            _log(f"офлайн-пакет битый ({error}) — пропуск")
            _clear_offline_spool(spool_dir)
            return 0
        if not needs_update(current_tag, tag):
            _log(f"офлайн-пакет {tag} уже установлен")
            _clear_offline_spool(spool_dir)
            return 0
        if tag == _read_failed_tag():
            _log(f"офлайн-пакет {tag} уже откатывали — пропуск")
            _clear_offline_spool(spool_dir)
            return 0
        if usb_monitor.is_copying():
            _log("идёт копирование — офлайн-обновление отложено")
            return 0
        with tempfile.TemporaryDirectory() as tmp:
            src_dir = _extract_src_dir(tarball, tmp)
            if usb_monitor.is_copying():
                _log("копирование началось — офлайн-обновление отложено")
                return 0
            _log(f"ставим офлайн {tag} (было {current_tag})")
            rc = _apply(src_dir, tag)
            _clear_offline_spool(spool_dir)
            return rc

    try:
        release = json.loads(_fetch(LATEST_URL))
    except Exception as e:
        _log(f"релиз не проверен ({e}) — пробуем в следующий раз")
        return 0

    latest_tag = release.get("tag_name")
    if not needs_update(current_tag, latest_tag):
        _log(f"актуальная версия: {current_tag}")
        return 0

    if latest_tag == _read_failed_tag():
        _log(f"релиз {latest_tag} уже откатывали — пропуск")
        return 0

    if usb_monitor.is_copying():
        _log("идёт копирование — обновление отложено")
        return 0

    urls = pick_asset(release)
    if not urls:
        _log(f"у релиза {latest_tag} нет архива с контрольной суммой")
        return 0
    tarball_url, checksum_url = urls

    with tempfile.TemporaryDirectory() as tmp:
        archive = os.path.join(tmp, "release.tar.gz")
        try:
            with open(archive, "wb") as f:
                f.write(_fetch(tarball_url, timeout=300))
            expected = parse_sha256(_fetch(checksum_url).decode())
        except Exception as e:
            _log(f"загрузка не удалась ({e})")
            return 0

        if sha256_of(archive) != expected:
            _log("контрольная сумма не сошлась — архив не установлен")
            return 0

        src_dir = _extract_src_dir(archive, tmp)

        # is_copying() проверялся ещё до скачивания архива — за это время
        # (до 300с на архив + время на сумму и распаковку) могла начаться
        # запись флешки, которую снесёт systemctl restart из install_native.sh.
        if usb_monitor.is_copying():
            _log("копирование началось во время загрузки — обновление отложено")
            return 0

        _log(f"ставим {latest_tag} (было {current_tag})")
        return _apply(src_dir, latest_tag)


if __name__ == "__main__":
    sys.exit(main())
