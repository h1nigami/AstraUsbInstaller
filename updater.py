#!/usr/bin/env python3
"""Автообновление киоска с релизов GitHub.

Запускается systemd-таймером, а не из GUI: install_native.sh в конце
перезапускает сервис приложения, и обновление, идущее внутри этого сервиса,
убило бы само себя посреди подмены файлов.
"""

from contextlib import ExitStack
import hashlib
import http.client
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import traceback
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
    """Обновлять, только если latest строго новее текущего тега.

    Раньше сравнивались на неравенство, и при скачках ``releases/latest``
    (переопубликация, правка prerelease-флага) станция могла откатиться на
    старую версию и затем прыгать туда-сюда.  Теперь числовое сравнение
    ``_version_key`` гарантирует переход только вперёд; отозванный релиз
    исправляется публикацией нового тега, а не повторной установкой старого.
    """
    if not latest or not current:
        # Нет текущей версии (чистая установка без VERSION) — всегда обновляем.
        return bool(latest)
    try:
        return _version_key(latest) > _version_key(current)
    except (ValueError, IndexError):
        # Непарсимый тег — не рискуем, пропускаем.
        return False


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


def has_network(timeout=5):
    """Быстрая проверка, что до api.github.com можно достучаться.

    Не заменяет саму проверку релиза (TCP-порт может быть открыт, а HTTPS —
    подменён), но отдельно отличает «сети нет вообще» от прочих ошибок и не
    требует загрузки JSON, чтобы GUI могло сразу сказать пользователю
    причину вместо общего «не удалось».
    """
    try:
        with socket.create_connection(("api.github.com", 443), timeout=timeout):
            return True
    except OSError:
        return False


def _log(msg):
    print(f"Обновление: {msg}", flush=True)


def read_update_status(runner=None):
    """Состояние отдельной службы и сообщения её текущей попытки для GUI."""
    runner = runner or subprocess.run
    status = {"active": False, "started": None, "message": "", "available": False}
    try:
        shown = runner(["systemctl", "show", "astra-usb-update.service",
                        "--property=ActiveState,Result,ExecMainStartTimestampMonotonic"],
                       capture_output=True, text=True, timeout=5)
        if shown.returncode:
            raise OSError((shown.stderr or shown.stdout).strip() or "служба обновления не найдена")
        state = dict(line.split("=", 1) for line in shown.stdout.splitlines() if "=" in line)
        started = int(state["ExecMainStartTimestampMonotonic"])
        active = state["ActiveState"] in ("activating", "active", "reloading", "deactivating")
        status.update(active=active, started=str(started), available=True)
        journal = runner(["journalctl", "-b", "-u", "astra-usb-update.service",
                          "-n", "60", "--no-pager", "-o", "json"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=5)
        if journal.returncode:
            status["message"] = "Не удалось прочитать результат обновления: " + journal.stderr.strip()
            return status
        messages, raw, stamp = [], [], ""
        for line in journal.stdout.splitlines():
            try:
                record = json.loads(line)
                if not isinstance(record, dict):
                    continue
                if int(record.get("__MONOTONIC_TIMESTAMP", 0)) < started:
                    continue
                message = record.get("MESSAGE", "")
                if not isinstance(message, str) or not message.strip():
                    continue
                match = re.match(r"(?:Обновление:|\[updater\])\s*(.*)", message)
                if match:
                    text = match.group(1)
                    if text.startswith(("запуск команды:", "команда ")):
                        continue
                    messages.append(text[:400])
                elif record.get("_PID") != "1":
                    raw.append(message[:400])
                if record.get("__REALTIME_TIMESTAMP"):
                    stamp = time.strftime("%d.%m %H:%M:%S", time.localtime(int(record["__REALTIME_TIMESTAMP"]) / 1_000_000))
            except (ValueError, TypeError, OverflowError):
                continue
        failed = state["ActiveState"] == "failed" or state.get("Result") not in (None, "success")
        title = "Обновление выполняется" if active else ("Проверка завершилась с ошибкой" if failed else "Результат последней проверки")
        details = messages[-2:] or raw[-2:]
        if failed and raw:
            details = messages[-1:] + raw[-1:]
        if not details:
            details = ["Ожидание сообщений установщика" if active else
                       ("Обновления ещё не проверялись" if not started else "Служба не передала подробности. Они доступны в журнале.")]
        status["message"] = title + (f" ({stamp})" if stamp else "") + ":\n" + "\n".join(details)
    except (OSError, subprocess.SubprocessError, ValueError, KeyError) as error:
        status.update(active=False, available=False, message=f"Не удалось получить состояние обновления: {error}")
    return status


def _run_logged(command, **kwargs):
    """Дочерняя команда пишет сразу в общий журнал, даже если затем зависнет."""
    _log(f"запуск команды: {' '.join(command)}")
    if usb_monitor._log_fh is not None:
        try:
            usb_monitor._log_fh.flush()
            usb_monitor._log_fh.fileno()
        except (OSError, ValueError) as error:
            _log(f"файл журнала недоступен ({error}), вывод команды остаётся в системном журнале")
        else:
            kwargs.update(stdout=usb_monitor._log_fh, stderr=subprocess.STDOUT)
    result = subprocess.run(command, **kwargs)
    _log(f"команда {command[0]} завершилась с кодом {result.returncode}")
    return result


def _service_healthy():
    """Служба работает либо штатно завершилась, без аварийных перезапусков."""
    try:
        properties = ("ActiveState", "SubState", "Result", "NRestarts", "ExecMainCode",
                      "ExecMainStatus", "ExecMainStartTimestampMonotonic", "ExecMainExitTimestampMonotonic")
        shown = subprocess.run(["systemctl", "show", SERVICE, "--property=" + ",".join(properties)],
                               capture_output=True, text=True, timeout=10)
        if shown.returncode != 0:
            return False
        state = dict(line.split("=", 1) for line in shown.stdout.splitlines() if "=" in line)
        if int(state["NRestarts"]) != 0:
            return False
        if state["ActiveState"] == "active" and state["SubState"] == "running":
            return True
        # CLD_EXITED=1 отличает выход с кодом 0 от завершения сигналом.
        clean_exit = (state["ActiveState"] == "inactive" and state["SubState"] == "dead"
                      and state["Result"] == "success" and state["ExecMainCode"] == "1"
                      and state["ExecMainStatus"] == "0"
                      and 0 < int(state["ExecMainStartTimestampMonotonic"])
                      <= int(state["ExecMainExitTimestampMonotonic"]))
        if clean_exit:
            _log("служба штатно завершилась с кодом 0")
        return clean_exit
    except (OSError, subprocess.SubprocessError, ValueError, KeyError) as error:
        _log(f"не удалось проверить службу: {error}")
        return False


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
    except (OSError, UnicodeError):
        return None


def _write_failed_tag(tag, path=None):
    try:
        with open(path or FAILED_TAG_FILE, "w") as f:
            f.write(tag)
    except OSError as error:
        # Без этой метки сломанный релиз будет ставиться и откатываться каждый запуск.
        _log(f"не удалось запомнить сбойный релиз {tag}: {error}")


def _clear_failed_tag(path=None):
    try:
        os.remove(path or FAILED_TAG_FILE)
    except OSError:
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
    except OSError:
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
    except (OSError, UnicodeError):
        return "рядом нет файла контрольной суммы (.sha256)"
    if not expected:
        return "файл контрольной суммы пуст"
    try:
        actual = sha256_of(tarball_path)
    except OSError as error:
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
    except OSError as error:
        shutil.rmtree(dest, ignore_errors=True)
        return None, f"не удалось подготовить обновление: {error}"
    return tag, None


def _take_offline_spool(spool_dir=None):
    """(tag, tarball) из спула или None. Спул чистит вызывающий."""
    directory = spool_dir or OFFLINE_SPOOL_DIR
    if not os.path.isdir(directory):
        return None
    try:
        with open(os.path.join(directory, "tag")) as stream:
            tag = stream.read().strip()
    except (OSError, UnicodeError) as error:
        _log(f"офлайн-пакет в {directory} без тега ({error}) — пропуск")
        return None
    if not re.fullmatch(r"v1\.[0-9]+(?:\.[0-9]+)*", tag or ""):
        _log(f"офлайн-пакет с неверным тегом {tag!r} — пропуск")
        return None
    tarball = os.path.join(directory, "release.tar.gz")
    if not os.path.isfile(tarball):
        _log(f"офлайн-пакет {tag} без архива — пропуск")
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
    _run_logged(["systemctl", "restart", SERVICE], timeout=120)


def _apply(src_dir, tag):
    return _try_apply(src_dir, tag)[0]


def _try_apply(src_dir, tag):
    """(код выхода, отложено ли). Отложенная установка ничего не меняла:
    вызывающий не должен считать её попыткой — иначе офлайн-пакет стирался
    бы, так и не встав, пока станция занята опознанием или монтированием."""
    with ExitStack() as stack:
        try:
            fd = stack.enter_context(usb_monitor.operation_guard(exclusive=True))
        except OSError as error:
            _log(f"обновление отложено: {error}")
            return 0, True
        return _apply_locked(src_dir, tag, fd), False


def _apply_locked(src_dir, tag, lock_fd):
    """Снять копию текущей установки, запустить установщик, проверить,
    при неудаче откатиться.

    Установщик может зависнуть (TimeoutExpired) или не найтись (нет bash/
    systemctl) — оба случая ловятся общим except, а не только неверный код
    возврата, иначе точка останется на середине подмены файлов и без отката.
    """
    _backup_app_dir(APP_DIR, PREV_DIR)

    try:
        _log(f"устанавливаем {tag}; программа перезапустится")
        _run_logged(["systemctl", "reset-failed", SERVICE], timeout=30)
        installer = os.path.join(src_dir, "install_native.sh")
        env = dict(os.environ, USB_DB_PATH=os.path.abspath(usb_monitor.DB_PATH))
        env.pop("ASTRA_OPERATIONS_LOCK_FD", None)
        if lock_fd is not None:
            env["ASTRA_OPERATIONS_LOCK_FD"] = str(lock_fd)
        result = _run_logged(["bash", installer], cwd=src_dir, timeout=1800,
                                env=env, pass_fds=(() if lock_fd is None else (lock_fd,)))
        if result.returncode != 0:
            raise RuntimeError(f"установщик вернул {result.returncode}")

        # Проверяем импорт до ожидания здоровья службы.
        probe = _run_logged([sys.executable, "-c", "import gui, usb_monitor, main"],
                               cwd=APP_DIR, timeout=60)
        if probe.returncode != 0:
            raise RuntimeError("новая версия не импортируется")

        _log(f"проверяем запуск {tag}; проверка займёт около минуты")
        time.sleep(60)
        if not _service_healthy():
            raise RuntimeError("сервис не поднялся после обновления")
        installed = usb_monitor.read_version()
        installed_tag = installed[0] if installed else None
        if installed_tag != tag:
            raise RuntimeError(f"после установки VERSION даёт {installed_tag}, а не {tag}")
    # Любая ошибка установки, даже неожиданная, обязана закончиться откатом.
    except Exception as e:
        _log(f"установка не удалась ({e})")
        _write_failed_tag(tag)
        _rollback()
        return 1

    shutil.rmtree(PREV_DIR, ignore_errors=True)

    _clear_failed_tag()

    _log(f"обновление {tag} установлено")
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
            rc, deferred = _try_apply(src_dir, tag)
            if not deferred:
                # Установка состоялась (успешно или с откатом и записью
                # сбойного тега) — спул больше не нужен. Отложенную же
                # повторим на следующем тике таймера.
                _clear_offline_spool(spool_dir)
            return rc

    _log("проверяем доступную версию на GitHub")
    try:
        release = json.loads(_fetch(LATEST_URL))
    except (OSError, ValueError, http.client.HTTPException) as e:
        _log(f"релиз не проверен ({e}) — пробуем в следующий раз")
        return 0

    latest_tag = release.get("tag_name")
    if not needs_update(current_tag, latest_tag):
        _log(f"обновление не требуется: установлена {current_tag}, доступна {latest_tag}")
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
    _log(f"найдена версия {latest_tag}; установлена {current_tag or 'неизвестная версия'}")

    with tempfile.TemporaryDirectory() as tmp:
        archive = os.path.join(tmp, "release.tar.gz")
        try:
            _log(f"загружаем архив {latest_tag}")
            with open(archive, "wb") as f:
                f.write(_fetch(tarball_url, timeout=300))
            expected = parse_sha256(_fetch(checksum_url).decode())
        except (OSError, ValueError, http.client.HTTPException) as e:
            _log(f"загрузка не удалась ({e})")
            return 0

        _log(f"проверяем контрольную сумму {latest_tag}")
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


def run():
    usb_monitor.update_log_location(start=True)
    current = usb_monitor.read_version()
    _log(f"проверка запущена, PID {os.getpid()}, версия {current[0] if current else 'неизвестна'}")
    try:
        return main()
    except Exception:
        traceback.print_exc()
        return 1
    finally:
        usb_monitor.update_log_location()
        usb_monitor.append_system_diagnostics()


if __name__ == "__main__":
    sys.exit(run())
