# Версия из релизов и автообновление — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Киоск показывает свою версию и сам ставит новые релизы с GitHub, откатываясь на предыдущую версию, если новая не стартует.

**Architecture:** Файл `VERSION` рядом с `gui.py` — единственный интерфейс между релизом и программой. Отдельный `updater.py`, запускаемый systemd-таймером, сверяет его с `releases/latest`, качает ассет, применяет через `install_native.sh` и откатывается при неудаче. GUI про обновления не знает — только читает `VERSION` и отмечает файлом-маркером, что идёт копирование.

**Tech Stack:** Python 3 (только stdlib: `urllib`, `hashlib`, `tarfile`, `shutil`, `subprocess`), systemd, bash, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-01-version-and-auto-update-design.md`

## Global Constraints

- Только stdlib. Новых зависимостей в `requirements.txt` не появляется.
- Репозиторий: `h1nigami/AstraUsbInstaller`, публичный. Токены и любые секреты на киоске не хранятся.
- Каталог приложения на машине: `/opt/astra-usb-monitor`. `data/` при обновлении не трогается.
- Имена папок бэкапов остаются `Device{id}` — они записаны в `backups.dest_path`.
- Формат `VERSION`: одна строка `<tag> <YYYY-MM-DD>`, например `v1.1 2026-07-17`.
- Тесты: `python3 -m unittest discover -s tests -v`, чистый stdlib, без GUI и сети.
- Перед каждым коммитом: `python3 -m py_compile gui.py usb_monitor.py main.py updater.py`.
- База падений на Windows — 10 тестов (Linux-специфика). Новые тесты обязаны проходить на Windows.

---

### Task 1: Номер устройства вместо DeviceN

Задача независима от остального плана и делается первой, чтобы не тащить её через все остальные правки.

**Files:**
- Modify: `usb_monitor.py:488-492`
- Modify: `gui.py:430`, `gui.py:739-747`, `gui.py:753-757`, `gui.py:845`, `gui.py:1076`, `gui.py:1109`
- Test: `tests/test_device_registry.py`

**Interfaces:**
- Consumes: ничего.
- Produces: `_friendly_device_label(device_id, name) -> str` — возвращает `name`, если задано, иначе `str(device_id)`.

- [ ] **Step 1: Написать падающий тест**

В конец `tests/test_device_registry.py` добавить:

```python
class FriendlyLabelTest(unittest.TestCase):
    def test_custom_name_wins(self):
        self.assertEqual(um._friendly_device_label(3, "Проходная"), "Проходная")

    def test_falls_back_to_bare_number(self):
        self.assertEqual(um._friendly_device_label(3, ""), "3")
        self.assertEqual(um._friendly_device_label(3, None), "3")
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `python3 -m unittest tests.test_device_registry.FriendlyLabelTest -v`
Expected: FAIL — `'Device3' != '3'`

- [ ] **Step 3: Поменять запасной вариант в обоих хелперах**

`usb_monitor.py`, заменить тело `_friendly_device_label`:

```python
def _friendly_device_label(device_id, name):
    """Human-facing label for a device: its custom name when set, else the
    bare number. The backup folder is named Device{id} regardless and is
    never renamed."""
    return name if name else str(device_id)
```

`gui.py`, в `_device_label` заменить последнюю строку:

```python
        return name if name else str(dev_id)
```

и в её docstring `stable Device{id}` заменить на `bare number`.

- [ ] **Step 4: Убедиться, что тест проходит**

Run: `python3 -m unittest tests.test_device_registry.FriendlyLabelTest -v`
Expected: PASS

- [ ] **Step 5: Подчистить оставшиеся подписи**

`gui.py:430`:

```python
        ttk.Label(edit_frame, text="Номер устройства:").pack(side="left", padx=2)
```

`gui.py:755` (фильтр на вкладке Поиск):

```python
            self._device_filter_ids = {
                (r[1] if r[1] else str(r[0])): r[0] for r in devices
            }
```

`gui.py:845` (колонка в результатах поиска):

```python
                            "device": dev_name if dev_name else str(dev_id),
```

`gui.py:1076`:

```python
            messagebox.showinfo("Готово", f"Устройство {dev_id} переименовано в {name or '(без имени)'}")
```

`gui.py:1109`:

```python
            messagebox.showwarning("Ошибка", "Некорректный номер устройства")
```

`gui.py:1113` (сборка пути к папке) **не трогать** — там остаётся `f"Device{dev_id}"`.

- [ ] **Step 6: Прогнать всё**

Run: `python3 -m py_compile gui.py usb_monitor.py main.py && python3 -m unittest discover -s tests`
Expected: компиляция чистая, падений не больше базовых 10

- [ ] **Step 7: Коммит**

```bash
git add usb_monitor.py gui.py tests/test_device_registry.py
git commit -m "Показывать номер устройства вместо DeviceN"
```

---

### Task 2: Чтение файла VERSION и показ версии в Настройках

**Files:**
- Modify: `usb_monitor.py` (рядом с `DEST_MARKER_FILE`, около строки 33)
- Modify: `gui.py:14` (импорт), `gui.py:543-546` (блок «О программе»)
- Test: `tests/test_version.py` (создать)

**Interfaces:**
- Consumes: ничего.
- Produces: `usb_monitor.read_version(path=None) -> tuple[str, str] | None` — пара `(tag, date)`, где `date` в формате `YYYY-MM-DD`; `None`, если файла нет или строка не разбирается. Используется в Task 4 апдейтером.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/test_version.py`:

```python
"""Tests for reading the VERSION file that ties the install to a GitHub release."""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import usb_monitor as um


class ReadVersionTest(unittest.TestCase):
    def _write(self, d, text):
        path = os.path.join(d, "VERSION")
        with open(path, "w") as f:
            f.write(text)
        return path

    def test_parses_tag_and_date(self):
        with tempfile.TemporaryDirectory() as d:
            path = self._write(d, "v1.1 2026-07-17\n")
            self.assertEqual(um.read_version(path), ("v1.1", "2026-07-17"))

    def test_missing_file_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(um.read_version(os.path.join(d, "VERSION")))

    def test_garbage_returns_none(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertIsNone(um.read_version(self._write(d, "мусор")))

    def test_extra_whitespace_is_tolerated(self):
        with tempfile.TemporaryDirectory() as d:
            path = self._write(d, "  v2.0   2026-09-01  \n")
            self.assertEqual(um.read_version(path), ("v2.0", "2026-09-01"))
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `python3 -m unittest tests.test_version -v`
Expected: FAIL — `module 'usb_monitor' has no attribute 'read_version'`

- [ ] **Step 3: Реализовать чтение**

В `usb_monitor.py` после строки `DEST_MARKER_FILE = ".astra_dest"` добавить:

```python
VERSION_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "VERSION")


def read_version(path=None):
    """Return (tag, date) from the VERSION file, or None when unavailable.

    The file is written by the release workflow and by install_native.sh; a
    missing or malformed file is normal for a source checkout and must never
    break the app.
    """
    try:
        with open(path or VERSION_FILE) as f:
            parts = f.read().split()
    except OSError:
        return None
    if len(parts) != 2:
        return None
    return parts[0], parts[1]
```

- [ ] **Step 4: Убедиться, что тест проходит**

Run: `python3 -m unittest tests.test_version -v`
Expected: PASS (4 теста)

- [ ] **Step 5: Показать версию в блоке «О программе»**

В `gui.py:14` добавить `read_version` в импорт из `usb_monitor`.

После строки с текстом «Автоматическое резервное копирование USB-устройств.» вставить:

```python
        ttk.Label(about, text=self._version_text(),
                  foreground=self.C["fg_muted"]).pack(anchor="w", pady=(6, 0))
```

И добавить метод рядом с `_refresh_timeout_status`:

```python
    def _version_text(self):
        v = read_version()
        if not v:
            return "Версия не определена"
        tag, date = v
        try:
            shown = datetime.strptime(date, "%Y-%m-%d").strftime("%d.%m.%y")
        except ValueError:
            return "Версия не определена"
        return f"Версия {tag.lstrip('v')} от {shown}"
```

- [ ] **Step 6: Прогнать проверки**

Run: `python3 -m py_compile gui.py usb_monitor.py && python3 -m unittest discover -s tests`
Expected: компиляция чистая, `test_version` проходит целиком

- [ ] **Step 7: Коммит**

```bash
git add usb_monitor.py gui.py tests/test_version.py
git commit -m "Читать версию из файла VERSION и показывать её в Настройках"
```

---

### Task 3: Маркер «идёт копирование»

**Files:**
- Modify: `usb_monitor.py` (рядом с `read_version`)
- Modify: `gui.py:1225-1231` (начало `_refresh_workers`)
- Test: `tests/test_version.py`

**Interfaces:**
- Consumes: ничего.
- Produces: `usb_monitor.touch_copying_marker()` — обновляет время файла `data/.copying`; `usb_monitor.is_copying(path=None, max_age=60) -> bool` — True, если маркер моложе `max_age` секунд. Апдейтер из Task 5 вызывает `is_copying()`.

- [ ] **Step 1: Написать падающий тест**

В `tests/test_version.py` добавить:

```python
class CopyingMarkerTest(unittest.TestCase):
    def test_absent_marker_means_idle(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertFalse(um.is_copying(os.path.join(d, ".copying")))

    def test_fresh_marker_means_busy(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, ".copying")
            with mock.patch.object(um, "COPYING_MARKER", path):
                um.touch_copying_marker()
            self.assertTrue(um.is_copying(path))

    def test_stale_marker_means_idle(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, ".copying")
            with open(path, "w"):
                pass
            old = time.time() - 3600
            os.utime(path, (old, old))
            self.assertFalse(um.is_copying(path))
```

И дописать в шапку файла импорты `time` и `from unittest import mock`.

- [ ] **Step 2: Убедиться, что тест падает**

Run: `python3 -m unittest tests.test_version.CopyingMarkerTest -v`
Expected: FAIL — `module 'usb_monitor' has no attribute 'is_copying'`

- [ ] **Step 3: Реализовать маркер**

В `usb_monitor.py` после `read_version` добавить:

```python
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
```

- [ ] **Step 4: Убедиться, что тест проходит**

Run: `python3 -m unittest tests.test_version.CopyingMarkerTest -v`
Expected: PASS (3 теста)

- [ ] **Step 5: Ставить маркер из GUI**

В `gui.py` добавить `touch_copying_marker` в импорт из `usb_monitor`, а в начало `_refresh_workers`, сразу после `tracked = set()`, вставить:

```python
        if any(d["state"] in ("Сканирование", "Копирование")
               for d in self.workers_data.values()):
            touch_copying_marker()
```

- [ ] **Step 6: Прогнать проверки**

Run: `python3 -m py_compile gui.py usb_monitor.py && python3 -m unittest discover -s tests`
Expected: компиляция чистая, падений не больше базовых 10

- [ ] **Step 7: Коммит**

```bash
git add usb_monitor.py gui.py tests/test_version.py
git commit -m "Отмечать маркером идущее копирование для апдейтера"
```

---

### Task 4: Апдейтер — разбор релиза и решение об обновлении

Сетевые запросы здесь не делаются: только чистые функции над уже полученным ответом, чтобы всё покрывалось тестами на Windows.

**Files:**
- Create: `updater.py`
- Test: `tests/test_updater.py` (создать)

**Interfaces:**
- Consumes: `usb_monitor.read_version`, `usb_monitor.is_copying`.
- Produces: `updater.pick_asset(release) -> tuple[str, str] | None` — пара URL `(tarball, sha256)`; `updater.needs_update(current, latest) -> bool`. Task 5 достраивает этот же файл.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/test_updater.py`:

```python
"""Tests for the release-payload logic of updater.py. No network here —
downloading and applying are exercised on the dock station."""

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import updater


RELEASE = {
    "tag_name": "v1.2",
    "published_at": "2026-08-20T10:00:00Z",
    "assets": [
        {"name": "astra-usb-monitor-v1.2.tar.gz",
         "browser_download_url": "https://example/app.tar.gz"},
        {"name": "astra-usb-monitor-v1.2.tar.gz.sha256",
         "browser_download_url": "https://example/app.sha256"},
    ],
}


class PickAssetTest(unittest.TestCase):
    def test_picks_tarball_and_checksum(self):
        self.assertEqual(updater.pick_asset(RELEASE),
                         ("https://example/app.tar.gz", "https://example/app.sha256"))

    def test_none_when_checksum_missing(self):
        release = {"assets": [RELEASE["assets"][0]]}
        self.assertIsNone(updater.pick_asset(release))

    def test_none_when_no_assets(self):
        self.assertIsNone(updater.pick_asset({"assets": []}))


class NeedsUpdateTest(unittest.TestCase):
    def test_same_tag_skips(self):
        self.assertFalse(updater.needs_update("v1.1", "v1.1"))

    def test_different_tag_updates(self):
        self.assertTrue(updater.needs_update("v1.1", "v1.2"))

    def test_unknown_local_version_updates(self):
        self.assertTrue(updater.needs_update(None, "v1.2"))

    def test_missing_latest_never_updates(self):
        self.assertFalse(updater.needs_update("v1.1", None))
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `python3 -m unittest tests.test_updater -v`
Expected: FAIL — `No module named 'updater'`

- [ ] **Step 3: Создать updater.py с этими функциями**

```python
#!/usr/bin/env python3
"""Автообновление киоска с релизов GitHub.

Запускается systemd-таймером, а не из GUI: install_native.sh в конце
перезапускает сервис приложения, и обновление, идущее внутри этого сервиса,
убило бы само себя посреди подмены файлов.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

REPO = "h1nigami/AstraUsbInstaller"
LATEST_URL = f"https://api.github.com/repos/{REPO}/releases/latest"
APP_DIR = os.path.dirname(os.path.abspath(__file__))
PREV_DIR = APP_DIR + ".prev"
SERVICE = "astra-usb-monitor"


def pick_asset(release):
    """Return (tarball_url, sha256_url) from a release payload, or None.

    Both are required: without the checksum we refuse to install anything.
    """
    tarball = checksum = None
    for asset in release.get("assets", []):
        name = asset.get("name", "")
        url = asset.get("browser_download_url")
        if name.endswith(".sha256"):
            checksum = url
        elif name.endswith(".tar.gz"):
            tarball = url
    if tarball and checksum:
        return tarball, checksum
    return None


def needs_update(current, latest):
    """Compare tags for inequality, not order: the point is brought to
    whatever GitHub marks as latest, so a withdrawn release is fixed by
    publishing the previous one instead of driving to the site."""
    if not latest:
        return False
    return current != latest
```

- [ ] **Step 4: Убедиться, что тест проходит**

Run: `python3 -m unittest tests.test_updater -v`
Expected: PASS (7 тестов)

- [ ] **Step 5: Коммит**

```bash
git add updater.py tests/test_updater.py
git commit -m "Апдейтер: разбор релиза и решение об обновлении"
```

---

### Task 5: Апдейтер — загрузка, проверка суммы, установка и откат

**Files:**
- Modify: `updater.py`
- Test: `tests/test_updater.py`

**Interfaces:**
- Consumes: `updater.pick_asset`, `updater.needs_update` из Task 4; `usb_monitor.read_version`, `usb_monitor.is_copying` из Task 2 и 3.
- Produces: `updater.sha256_of(path) -> str`; `updater.main() -> int` — код возврата для systemd.

- [ ] **Step 1: Написать падающий тест на контрольную сумму**

В `tests/test_updater.py` добавить (и дописать в шапку `import hashlib`, `import tempfile`):

```python
class Sha256Test(unittest.TestCase):
    def test_matches_hashlib(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "blob.bin")
            payload = b"astra" * 100000
            with open(path, "wb") as f:
                f.write(payload)
            self.assertEqual(updater.sha256_of(path),
                             hashlib.sha256(payload).hexdigest())

    def test_reads_checksum_field_from_sha_file(self):
        # GitHub-style "<hex>  <filename>" line
        self.assertEqual(
            updater.parse_sha256("abc123  astra-usb-monitor-v1.2.tar.gz\n"),
            "abc123")

    def test_parse_sha256_handles_bare_hex(self):
        self.assertEqual(updater.parse_sha256("abc123\n"), "abc123")
```

- [ ] **Step 2: Убедиться, что тест падает**

Run: `python3 -m unittest tests.test_updater.Sha256Test -v`
Expected: FAIL — `module 'updater' has no attribute 'sha256_of'`

- [ ] **Step 3: Дописать updater.py**

Добавить импорты в шапку файла:

```python
import hashlib
import json
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request
```

И функции после `needs_update`:

```python
def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def parse_sha256(text):
    """Checksum files come as bare hex or as '<hex>  <filename>'."""
    return text.split()[0] if text.split() else ""


def _fetch(url, timeout=15):
    req = urllib.request.Request(url, headers={"User-Agent": "astra-usb-monitor"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def _log(msg):
    print(f"[updater] {msg}", flush=True)


def _service_healthy():
    """Active and not looping through restarts."""
    try:
        active = subprocess.run(["systemctl", "is-active", "--quiet", SERVICE],
                                timeout=10).returncode == 0
        shown = subprocess.run(["systemctl", "show", "-p", "NRestarts", "--value", SERVICE],
                               capture_output=True, text=True, timeout=10).stdout.strip()
        restarts = int(shown or 0)
    except Exception:
        return False
    return active and restarts == 0


def _rollback():
    _log("новая версия не поднялась — откат на предыдущую")
    shutil.rmtree(APP_DIR, ignore_errors=True)
    shutil.move(PREV_DIR, APP_DIR)
    subprocess.run(["systemctl", "restart", SERVICE], timeout=120)


def _apply(src_dir):
    """Back up the current install, run the fresh installer, verify, roll back."""
    shutil.rmtree(PREV_DIR, ignore_errors=True)
    shutil.copytree(APP_DIR, PREV_DIR, symlinks=True)

    installer = os.path.join(src_dir, "install_native.sh")
    result = subprocess.run(["bash", installer], cwd=src_dir, timeout=1800)
    if result.returncode != 0:
        _rollback()
        return 1

    subprocess.run(["systemctl", "reset-failed", SERVICE], timeout=30)
    time.sleep(60)
    if not _service_healthy():
        _rollback()
        return 1

    shutil.rmtree(PREV_DIR, ignore_errors=True)
    _log("обновление установлено")
    return 0


def main():
    current = usb_monitor.read_version()
    current_tag = current[0] if current else None

    try:
        release = json.loads(_fetch(LATEST_URL))
    except Exception as e:
        _log(f"релиз не проверен ({e}) — пробуем в следующий раз")
        return 0

    latest_tag = release.get("tag_name")
    if not needs_update(current_tag, latest_tag):
        _log(f"актуальная версия: {current_tag}")
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

        unpacked = os.path.join(tmp, "src")
        with tarfile.open(archive) as tar:
            tar.extractall(unpacked)
        roots = [os.path.join(unpacked, n) for n in os.listdir(unpacked)]
        src_dir = roots[0] if len(roots) == 1 and os.path.isdir(roots[0]) else unpacked

        _log(f"ставим {latest_tag} (было {current_tag})")
        return _apply(src_dir)


if __name__ == "__main__":
    sys.exit(main())
```

Дописать в шапку `import time` и `import usb_monitor`.

- [ ] **Step 4: Убедиться, что тесты проходят**

Run: `python3 -m unittest tests.test_updater -v`
Expected: PASS (10 тестов)

- [ ] **Step 5: Проверить компиляцию**

Run: `python3 -m py_compile updater.py`
Expected: без вывода

- [ ] **Step 6: Коммит**

```bash
git add updater.py tests/test_updater.py
git commit -m "Апдейтер: загрузка релиза, сверка суммы, установка и откат"
```

---

### Task 6: Установщик раскладывает апдейтер и включает таймер

**Files:**
- Modify: `install_native.sh:138-140` (список копируемых файлов), `install_native.sh:150` (после блока сервиса)

**Interfaces:**
- Consumes: `updater.py` из Task 5.
- Produces: юниты `astra-usb-update.service` и `astra-usb-update.timer` на машине; файл `$APP_DIR/VERSION`.

- [ ] **Step 1: Копировать VERSION и updater.py**

Заменить блок копирования:

```bash
$SUDO cp "$SRC_DIR/main.py" "$SRC_DIR/gui.py" "$SRC_DIR/usb_monitor.py" \
         "$SRC_DIR/updater.py" "$SRC_DIR/start_native.sh" "$APP_DIR/"
$SUDO chmod 755 "$APP_DIR/start_native.sh"

# VERSION приезжает в архиве релиза. При установке из git-клона его нет —
# собираем из тега, чтобы в Настройках было видно, что именно стоит.
if [ -f "$SRC_DIR/VERSION" ]; then
    $SUDO cp "$SRC_DIR/VERSION" "$APP_DIR/VERSION"
elif [ -d "$SRC_DIR/.git" ] && command -v git >/dev/null 2>&1; then
    _tag=$(cd "$SRC_DIR" && git describe --tags --abbrev=0 2>/dev/null || true)
    if [ -n "$_tag" ]; then
        _date=$(cd "$SRC_DIR" && git log -1 --format=%cd --date=format:%Y-%m-%d "$_tag")
        echo "$_tag $_date" | $SUDO tee "$APP_DIR/VERSION" > /dev/null
    fi
fi
```

- [ ] **Step 2: Добавить юниты автообновления**

После `systemctl restart "$SERVICE_NAME.service"` вставить:

```bash
# --- 6. Автообновление --------------------------------------------------------
echo "--- Настройка автообновления с GitHub..."
$SUDO tee "/etc/systemd/system/astra-usb-update.service" > /dev/null << EOF
[Unit]
Description=Astra USB Monitor — проверка и установка обновлений с GitHub
After=network-online.target

[Service]
Type=oneshot
WorkingDirectory=$APP_DIR
ExecStart=/usr/bin/python3 $APP_DIR/updater.py
Environment=PYTHONUNBUFFERED=1
EOF

$SUDO tee "/etc/systemd/system/astra-usb-update.timer" > /dev/null << 'EOF'
[Unit]
Description=Проверять обновления Astra USB Monitor

[Timer]
OnBootSec=10min
OnUnitActiveSec=6h
Persistent=true

[Install]
WantedBy=timers.target
EOF
$SUDO systemctl daemon-reload
$SUDO systemctl enable --now astra-usb-update.timer
```

- [ ] **Step 3: Проверить синтаксис**

Run: `bash -n install_native.sh`
Expected: без вывода

- [ ] **Step 4: Коммит**

```bash
git add install_native.sh
git commit -m "Установщик кладёт апдейтер, VERSION и включает таймер обновлений"
```

---

### Task 7: Релизный workflow

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: ничего.
- Produces: ассеты релиза `astra-usb-monitor-<tag>.tar.gz` и `.tar.gz.sha256`, внутри архива — `VERSION`. Их читает `updater.pick_asset` из Task 4.

- [ ] **Step 1: Создать workflow**

```yaml
name: Release — сборка архива с версией

on:
  release:
    types: [published]

permissions:
  contents: write

jobs:
  package:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Собрать VERSION и архив
        run: |
          TAG="${{ github.event.release.tag_name }}"
          DATE="${{ github.event.release.published_at }}"
          DATE="${DATE%%T*}"
          echo "$TAG $DATE" > VERSION
          NAME="astra-usb-monitor-${TAG}.tar.gz"
          tar --exclude=.git --exclude=.github -czf "$NAME" \
              main.py gui.py usb_monitor.py updater.py \
              start_native.sh install_native.sh update.sh diagnose.sh \
              start.sh Dockerfile docker-compose.yml requirements.txt \
              99-astra-usb-monitor-udisks.rules VERSION data tests
          sha256sum "$NAME" > "${NAME}.sha256"
          echo "ASSET=$NAME" >> "$GITHUB_ENV"

      - name: Прикрепить к релизу
        run: gh release upload "${{ github.event.release.tag_name }}" \
             "$ASSET" "${ASSET}.sha256" --clobber
        env:
          GH_TOKEN: ${{ github.token }}
```

- [ ] **Step 2: Проверить YAML**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml'))"`
Expected: без вывода (если `pyyaml` нет — пропустить, синтаксис проверит GitHub)

- [ ] **Step 3: Коммит**

```bash
git add .github/workflows/release.yml
git commit -m "Workflow: прикреплять к релизу архив с VERSION и контрольной суммой"
```

---

### Task 8: Проверка на док-станции

Всё, что нельзя проверить на Windows. Выполняется на 192.168.0.41 после того, как там поднят `openssh-server`.

**Files:** ничего не меняется, только правки по результатам.

- [ ] **Step 1: Поставить свежую версию из git-клона**

```bash
./install_native.sh
cat /opt/astra-usb-monitor/VERSION
systemctl status astra-usb-monitor astra-usb-update.timer
```
Expected: сервис активен, таймер включён, в `VERSION` тег и дата.

- [ ] **Step 2: Проверить показ версии**

Открыть Настройки → «О программе».
Expected: строка «Версия 1.1 от 17.07.26» с реальными значениями.

- [ ] **Step 3: Прогнать апдейтер вручную на актуальной версии**

```bash
sudo python3 /opt/astra-usb-monitor/updater.py
```
Expected: «актуальная версия: vX.Y», ничего не переустановлено.

- [ ] **Step 4: Проверить поведение без сети**

```bash
IFACE=$(ip route show default | awk '{print $5; exit}')
sudo ip link set "$IFACE" down
sudo python3 /opt/astra-usb-monitor/updater.py; echo "код: $?"
sudo ip link set "$IFACE" up
```
Expected: сообщение «релиз не проверен», код 0, приложение работает.

- [ ] **Step 5: Проверить отказ при занятости**

Вставить флешку, дождаться начала копирования, параллельно запустить апдейтер.
Expected: «идёт копирование — обновление отложено».

- [ ] **Step 6: Проверить обновление и откат**

Опубликовать тестовый релиз, дождаться таймера или запустить апдейтер вручную.
Expected: версия в Настройках сменилась, `journalctl -u astra-usb-update` показывает установку.

Затем опубликовать заведомо сломанный релиз (например, с синтаксической ошибкой в `gui.py`).
Expected: `journalctl` показывает откат, приложение работает на прежней версии.

- [ ] **Step 7: Зафиксировать результаты**

Найденные расхождения исправить и закоммитить отдельными правками со ссылкой на шаг, который их выявил.

---

## Порядок и зависимости

Task 1 независима и может делаться в любой момент. Task 2 и 3 дают функции, на которые опирается Task 5, поэтому идут раньше. Task 4 и 5 — один файл, разделены по границе «покрывается тестами на Windows» и «проверяется только на Linux». Task 6 и 7 бессмысленны без Task 5. Task 8 выполняется последней и только на живой машине.
