# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Commands

```bash
# Run (auto-detects GUI vs headless based on $DISPLAY)
python main.py

# Force headless
python usb_monitor.py

# Syntax check
python3 -m py_compile gui.py usb_monitor.py main.py updater.py

# Tests (pure stdlib unittest, no GUI/X11 needed)
python3 -m unittest discover -s tests -v

# Windows launcher
run_gui.bat
```

Run `py_compile` and the unittest suite before committing Python changes. The
tests in `tests/` cover the GUI-free core logic in `usb_monitor.py` (formatting,
the search date filter, lsblk parsing, scanning, and the copy/auto-delete
safety guarantees) — extend them when you touch that logic.

## Architecture

Four top-level modules:

**`usb_monitor.py`** — core engine (no GUI dependency)
- `monitor_usb(interval, stop_event, progress_queue)` — main loop; detects USB attach/detach via polling `lsblk` (Linux) or `GetDriveTypeW` (Windows). Removal is debounced: a device must be missing for ≥1.5× the poll interval before it is confirmed gone.
- `copy_task()` → `_copy_files()` — incremental backup: skips files matching size+mtime, renames changed files with `_YYYYMMDD_HHMMSS` suffix. `_copy_files` returns the set of source paths that are safely present at the destination (copied or already identical) plus a failed-copy count; only backed-up paths are passed to `_delete_source_videos()`, so a video whose copy failed is never deleted from the source. Failures surface as an `error` progress state (red in the GUI) instead of a false "Done".
- Mount reuse (double-mount safety): native installs now also drop a `udev` rule (`99-astra-usb-monitor-udisks.rules`) that sets `UDISKS_AUTO=0` for USB filesystems, so the desktop normally does **not** auto-mount a fresh stick behind our back. `copy_task_linux` still keeps the older reuse path as a safety net for upgraded systems / manual mounts: it waits up to `USB_MOUNT_GRACE` seconds for a mount the system already owns (`_wait_for_system_mount` → `_find_existing_mount`, which reads `/proc/mounts`) and reuses it; only when none appears does it self-mount via `_mount_device`. This prevents two concurrent **read-write** mounts of the same FAT/exFAT stick — the failure the app was writing (`.astra_id`, auto-delete) into, which lets two uncoordinated FAT caches corrupt the filesystem (the stick then shows 0 B and refuses to remount). `should_unmount` is set only when we own the mount (`_is_own_mount`, under `MOUNT_BASE`), so the desktop's own mount is never torn down.
- Destination stability across mountpoints: a GUI-selected destination now stores not just the chosen path but also the filesystem UUID/serial plus the relative path inside that filesystem. If native mode later mounts the same disk under `/mnt/usb_backup/<dev>`, `get_dest_base()` resolves the live path there, so the destination disk is still recognised as destination (not as source) and backups keep landing on the real disk without requiring the desktop's old mountpoint.
- Destination availability: a GUI-selected `backup_dest` is stamped with a `.astra_dest` marker (`ensure_dest_marker`) at selection time, and `copy_task` refuses to write (state `error`) while the marker is absent (`dest_available()`) — a missing marker means the destination disk is not mounted at that path, and `makedirs` would otherwise silently back up into a shadow directory on the root/overlay FS. The drive hosting the destination (`_is_dest_path`) is never treated as a backup source and is kept mounted (`copy_task_linux` skips it, `_mount_device` tolerates an existing mount); reconnecting it re-stamps the marker.
- Each backup runs in its own `ThreadPoolExecutor` worker and opens its own SQLite connection via `_connect()` (sharing one connection across the pool is not safe for concurrent writes). `_init_db()` is called once at startup to create the schema / run migrations, then closed.
- `_resolve_device_id()` использует положительное целое число из `.astra_id` как единственный ID устройства. Носитель без маркера получает следующий локальный `devices.id` от SQLite `AUTOINCREMENT`; заводской USB-серийник хранится только как служебная информация и не объединяет носители. Повреждённый маркер или Astra ID, уже занятый другим подключённым носителем, останавливает этот носитель до сканирования, копирования и автоудаления без перезаписи маркера. Маркер исключён из сканирования и копирования.
- `_parse_lsblk_tree()` — pure helper over parsed `lsblk -J` output (unit-tested); partitions of a USB disk are listed exactly once, a whole-disk filesystem yields the disk itself.
- SQLite DB at `data/devices.db`: tables `devices` (serial, label, person) and `backups` (per-session stats). `started_at`/`finished_at` are stored via `datetime.isoformat()` (`T` separator).
- `format_filter_dt()` — builds search range bounds with the same `T` separator as stored `started_at` so lexicographic SQL comparisons are correct (a space would sort before `T` and wrongly exclude same-day backups).
- Progress emission: when `progress_queue` is provided, puts tuples `(device_id, display_id, state, current, total, msg, devname)` for GUI consumption; special sentinel device_ids `"_removed_"` and `"_status_"` signal device removal and status updates.
- `read_version()` — parses the `VERSION` file (`<tag> <YYYY-MM-DD>`, written by the release workflow / `install_native.sh`, never by hand) into `(tag, date)`, or `None` if it's missing or malformed. Read by the GUI (Настройки tab) and by `updater.py`.
- `touch_copying_marker()` / `is_copying()` — `data/.copying` is the interface between the GUI and `updater.py`: the GUI stamps its mtime whenever a device is scanning or copying, and `updater.py` treats the point as busy while the marker is younger than 60s. A stale or missing marker means idle, so a crashed GUI doesn't block updates forever.

## Astra ID и папки архива

- Python и Avalonia называют архивную папку `Device{astra_id}` и показывают тот же `Astra ID N` на главном экране. Пользовательское имя остаётся дополнительной подписью и не меняет папку.
- Avalonia читает `.bestcam_id` только для одноразового переноса, если `.astra_id` отсутствует, а старый маркер однозначно указывает на одну существующую запись локальной базы. В `.astra_id` записывается `devices.id` этой записи. Неизвестный или неоднозначный старый маркер получает новый локальный ID.
- На Linux обе службы рекурсивно передают прямые папки с положительным числовым именем `DeviceN` владельцу и группе корня архива. При запуске исправляются существующие папки, после копирования текущая папка устройства. Соседние и вложенные каталоги не затрагиваются.

**`gui.py`** — Tkinter fullscreen GUI
- `App` class owns the notebook (4 tabs: Загрузка, Поиск, Устройства, Настройки)
- Runs `monitor_usb` in a daemon thread; polls `progress_queue` every 200ms via `root.after`; `_poll_queue` also stamps `touch_copying_marker()` on every tick (via the pure `_is_busy(workers_data)` helper, keyed off the raw `scanning`/`copying` state, not the localized label) — this runs regardless of queue traffic, since a single large file can copy for minutes with no progress message in between.
- Tab access protection: tabs at indices 1–3 require a password; `_prompt_unlock()` is modal, sized to 1/4 screen. Unlocked tabs re-lock after `lock_timeout_minutes` of inactivity (`_check_lock_timeout`).
- Search tab runs queries in a background thread (`_search_worker`, generation-guarded), walks the matched backup folders on disk, and can export the matched files (`_export_worker`). Results are capped at 500.
- Exit is password-protected: the header has a visible "⏻ Выход" button (only way out in fullscreen kiosk mode, since the window has no close button); it calls `_on_close()`, a modal password dialog that on success runs `stop_event.set()` + `root.destroy()`. Same dialog is bound to `WM_DELETE_WINDOW`.
- Password stored in `data/config.json`; default `exit`; also reads `APP_EXIT_PASSWORD` env var on first run; change via Настройки tab (`_change_password`). The Настройки tab also configures the backup destination, lock timeout, and auto-cleanup of old videos.

**`main.py`** — entry point; launches GUI if `$DISPLAY` is set or on Windows, otherwise falls back to headless `monitor_usb()`.

**`updater.py`** — auto-update, run by a systemd timer (`astra-usb-update.timer`), not from inside the GUI service (so the installer's final `systemctl restart` of the GUI service can't kill an update mid-file-swap)
- `main()`: reads `VERSION`, polls `releases/latest` of the public `h1nigami/AstraUsbInstaller` repo, compares tags for inequality (not ordering — the point is always brought to whatever GitHub marks latest), skips while `usb_monitor.is_copying()` or while the latest tag is the one already recorded as failed, downloads the release asset + `.sha256` and refuses to install on a mismatch, then hands off to `_apply()`.
- `_apply()`: сохраняет код в `APP_DIR.prev`, сбрасывает счётчик перезапусков до установки и запускает `install_native.sh`. Затем проверяет импорт Python, активность службы, отсутствие перезапусков и совпадение `VERSION` с тегом. Любая ошибка сохраняет неудачный тег и вызывает откат; резервная копия удаляется только после всех проверок. `start_native.sh` запускает GUI через `exec`, поэтому systemd видит падения процесса.
- `_rollback()` restores only `APP_DIR`; the systemd units and udev rule it installs are not reverted (deliberate — see the comment in the function and the spec's rollback section for the ceiling this implies).
- Only stdlib (`urllib`, `hashlib`, `tarfile`, `shutil`, `subprocess`); no secrets on the point since the repo is public.

## Key environment variables

| Variable | Default | Effect |
|---|---|---|
| `USB_BACKUP_DEST` | `./USB_Backups` | Backup root; device folders are `Device{astra_id}/` inside. `data/config.json`'s `backup_dest` overrides this (see `get_dest_base`). |
| `USB_DB_PATH` | `./data/devices.db` | SQLite DB path |
| `USB_MAX_WORKERS` | `10` | ThreadPoolExecutor size |
| `USB_MOUNT_GRACE` | `4` | Seconds to wait for an existing system mount before self-mounting (upgrade/manual-mount fallback). Native installs normally suppress desktop auto-mount via `UDISKS_AUTO=0`, so this is mostly a safety net now. |
| `USB_DEBUG` | `0` | Enable debug output |
| `APP_EXIT_PASSWORD` | `exit` | Initial exit/unlock password (only used on first run, then persisted to `config.json`) |

## Установка и проверки

Python устанавливается через `install_native.sh`, C# через установщики
в `avalonia/`. Docker больше не используется для установки станции.
Контейнеры допустимы только как изолированная среда тестов.

`tests.yml` проверяет Python и синтаксис shell-скриптов на pull request
и при изменении master. `release.yml` собирает Python-архив и SHA256;
`release-station.yml` собирает отдельные релизы C# по правилам ниже.
В нативном установщике сохраняется остановка старого контейнера
`astra-usb-monitor` для безопасного перехода прежних установок.

## Version and auto-update

- `VERSION` (one line, `<tag> <YYYY-MM-DD>`) is the only interface between a
  GitHub release and the running app. It ships inside the release archive and
  is never hand-edited; `install_native.sh` also derives it from
  `git describe --tags` when installing from a git clone that has no
  `VERSION` file (best-effort — a tagless clone installs with no version, not
  an error). Shown in Настройки via `usb_monitor.read_version()`; the GUI
  never fails over a missing/malformed version.
- `install_native.sh` also installs `astra-usb-update.{service,timer}`, a
  systemd timer that runs `updater.py` 10 minutes after boot and every 6h
  after that, `enable --now`. Installing from a git clone with no tags means
  no `VERSION`, so the very first tick treats the point as needing an update
  and replaces the local build with whatever is latest on GitHub — the
  installer prints a warning about this when it detects a non-release install.
- A bad release cannot be fixed by re-publishing the same tag: the failed tag
  is remembered (`APP_DIR.failed`) and skipped on every later tick. Publish a
  new tag instead. See `README.md`'s "Обновления" section and
  `docs/superpowers/specs/2026-09-01-version-and-auto-update-design.md` for
  the full design.

## Разделение релизов и автообновлений Python и C#

Python и C#/Avalonia являются отдельными продуктами с независимыми обновлениями.
Станция должна обновляться только в пределах установленного продукта. Переход
между ними разрешён только по явному запросу пользователя на миграцию.

- Python использует теги `v1.*` и архивы `astra-usb-monitor-<tag>.tar.gz`.
  C#/Avalonia использует теги `v2.*`, архивы `bestcam-station-<tag>-<rid>.*`,
  пакеты `bestcam-station_*.deb` и установщики `BestCamStationSetup-*.exe`.
- В одном релизе запрещено размещать сборки обоих продуктов. Выпуск Python
  не должен запускать сборку или публикацию C#, и наоборот.
- Перед выпуском проверь ограничения всех заданий в `release.yml` и
  `release-station.yml`, включая `windows-setup`, ручной запуск и повторный
  запуск. Проверка только `prerelease` недостаточна: нужна проверка семейства
  тега. Если workflow публикует чужой продукт, сначала исправь workflow.
- Каждый апдейтер должен проверять семейство релиза, префикс имени архива,
  платформу и соответствующую контрольную сумму. Чужой релиз или отсутствие
  своего архива означают пропуск обновления, а не выбор другого установщика.
- Python читает стабильный `releases/latest`; C# выбирает последний готовый
  релиз `v2.*` для своей платформы из списка релизов, включая prerelease.
  Проверяются семейство тега, точное имя архива и его сумма. Старые
  апдейтеры продолжают читать общий latest, поэтому смешанные архивы
  запрещены и после разделения каналов в коде.
- Пока обновлённые апдейтеры не установлены на всех действующих
  станциях, `latest` предназначен только для Python. C# публикуется как
  предварительный релиз без Python-архивов и без назначения `latest`.
- Перед публикацией проверь целевой коммит, семейство тега и состав файлов.
  После публикации проверь фактические вложения через GitHub API и выбор
  архива обоими апдейтерами. Ошибочно опубликованные чужие файлы удали сразу;
  факт установки на станции подтверждай отдельно, по её версии или журналу.

Это обязательные правила выпуска. Перед каждым релизом проверяй фактические
условия GitHub Actions и состав архивов, включая повторный запуск старых workflow.

## Development branch

Active feature branch: `Codex/version-and-auto-update`. Base: `master`.
