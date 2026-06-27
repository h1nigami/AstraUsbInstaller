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

# Windows launcher
run_gui.bat
```

No test suite exists. Syntax check with `py_compile` before committing Python changes.

## Architecture

Three layers:

**`usb_monitor.py`** — core engine (no GUI dependency)
- `monitor_usb(interval, stop_event, progress_queue)` — main loop; detects USB attach/detach via polling `lsblk` (Linux) or `GetDriveTypeW` (Windows)
- `copy_task()` → `_copy_files()` — incremental backup: skips files matching size+mtime, renames changed files with `_YYYYMMDD_HHMMSS` suffix
- `_resolve_device_id()` — stable device identity: reads/writes `.astra_id` on the USB, falls back to serial number lookup in SQLite, then creates new record
- SQLite DB at `data/devices.db`: tables `devices` (serial, label, person) and `backups` (per-session stats)
- Progress emission: when `progress_queue` is provided, puts tuples `(device_id, display_id, state, current, total, msg, devname)` for GUI consumption; special sentinel device_ids `"_removed_"` and `"_status_"` signal device removal and status updates

**`gui.py`** — Tkinter fullscreen GUI
- `App` class owns the notebook (4 tabs: Загрузка, Поиск, Устройства, Настройки)
- Runs `monitor_usb` in a daemon thread; polls `progress_queue` every 200ms via `root.after`
- Tab access protection: tabs at indices 1–3 require password; `_prompt_unlock()` is modal, sized to 1/4 screen
- Password stored in `data/config.json`; default `exit`; also reads `APP_EXIT_PASSWORD` env var on first run
- Nanosuit voice greeting runs in a daemon thread at startup via `_nanosuit_greeting()` → espeak-ng + Python DSP (numpy/scipy)

**`main.py`** — entry point; launches GUI if `$DISPLAY` is set or on Windows, otherwise falls back to headless `monitor_usb()`

## DSP audio chain (`gui.py`)

`_HAVE_DSP` guards all numpy/scipy usage — app works without them (falls back to plain espeak).

Pipeline: espeak-ng stdout WAV → `_wav_bytes_to_float()` → `_nanosuit_fx()` → `_play_processed_wav()`

Pitch shift is done by writing the WAV with a lower framerate (`write_sr = int(sr * 2**(_PITCH_CENTS/1200))`), not by resampling the audio array. DSP constants (`_PITCH_CENTS`, `_ECHO*`, `_REVERB_AMOUNT`, `_BASS_*`, `_TREBLE_*`) are module-level.

## Key environment variables

| Variable | Default | Effect |
|---|---|---|
| `USB_BACKUP_DEST` | `./USB_Backups` | Backup root; device folders are `Device{id}/` inside |
| `USB_DB_PATH` | `./data/devices.db` | SQLite DB path |
| `USB_MAX_WORKERS` | `10` | ThreadPoolExecutor size |
| `USB_DEBUG` | `0` | Enable debug output |
| `APP_EXIT_PASSWORD` | `exit` | Initial exit/unlock password |

## Docker

Runs privileged with `/dev`, `/sys`, `/proc`, `/run/udev` mounted. Audio via ALSA (`/dev/snd` device, `audio` group). GUI via X11 socket passthrough (`/tmp/.X11-unix`). `start.sh` auto-detects `$DISPLAY`; `.gitattributes` enforces LF endings to prevent CRLF corruption in the container.

CI: two GitHub Actions workflows (`pr-docker-build.yml`, `main-docker-build.yml`) build the image using `docker/build-push-action` with `type=gha` layer cache.

## Development branch

Active feature branch: `claude/device-video-cleanup-button-qwl931`. Base: `master`.
