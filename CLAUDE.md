# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Run (auto-detects GUI vs headless based on $DISPLAY)
python main.py

# Force headless
python usb_monitor.py

# Docker
docker compose up -d --build
docker compose logs -f

# Syntax check
python3 -m py_compile gui.py usb_monitor.py main.py

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

Three layers:

**`usb_monitor.py`** — core engine (no GUI dependency)
- `monitor_usb(interval, stop_event, progress_queue)` — main loop; detects USB attach/detach via polling `lsblk` (Linux) or `GetDriveTypeW` (Windows). Removal is debounced: a device must be missing for ≥1.5× the poll interval before it is confirmed gone.
- `copy_task()` → `_copy_files()` — incremental backup: skips files matching size+mtime, renames changed files with `_YYYYMMDD_HHMMSS` suffix. `_copy_files` returns the set of source paths that are safely present at the destination (copied or already identical); only those are passed to `_delete_source_videos()`, so a video whose copy failed is never deleted from the source.
- Each backup runs in its own `ThreadPoolExecutor` worker and opens its own SQLite connection via `_connect()` (sharing one connection across the pool is not safe for concurrent writes). `_init_db()` is called once at startup to create the schema / run migrations, then closed.
- `_resolve_device_id()` — stable device identity: reads/writes `.astra_id` on the USB, falls back to serial number lookup in SQLite, then creates a new record. The `.astra_id` marker file is excluded from scans and copies.
- `_parse_lsblk_tree()` — pure helper over parsed `lsblk -J` output (unit-tested); partitions of a USB disk are listed exactly once, a whole-disk filesystem yields the disk itself.
- SQLite DB at `data/devices.db`: tables `devices` (serial, label, person) and `backups` (per-session stats). `started_at`/`finished_at` are stored via `datetime.isoformat()` (`T` separator).
- `format_filter_dt()` — builds search range bounds with the same `T` separator as stored `started_at` so lexicographic SQL comparisons are correct (a space would sort before `T` and wrongly exclude same-day backups).
- Progress emission: when `progress_queue` is provided, puts tuples `(device_id, display_id, state, current, total, msg, devname)` for GUI consumption; special sentinel device_ids `"_removed_"` and `"_status_"` signal device removal and status updates.

**`gui.py`** — Tkinter fullscreen GUI
- `App` class owns the notebook (4 tabs: Загрузка, Поиск, Устройства, Настройки)
- Runs `monitor_usb` in a daemon thread; polls `progress_queue` every 200ms via `root.after`
- Tab access protection: tabs at indices 1–3 require a password; `_prompt_unlock()` is modal, sized to 1/4 screen. Unlocked tabs re-lock after `lock_timeout_minutes` of inactivity (`_check_lock_timeout`).
- Search tab runs queries in a background thread (`_search_worker`, generation-guarded), walks the matched backup folders on disk, and can export the matched files (`_export_worker`). Results are capped at 500.
- Exit is password-protected: the header has a visible "⏻ Выход" button (only way out in fullscreen kiosk mode, since the window has no close button); it calls `_on_close()`, a modal password dialog that on success runs `stop_event.set()` + `root.destroy()`. Same dialog is bound to `WM_DELETE_WINDOW`.
- Password stored in `data/config.json`; default `exit`; also reads `APP_EXIT_PASSWORD` env var on first run; change via Настройки tab (`_change_password`). The Настройки tab also configures the backup destination, lock timeout, and auto-cleanup of old videos.

**`main.py`** — entry point; launches GUI if `$DISPLAY` is set or on Windows, otherwise falls back to headless `monitor_usb()`.

## Key environment variables

| Variable | Default | Effect |
|---|---|---|
| `USB_BACKUP_DEST` | `./USB_Backups` | Backup root; device folders are `Device{id}/` inside. `data/config.json`'s `backup_dest` overrides this (see `get_dest_base`). |
| `USB_DB_PATH` | `./data/devices.db` | SQLite DB path |
| `USB_MAX_WORKERS` | `10` | ThreadPoolExecutor size |
| `USB_DEBUG` | `0` | Enable debug output |
| `APP_EXIT_PASSWORD` | `exit` | Initial exit/unlock password (only used on first run, then persisted to `config.json`) |

## Docker

Runs privileged with `/dev`, `/sys`, `/proc`, `/run/udev` mounted. GUI via X11
socket passthrough (`/tmp/.X11-unix`). The image is lean: `requirements.txt`
only pulls `rich`; system packages are `util-linux`/`udev`/`mount`/`ntfs-3g`/
etc. for mounting USB filesystems, plus `python3-tk` and `x11-utils` for the GUI.

`start.sh` is the container entrypoint and handles the kiosk lifecycle:
- If `DISPLAY` is empty → headless `usb_monitor.py`.
- Otherwise it runs the USB monitor in the background and waits for X11 to
  become reachable. Because the container starts before the desktop session,
  it discovers the real session X11 cookie via `/proc/<pid>/{environ,cmdline,root}`
  (works thanks to `pid:host` + privileged) and **honestly verifies** the
  connection with `xdpyinfo` rather than trusting the error text.
- When X11 is up it stops the background monitor and launches the GUI. If the
  GUI exits with code 0 (the password-protected "Выход") it does **not**
  relaunch — it drops to headless monitoring so the kiosk exit is meaningful.
  A non-zero exit (e.g. the X session died) is treated as a crash and the GUI
  relaunch loop continues.

`.gitattributes` enforces LF endings; `start.sh` is also stripped of CR at build
time. CI: two GitHub Actions workflows (`pr-docker-build.yml`,
`main-docker-build.yml`) build the image via `docker/build-push-action` with
`type=gha` cache.

## Development branch

Active feature branch: `claude/project-review-bugs-k0neon`. Base: `master`.
