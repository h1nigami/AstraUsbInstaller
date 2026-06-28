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

**Source voice (in priority order):**

1. **Exact copy via voice cloning — XTTS v2** (`_clone_synthesize_to_file`, `_play_clone_voice`). The only way to reproduce the EXACT localized nanosuit voice on arbitrary text: clone it from a real sample. The user drops a 6–15 s clip at `data/nanosuit_ref.wav` (override: `NANOSUIT_REF_WAV`); `TTS.api.TTS("tts_models/multilingual/multi-dataset/xtts_v2").tts_to_file(text=, speaker_wav=ref, language="ru", file_path=)` renders the line in that voice. The reference already carries the suit timbre, so **no nanosuit DSP is applied** to the clone. `coqui-tts` is imported lazily; `COQUI_TOS_AGREED=1` is set to accept the CPML license non-interactively. Model (~1.8 GB) downloads once. If `coqui-tts` or the reference clip are missing, falls through to Silero.
2. **Silero neural TTS** (`_silero_synthesize`, speaker `eugene`, 48 kHz) — a real Russian male voice + the additive nanosuit FX, used when there is no reference clip. `torch` lazy/optional. Model (~60 MB) → `data/silero_v3_1_ru.pt` via `torch.hub.download_url_to_file`, loaded with `torch.package.PackageImporter(...).load_pickle("tts_models", "model")`.
3. **espeak / SAPI + DSP** — final fallbacks (formant-synth robot; no DSP can humanize it).

Greeting fallback order (both OS): `_play_clone_voice` → `_play_silero_fx` → `_play_with_python_fx` (espeak+DSP) → [Windows: `_sapi_to_wav_and_play`] → plain espeak/SAPI.

Pipeline: source WAV/tensor → (`_wav_bytes_to_float()` for espeak) → `_nanosuit_fx()` → `_play_processed_wav()`

**Nanosuit FX design principle:** the source voice stays fully intelligible; character is ADDED in parallel layers. Do NOT replace the voice with a synthetic carrier (vocoder / ring-mod / sawtooth) — that produces robotic noise, not the Crysis nanosuit sound. The game voice is the original voice + 5 additive components: grit/vocal-fry, breathy highs, bass body, presence, and a metallic "detuned-double" robotic echo.

`_nanosuit_fx` order: normalize → `_grit` (parallel soft-clip) → `_detuned_double` (two LFO-modulated short delays = the metallic two-voices signature) → `_comb_fast` → `_shelf`/`_peak` EQ (bass, mud scoop, presence, air) → `_reverb` → normalize → pitch via framerate.

Pitch shift is done by writing the WAV with a lower framerate (`write_sr = int(sr * 2**(_PITCH_CENTS/1200))`), not by resampling the audio array. DSP constants (`_PITCH_CENTS`, `_DBL_*`, `_GRIT_*`, `_COMB_*`, `_REVERB_AMOUNT`, `_BASS_*`, `_MUD_*`, `_PRESENCE_*`, `_AIR_*`) are module-level.

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

The heavy voice stack (`requirements-voice.txt`: torch + coqui-tts, ~3 GB) is gated behind the `INSTALL_VOICE` build arg. The **Dockerfile default is `0`** (lean), but **`docker-compose.yml` defaults it to `1`** so `docker compose up` works with the neural/clone voice out of the box; opt out with `INSTALL_VOICE=0 docker compose build`. The Dockerfile also sets `COQUI_TOS_AGREED=1` (accept XTTS CPML license non-interactively) and `TTS_HOME=/app/data/tts`, and declares `/app/data` a volume, so the large voice models download once into the mounted `./data` and persist across container recreation. Exact-copy clone still needs a `data/nanosuit_ref.wav` reference clip; without it the greeting uses Silero.

CI: two GitHub Actions workflows (`pr-docker-build.yml`, `main-docker-build.yml`) build via `docker/build-push-action` (the Dockerfile directly, not compose) with `type=gha` cache, so they use the Dockerfile's default `INSTALL_VOICE=0` and stay fast and small.

## Voice dependency pins (`requirements-voice.txt`)

XTTS v2 (coqui-tts) is version-sensitive. The file pins a known-good CPU set: `torch`/`torchaudio` from the pytorch CPU index (must match each other), `coqui-tts>=0.25`, and crucially `transformers>=4.57,<5` — transformers 5.x removed `isin_mps_friendly`, which coqui-tts imports, so transformers≥5 breaks XTTS with `cannot import name 'isin_mps_friendly'`. `torchaudio` is a hard runtime dep of coqui-tts. XTTS runs on CPU/CUDA only (no Apple MPS; the code falls back to CPU).

## Development branch

Active feature branch: `claude/voice-deps-fix-dockerfiles`. Base: `master`.
