import os
import json
import platform
import queue
import shutil
import sqlite3
import subprocess
import io
import tempfile
import time
import wave
import threading
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog, filedialog
from datetime import datetime, timedelta

from usb_monitor import monitor_usb, DB_PATH, _init_db, DEST_BASE, get_dest_base, VIDEO_EXTS

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".raw", ".cr2", ".nef"}
DOC_EXTS   = {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".odt", ".ods"}

try:
    from PIL import Image, ImageTk as _ImageTk
    _HAVE_PIL = True
except ImportError:
    _HAVE_PIL = False

try:
    import numpy as np
    from scipy import signal as _scipy_signal
    _HAVE_DSP = True
except Exception:
    _HAVE_DSP = False

POLL_MS = 200
CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "config.json")


def _load_config():
    try:
        with open(CONFIG_PATH) as f:
            return json.load(f)
    except Exception:
        return {}


def _save_config(cfg):
    os.makedirs(os.path.dirname(CONFIG_PATH), exist_ok=True)
    with open(CONFIG_PATH, "w") as f:
        json.dump(cfg, f)


def _get_exit_password():
    cfg = _load_config()
    pw = cfg.get("exit_password")
    if pw:
        return pw
    default = os.environ.get("APP_EXIT_PASSWORD", "exit")
    _save_config({**cfg, "exit_password": default})
    return default


def _set_exit_password(new_pw):
    cfg = _load_config()
    cfg["exit_password"] = new_pw
    _save_config(cfg)


_NANOSUIT_LINES = [
    "С возвращением, Пророк.",
]


def _has_bin(*names):
    for name in names:
        try:
            subprocess.run([name, "--version"], capture_output=True, timeout=3, check=True)
            return name
        except FileNotFoundError:
            continue
        except Exception:
            return name
    return None


# espeak: m7 = deepest robotic male variant; p=5 = maximum base pitch lowering
# Used only as a fallback when Silero (neural voice) is unavailable.
_ESP_ARGS = ["-v", "ru+m7", "-s", "90", "-p", "5", "-a", "200", "-g", "2", "--stdout"]

# ── Silero neural TTS — the human-quality source voice for the nanosuit ─────
# espeak is a formant-synth robot; no DSP can make it sound like a real actor.
# Silero gives a real Russian male voice; the nanosuit FX then go on top.
# torch is heavy and imported lazily — the app still runs (falls back to
# espeak) if torch/model are missing. The model is downloaded once into data/.
_SILERO_MODEL_URL  = "https://models.silero.ai/models/tts/ru/v3_1_ru.pt"
_SILERO_MODEL_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                  "data", "silero_v3_1_ru.pt")
_SILERO_SPEAKER    = "eugene"   # deep, calm male — closest to the suit delivery
_SILERO_SR         = 48000      # Silero supports 8000 / 24000 / 48000
_silero_model      = None       # lazy-loaded singleton

# ── Voice cloning (the EXACT copy) — XTTS v2 ───────────────────────────────
# A TTS voice is a DIFFERENT voice; no effect turns it into the nanosuit actor.
# The only way to reproduce the EXACT localized nanosuit voice on arbitrary
# text is to CLONE it from a real sample of that voice. Drop a 6–15 s clip of
# the nanosuit voice (from the game's Russian localization) at
# data/nanosuit_ref.wav — XTTS v2 then speaks the greeting in that exact voice.
# The reference already carries the suit timbre, so NO extra DSP is applied.
# Heavy (~1.8 GB model, pulls torch) and imported lazily; if coqui-tts or the
# reference clip are missing, the app falls back to Silero → espeak.
_XTTS_MODEL        = "tts_models/multilingual/multi-dataset/xtts_v2"
_CLONE_REF_PATH    = os.environ.get(
    "NANOSUIT_REF_WAV",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "nanosuit_ref.wav"),
)
_CLONE_LANG        = "ru"
_xtts_model        = None       # lazy-loaded singleton

# Debugging knobs:
#   NANOSUIT_VOICE=clone|silero|espeak|sapi|espeak-plain|sapi-plain
#     force a single engine (and see exactly why it fails instead of silently
#     falling back). Default: try all in quality order.
#   NANOSUIT_CLONE_FX=1  layer the metallic nanosuit DSP on top of the clone
#     (default off — the cloned reference already carries the suit timbre).
_VOICE_FORCE = os.environ.get("NANOSUIT_VOICE", "").strip().lower()
_CLONE_FX    = os.environ.get("NANOSUIT_CLONE_FX", "0").strip().lower() not in ("", "0", "false", "no")

# ── Crysis nanosuit DSP ────────────────────────────────────────────────────
# The nanosuit voice is the ORIGINAL human voice kept fully intelligible, with
# character ADDED on top — it is NOT a voice replaced by a synthetic carrier.
# Audio analysis of the game voice identifies five additive components:
#   1. grit / vocal-fry      → parallel soft-clip distortion
#   2. breathy highs         → air shelf on aspirated consonants
#   3. bass / low-frequency  → low shelf for body/weight
#   4. high-frequency        → presence bell boost
#   5. metallic robotic echo → detuned doubling + comb + short reverb
# Confirmed fan recreation: keep the voice, duplicate it with short delays,
# detune, add grit and a metallic echo.

# Detuned doubling — the signature "two voices in the helmet" metallic chorus.
# Short LFO-modulated delays (<30 ms) detune via Doppler → robotic-echo layer.
_DBL_DELAY1_MS    = 13.0
_DBL_DELAY2_MS    = 21.0
_DBL_DEPTH_MS     =  2.0
_DBL_RATE1_HZ     =  0.6
_DBL_RATE2_HZ     =  0.9
_DBL_MIX          =  0.5     # doubles sit UNDER the dry voice

# Grit / vocal-fry — parallel soft-clip distortion
_GRIT_DRIVE       =  6.0
_GRIT_MIX         =  0.22

# Comb filter — light metallic armor resonance
_COMB_DELAY_MS    =  5.0
_COMB_FEEDBACK    =  0.35
_COMB_MIX         =  0.15

# Short metallic reverb — enclosed helmet space
_REVERB_AMOUNT    = 16.0

# EQ — shapes the bass / mud / presence / air components
_BASS_GAIN_DB     = +5.0
_BASS_FREQ_HZ     = 120.0
_MUD_GAIN_DB      = -3.0     # scoop boxiness for clarity
_MUD_FREQ_HZ      = 450.0
_MUD_Q            =  1.2
_PRESENCE_GAIN_DB = +4.0     # high-frequency consonant component
_PRESENCE_FREQ_HZ = 3200.0
_PRESENCE_Q       =  1.0
_AIR_GAIN_DB      = +3.0     # breathy air on top
_AIR_FREQ_HZ      = 7000.0

# Overall weight — slight pitch-down via WAV framerate trick
_PITCH_CENTS      = -90.0


def _frac_delay(x, read_pos):
    """Read x at fractional sample positions (linear interpolation)."""
    n = len(x)
    idx = np.clip(read_pos, 0.0, n - 1.0)
    return np.interp(idx, np.arange(n), x)


def _chorus_voice(x, sr, delay_ms, depth_ms, rate_hz):
    """One detuned copy via LFO-modulated fractional delay (Doppler detune)."""
    n = len(x)
    base  = sr * delay_ms / 1000.0
    depth = sr * depth_ms / 1000.0
    t = np.arange(n)
    lfo = depth * np.sin(2.0 * np.pi * rate_hz * t / sr)
    return _frac_delay(x, t - base - lfo)


def _detuned_double(x, sr):
    """Two modulated copies mixed under the dry voice — the nanosuit's
    'two voices speaking together inside the helmet' metallic signature."""
    a = _chorus_voice(x, sr, _DBL_DELAY1_MS, _DBL_DEPTH_MS, _DBL_RATE1_HZ)
    b = _chorus_voice(x, sr, _DBL_DELAY2_MS, _DBL_DEPTH_MS, _DBL_RATE2_HZ)
    return x + _DBL_MIX * 0.5 * (a + b)


def _grit(x, drive, mix):
    """Parallel soft-clip distortion — the gritty/vocal-fry component.
    Level-matched so the mix ratio is meaningful."""
    wet = np.tanh(drive * x)
    xr = np.sqrt(np.mean(x * x)) + 1e-9
    wr = np.sqrt(np.mean(wet * wet)) + 1e-9
    wet = wet * (xr / wr)
    return (1.0 - mix) * x + mix * wet


def _peak(x, sr, gain_db, freq, q):
    """RBJ peaking (bell) EQ filter."""
    A = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * np.pi * freq / sr
    alpha = np.sin(w0) / (2.0 * q)
    cosw = np.cos(w0)
    b0 = 1.0 + alpha * A
    b1 = -2.0 * cosw
    b2 = 1.0 - alpha * A
    a0 = 1.0 + alpha / A
    a1 = -2.0 * cosw
    a2 = 1.0 - alpha / A
    b = np.array([b0, b1, b2]) / a0
    a = np.array([1.0, a1 / a0, a2 / a0])
    return _scipy_signal.lfilter(b, a, x)


def _comb_fast(x, sr, delay_ms, feedback, mix):
    d = max(1, int(round(sr * delay_ms / 1000.0)))
    a_coef = np.zeros(d + 1); a_coef[0] = 1.0; a_coef[d] = -feedback
    y = _scipy_signal.lfilter([1.0], a_coef, x)
    return (1.0 - mix) * x + mix * y


def _reverb(x, sr, reverberance):
    tail_s = 0.01 + (reverberance / 100.0) * 0.9
    n = int(sr * tail_s)
    t = np.arange(n)
    decay = np.exp(-3.0 * t / n)
    ir = decay * np.random.RandomState(0).randn(n)
    ir[0] = 1.0
    wet = _scipy_signal.fftconvolve(x, ir)[:len(x)]
    mix = reverberance / 100.0 * 0.5
    return (1.0 - mix) * x + mix * (wet / (np.max(np.abs(wet)) + 1e-9))


def _shelf(x, sr, gain_db, freq, kind):
    A = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * np.pi * freq / sr
    cosw = np.cos(w0)
    sinw = np.sin(w0)
    alpha = sinw / 2.0 * np.sqrt((A + 1.0 / A) * (1.0 / 1.0 - 1.0) + 2.0)
    sa = 2.0 * np.sqrt(A) * alpha
    if kind == "low":
        b0 =     A * ((A + 1) - (A - 1) * cosw + sa)
        b1 = 2 * A * ((A - 1) - (A + 1) * cosw)
        b2 =     A * ((A + 1) - (A - 1) * cosw - sa)
        a0 =          (A + 1) + (A - 1) * cosw + sa
        a1 =    -2 * ((A - 1) + (A + 1) * cosw)
        a2 =          (A + 1) + (A - 1) * cosw - sa
    else:
        b0 =     A * ((A + 1) + (A - 1) * cosw + sa)
        b1 = -2 * A * ((A - 1) + (A + 1) * cosw)
        b2 =     A * ((A + 1) + (A - 1) * cosw - sa)
        a0 =          (A + 1) - (A - 1) * cosw + sa
        a1 =     2 * ((A - 1) - (A + 1) * cosw)
        a2 =          (A + 1) - (A - 1) * cosw - sa
    b = np.array([b0, b1, b2]) / a0
    a = np.array([1.0, a1 / a0, a2 / a0])
    return _scipy_signal.lfilter(b, a, x)


def _wav_bytes_to_float(wav_bytes):
    with wave.open(io.BytesIO(wav_bytes), "rb") as w:
        sr = w.getframerate()
        n = w.getnframes()
        ch = w.getnchannels()
        sw = w.getsampwidth()
        raw = w.readframes(n)
    if sw != 2:
        raise ValueError(f"unexpected sample width {sw}")
    audio = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
    if ch > 1:
        audio = audio.reshape(-1, ch).mean(axis=1)
    return audio, sr


def _nanosuit_fx(audio, sr):
    """Apply Crysis nanosuit DSP chain. Returns (processed_float32, write_sr).

    The voice stays fully intelligible; character is ADDED in parallel layers:
    grit (vocal-fry) → detuned doubling (metallic robotic echo) → comb →
    EQ (bass body, scooped mud, presence, air) → short reverb → slight
    pitch-down via WAV framerate trick.
    """
    x = audio.astype(np.float64)
    pk = np.max(np.abs(x)) + 1e-9
    x = x / pk * 0.9                       # normalize input first
    # Grit / vocal-fry component (parallel soft clip)
    x = _grit(x, _GRIT_DRIVE, _GRIT_MIX)
    # Detuned doubling — the metallic "two voices" robotic-echo signature
    x = _detuned_double(x, sr)
    # Light metallic comb resonance
    x = _comb_fast(x, sr, _COMB_DELAY_MS, _COMB_FEEDBACK, _COMB_MIX)
    # EQ — shape bass body, scoop mud, lift presence and air
    x = _shelf(x, sr, gain_db=_BASS_GAIN_DB, freq=_BASS_FREQ_HZ, kind="low")
    x = _peak(x, sr, gain_db=_MUD_GAIN_DB, freq=_MUD_FREQ_HZ, q=_MUD_Q)
    x = _peak(x, sr, gain_db=_PRESENCE_GAIN_DB, freq=_PRESENCE_FREQ_HZ, q=_PRESENCE_Q)
    x = _shelf(x, sr, gain_db=_AIR_GAIN_DB, freq=_AIR_FREQ_HZ, kind="high")
    # Short metallic reverb — enclosed helmet space
    x = _reverb(x, sr, reverberance=_REVERB_AMOUNT)
    # Normalize
    peak = np.max(np.abs(x)) + 1e-9
    x = x / peak * 0.95
    # Slight overall weight via framerate trick
    ratio = 2.0 ** (_PITCH_CENTS / 1200.0)
    write_sr = max(1, int(round(sr * ratio)))
    return x.astype(np.float32), write_sr


def _play_wav_file(path):
    """Play a WAV file at its native sample rate (winsound / aplay)."""
    if platform.system() == "Windows":
        import winsound
        winsound.PlaySound(path, winsound.SND_FILENAME)
    else:
        res = subprocess.run(["aplay", "-q", path], capture_output=True, timeout=30)
        if res.returncode != 0:
            err = res.stderr.decode(errors="replace").strip()
            print(f"[nanosuit] aplay error (rc={res.returncode}): {err}", flush=True)


def _play_processed_wav(audio, sr):
    pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype("<i2")
    fd, path = tempfile.mkstemp(suffix=".wav")
    os.close(fd)
    try:
        with wave.open(path, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(sr)
            w.writeframes(pcm.tobytes())
        _play_wav_file(path)
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def _ensure_xtts_model():
    """Load (import + download + load) the XTTS v2 model singleton on first call.

    Returns the model, or None if coqui-tts is unavailable or the load fails —
    the import error is logged, never swallowed.
    """
    global _xtts_model
    if _xtts_model is not None:
        return _xtts_model
    try:
        # Accept the XTTS (CPML) model license non-interactively for the daemon
        os.environ.setdefault("COQUI_TOS_AGREED", "1")
        from TTS.api import TTS
    except Exception as e:
        print(f"[nanosuit] coqui-tts unavailable ({type(e).__name__}: {e}) — "
              "the exact-copy clone is OFF. Install it with: "
              "pip install -r requirements-voice.txt", flush=True)
        return None
    try:
        try:
            import torch
            device = "cuda" if torch.cuda.is_available() else "cpu"
        except Exception:
            device = "cpu"
        print(f"[nanosuit] loading XTTS v2 clone model on {device} "
              "(first run downloads ~1.8 GB)…", flush=True)
        _xtts_model = TTS(_XTTS_MODEL).to(device)
        return _xtts_model
    except Exception as e:
        print(f"[nanosuit] XTTS load error ({type(e).__name__}: {e})", flush=True)
        return None


def _clone_synthesize_to_file(text, out_path):
    """Clone the reference voice with XTTS v2 and render `text` to out_path.

    Returns True on success, False if coqui-tts or the reference clip are
    missing. This is the EXACT-copy path: the output IS the nanosuit voice
    (cloned from data/nanosuit_ref.wav), so no extra nanosuit DSP is applied.
    """
    if not os.path.exists(_CLONE_REF_PATH):
        print(f"[nanosuit] no voice-clone reference at {_CLONE_REF_PATH} — "
              "drop a 6-15s clip of the nanosuit voice there for an exact copy", flush=True)
        return False
    model = _ensure_xtts_model()
    if model is None:
        return False
    try:
        model.tts_to_file(
            text=text, speaker_wav=_CLONE_REF_PATH,
            language=_CLONE_LANG, file_path=out_path,
        )
        if _CLONE_FX and _HAVE_DSP and os.path.exists(out_path):
            try:
                with open(out_path, "rb") as f:
                    audio, sr = _wav_bytes_to_float(f.read())
                audio, write_sr = _nanosuit_fx(audio, sr)
                pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype("<i2")
                with wave.open(out_path, "wb") as w:
                    w.setnchannels(1); w.setsampwidth(2); w.setframerate(write_sr)
                    w.writeframes(pcm.tobytes())
            except Exception as e:
                print(f"[nanosuit] clone FX skipped ({e})", flush=True)
        return os.path.exists(out_path)
    except Exception as e:
        print(f"[nanosuit] voice-clone error ({type(e).__name__}: {e})", flush=True)
        return False


def _play_clone_voice():
    """Best path: exact voice clone (XTTS v2) of data/nanosuit_ref.wav.
    The reference already carries the nanosuit timbre — play it raw."""
    spoke = False
    for line in _NANOSUIT_LINES:
        fd, path = tempfile.mkstemp(suffix=".wav")
        os.close(fd)
        try:
            if not _clone_synthesize_to_file(line, path):
                return False
            _play_wav_file(path)
            spoke = True
        finally:
            try:
                os.remove(path)
            except OSError:
                pass
    return spoke


def _ensure_silero_model():
    """Load the Silero model singleton (download once to data/, then offline).

    Returns the model, or None if torch/numpy are unavailable or load fails.
    API: torch.package.PackageImporter(...).load_pickle("tts_models", "model").
    """
    global _silero_model
    if _silero_model is not None:
        return _silero_model
    if not _HAVE_DSP:
        return None
    try:
        import torch
    except Exception:
        return None
    try:
        if not os.path.exists(_SILERO_MODEL_PATH):
            os.makedirs(os.path.dirname(_SILERO_MODEL_PATH), exist_ok=True)
            print("[nanosuit] downloading Silero voice model (~60 MB, one time)…", flush=True)
            torch.hub.download_url_to_file(_SILERO_MODEL_URL, _SILERO_MODEL_PATH)
        imp = torch.package.PackageImporter(_SILERO_MODEL_PATH)
        _silero_model = imp.load_pickle("tts_models", "model")
        _silero_model.to(torch.device("cpu"))
        return _silero_model
    except Exception as e:
        print(f"[nanosuit] Silero load error: {e}", flush=True)
        return None


def _silero_synthesize(text):
    """Render text with Silero neural TTS → (float32 mono, sample_rate).
    Returns None if the model/deps are unavailable."""
    model = _ensure_silero_model()
    if model is None:
        return None
    try:
        import torch
        torch.set_num_threads(max(1, os.cpu_count() or 1))
        tensor = model.apply_tts(
            text=text, speaker=_SILERO_SPEAKER, sample_rate=_SILERO_SR,
        )
        audio = np.asarray(tensor.detach().cpu().numpy(), dtype=np.float32)
        return audio, _SILERO_SR
    except Exception as e:
        print(f"[nanosuit] Silero error: {e}", flush=True)
        return None


def _play_silero_fx():
    """Best path: Silero neural voice → nanosuit DSP. False if unavailable."""
    if not _HAVE_DSP:
        return False
    spoke = False
    for line in _NANOSUIT_LINES:
        res = _silero_synthesize(line)
        if not res:
            return False
        audio, sr = res
        audio, write_sr = _nanosuit_fx(audio, sr)
        _play_processed_wav(audio, write_sr)
        spoke = True
    return spoke


def _play_with_python_fx(binary):
    if not _HAVE_DSP:
        return False
    all_ok = True
    for line in _NANOSUIT_LINES:
        try:
            proc = subprocess.run(
                [binary, *_ESP_ARGS, line],
                capture_output=True, timeout=20,
            )
            if proc.returncode != 0 or not proc.stdout:
                raise RuntimeError("espeak produced no audio")
            audio, sr = _wav_bytes_to_float(proc.stdout)
            audio, write_sr = _nanosuit_fx(audio, sr)
            _play_processed_wav(audio, write_sr)
        except Exception as e:
            print(f"[nanosuit] DSP error: {e}", flush=True)
            all_ok = False
    return all_ok


def _play_plain_espeak(binary):
    for line in _NANOSUIT_LINES:
        try:
            subprocess.run(
                [binary, "-v", "ru+m7", "-s", "90", "-p", "5", "-a", "200", line],
                capture_output=True, timeout=10,
            )
        except Exception as e:
            print(f"[nanosuit] speech error: {e}", flush=True)


def _sapi_to_wav_and_play(text):
    """Render text via Windows SAPI to a WAV file, then apply nanosuit DSP."""
    if not _HAVE_DSP:
        return False
    fd, wav_path = tempfile.mkstemp(suffix=".wav")
    os.close(fd)
    try:
        # Escape single quotes for PowerShell string
        safe_text = text.replace("'", "''")
        ps_script = (
            "Add-Type -AssemblyName System.Speech; "
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
            # Prefer Russian male → any Russian → any male → default
            "$ru_m = $s.GetInstalledVoices() | Where-Object { "
            "  $_.VoiceInfo.Culture.Name -like 'ru*' -and "
            "  $_.VoiceInfo.Gender -eq 'Male' }; "
            "$ru_any = $s.GetInstalledVoices() | Where-Object { "
            "  $_.VoiceInfo.Culture.Name -like 'ru*' }; "
            "$male = $s.GetInstalledVoices() | Where-Object { "
            "  $_.VoiceInfo.Gender -eq 'Male' }; "
            "if ($ru_m)    { $s.SelectVoice($ru_m[0].VoiceInfo.Name) } "
            "elseif ($ru_any) { $s.SelectVoice($ru_any[0].VoiceInfo.Name) } "
            "elseif ($male)   { $s.SelectVoice($male[0].VoiceInfo.Name) }; "
            "$s.Rate = -3; "
            f"$s.SetOutputToWaveFile('{wav_path}'); "
            f"$s.Speak('{safe_text}'); "
            "$s.Dispose()"
        )
        res = subprocess.run(
            ["powershell", "-NoProfile", "-WindowStyle", "Hidden", "-Command", ps_script],
            timeout=30, capture_output=True,
        )
        if res.returncode != 0 or not os.path.exists(wav_path):
            return False
        with open(wav_path, "rb") as f:
            wav_bytes = f.read()
        audio, sr = _wav_bytes_to_float(wav_bytes)
        audio, write_sr = _nanosuit_fx(audio, sr)
        _play_processed_wav(audio, write_sr)
        return True
    except Exception as e:
        print(f"[nanosuit] SAPI DSP error: {e}", flush=True)
        return False
    finally:
        try:
            os.remove(wav_path)
        except OSError:
            pass


def _play_plain_sapi():
    """Last-resort Windows SAPI, no DSP. Returns True if it ran."""
    text = " ".join(_NANOSUIT_LINES)
    ps_script = (
        "Add-Type -AssemblyName System.Speech; "
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
        "$ru = $s.GetInstalledVoices() | "
        "Where-Object { $_.VoiceInfo.Culture.Name -like 'ru*' }; "
        "if ($ru) { $s.SelectVoice($ru[0].VoiceInfo.Name) }; "
        f"$s.Rate = -4; $s.Speak('{text}')"
    )
    try:
        subprocess.run(
            ["powershell", "-NoProfile", "-WindowStyle", "Hidden", "-Command", ps_script],
            timeout=30, capture_output=True,
        )
        return True
    except Exception as e:
        print(f"[nanosuit] Windows TTS error: {e}", flush=True)
        return False


def _voice_engines():
    """Ordered (key, label, callable→bool) greeting engines, best first."""
    engines = [
        ("clone",  "XTTS v2 exact-copy clone",    _play_clone_voice),
        ("silero", "Silero neural + nanosuit FX", _play_silero_fx),
    ]
    binary = _has_bin("espeak-ng", "espeak")
    if binary:
        engines.append(("espeak", "espeak + nanosuit FX",
                        lambda: _play_with_python_fx(binary)))
    if platform.system() == "Windows":
        engines.append(("sapi", "Windows SAPI + nanosuit FX",
                        lambda: _sapi_to_wav_and_play(" ".join(_NANOSUIT_LINES))))
    if binary:
        engines.append(("espeak-plain", "espeak (no FX)",
                        lambda: (_play_plain_espeak(binary) or True)))
    if platform.system() == "Windows":
        engines.append(("sapi-plain", "Windows SAPI (no FX)", _play_plain_sapi))
    return engines


def _nanosuit_greeting():
    """Try voice engines in quality order, logging which one actually spoke.

    NANOSUIT_VOICE=<key> forces a single engine so failures are visible
    instead of silently falling through to the next one. Returns the key
    of the engine that produced audio, or None.
    """
    engines = _voice_engines()
    if _VOICE_FORCE:
        forced = [e for e in engines if e[0] == _VOICE_FORCE]
        if forced:
            print(f"[nanosuit] NANOSUIT_VOICE={_VOICE_FORCE} — forcing this engine only", flush=True)
            engines = forced
        else:
            have = ", ".join(e[0] for e in engines)
            print(f"[nanosuit] NANOSUIT_VOICE={_VOICE_FORCE!r} unknown "
                  f"(available: {have}) — using default order", flush=True)
    for key, label, fn in engines:
        try:
            if fn():
                print(f"[nanosuit] ✓ voice engine used: {label}", flush=True)
                return key
        except Exception as e:
            print(f"[nanosuit] ✗ engine '{key}' failed ({type(e).__name__}: {e})", flush=True)
    print("[nanosuit] no voice engine produced audio — "
          "for the exact/neural voice: pip install -r requirements-voice.txt", flush=True)
    return None


def _preload_voice_model():
    """Download/load the TTS model BEFORE the GUI starts, so the greeting is
    ready the moment the interface appears (and the big first-run download
    happens up front, not behind the locked kiosk UI).

    Warms the engine that will actually be used: the XTTS clone when a
    reference clip + coqui-tts are available, otherwise Silero. Returns that
    engine key, or None when no neural voice deps are present (the greeting
    then falls back to espeak as before). Honors NANOSUIT_VOICE.
    """
    forced = _VOICE_FORCE
    if forced in ("espeak", "espeak-plain", "sapi", "sapi-plain"):
        return None  # nothing to preload for the espeak/SAPI engines
    if forced == "silero":
        return "silero" if _ensure_silero_model() is not None else None
    if forced == "clone":
        return "clone" if _ensure_xtts_model() is not None else None
    # Auto order: exact clone if a reference exists, else Silero.
    if os.path.exists(_CLONE_REF_PATH) and _ensure_xtts_model() is not None:
        return "clone"
    if _ensure_silero_model() is not None:
        return "silero"
    return None


def _preload_with_splash():
    """Load the TTS model before the main UI, showing a small splash meanwhile.

    Runs the (possibly long) model load on a worker thread so the splash stays
    responsive. Falls back to a plain blocking preload if no display is usable.
    """
    try:
        splash = tk.Tk()
    except Exception:
        _preload_voice_model()
        return
    try:
        splash.overrideredirect(True)
        splash.configure(bg="#0f172a")
        sw, sh = splash.winfo_screenwidth(), splash.winfo_screenheight()
        w, h = max(380, sw // 3), max(160, sh // 6)
        splash.geometry(f"{w}x{h}+{(sw - w) // 2}+{(sh - h) // 2}")
        tk.Label(splash, text="Инициализация голосового модуля…",
                 fg="#f1f5f9", bg="#0f172a",
                 font=("Segoe UI", 16, "bold")).pack(expand=True, pady=(24, 4))
        tk.Label(splash,
                 text="Загрузка модели TTS — при первом запуске это может занять время",
                 fg="#94a3b8", bg="#0f172a", font=("Segoe UI", 10)).pack(pady=(0, 24))
        state = {"done": False}

        def work():
            try:
                _preload_voice_model()
            finally:
                state["done"] = True

        threading.Thread(target=work, daemon=True).start()

        def check():
            if state["done"]:
                splash.destroy()
            else:
                splash.after(150, check)

        splash.after(150, check)
        splash.mainloop()
    finally:
        try:
            splash.destroy()
        except Exception:
            pass


class App:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("BestElectronics USB Backup Manager")
        self.root.attributes("-fullscreen", True)
        self.root.bind("<Escape>", lambda e: None)
        self.root.configure(bg="#0f172a")

        self.C = {
            "bg_app":      "#0f172a",
            "bg_panel":    "#1e293b",
            "bg_surface":  "#334155",
            "fg_main":     "#f1f5f9",
            "fg_muted":    "#94a3b8",
            "accent":      "#2563eb",
            "accent_warn": "#d97706",
            "accent_ok":   "#16a34a",
            "border":      "#475569",
            "brand":       "#38bdf8",
        }
        self.tabs_unlocked = False
        self._unlock_in_progress = False
        self._last_tab = 0
        self.public_tab_index = 0

        cfg = _load_config()
        self._lock_timeout = int(cfg.get("lock_timeout_minutes", 10)) * 60
        self._last_activity = time.time()

        self.mon_status = tk.StringVar(value="Мониторинг: запуск...")

        self.progress_queue = queue.Queue()
        self.stop_event = threading.Event()
        self.monitor_thread = None
        self.workers_data = {}
        self._done_times = {}
        self.port_assignment = {}
        self._search_results = []
        self._search_gen = 0

        conn = _init_db()
        conn.close()

        threading.Thread(target=_nanosuit_greeting, daemon=True).start()
        self._build_ui()
        self.root.bind_all("<Button>", self._touch_activity, add=True)
        self.root.bind_all("<Key>", self._touch_activity, add=True)
        self._poll_queue()
        self._start_monitor()
        self._check_lock_timeout()

    def _setup_styles(self):
        C = self.C
        s = ttk.Style(self.root)
        s.theme_use("clam")
        s.configure("TNotebook", background=C["bg_app"], borderwidth=0)
        s.configure("TNotebook.Tab", font=("Segoe UI", 13, "bold"), padding=[22, 10],
                    background=C["bg_panel"], foreground=C["fg_muted"], borderwidth=0)
        s.map("TNotebook.Tab",
              background=[("selected", C["accent"])],
              foreground=[("selected", "#ffffff")])
        s.configure("TFrame", background=C["bg_app"])
        s.configure("TLabel", background=C["bg_app"], foreground=C["fg_main"], font=("Segoe UI", 11))
        s.configure("TLabelframe", background=C["bg_panel"], foreground=C["brand"],
                    bordercolor=C["border"], font=("Segoe UI", 12, "bold"))
        s.configure("TLabelframe.Label", background=C["bg_panel"], foreground=C["brand"])
        s.configure("TButton", font=("Segoe UI", 11, "bold"), padding=[14, 8],
                    background=C["accent"], foreground="#ffffff", borderwidth=0)
        s.map("TButton", background=[("active", "#1d4ed8"), ("pressed", "#1e40af")])
        s.configure("Danger.TButton", font=("Segoe UI", 11, "bold"), padding=[14, 8],
                    background="#dc2626", foreground="#ffffff", borderwidth=0)
        s.map("Danger.TButton", background=[("active", "#b91c1c"), ("pressed", "#991b1b")])
        s.configure("Treeview", font=("Segoe UI", 11), rowheight=30,
                    background=C["bg_surface"], fieldbackground=C["bg_surface"],
                    foreground=C["fg_main"], borderwidth=0)
        s.configure("Treeview.Heading", font=("Segoe UI", 11, "bold"),
                    background=C["bg_panel"], foreground=C["brand"], padding=[6, 8])
        s.map("Treeview", background=[("selected", C["accent"])], foreground=[("selected", "#ffffff")])
        s.configure("TEntry", fieldbackground=C["bg_surface"], foreground=C["fg_main"],
                    bordercolor=C["border"], insertcolor=C["fg_main"], padding=4)
        s.configure("TCombobox", fieldbackground=C["bg_surface"], background=C["bg_surface"],
                    foreground=C["fg_main"], arrowcolor=C["fg_main"], padding=4)

    def _build_header(self):
        C = self.C
        hdr = tk.Frame(self.root, bg=C["bg_panel"], height=80)
        hdr.pack(fill="x", side="top")
        hdr.pack_propagate(False)

        _logo_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "LOGO-1.png")
        try:
            self._logo_img = tk.PhotoImage(file=_logo_path).subsample(3)
            logo = tk.Label(hdr, image=self._logo_img, bg=C["bg_app"])
        except Exception:
            logo = tk.Label(hdr, text="[LOGO]", width=7, height=3,
                            font=("Segoe UI", 12, "bold"),
                            fg=C["brand"], bg=C["bg_app"],
                            relief="solid", bd=1)
        logo.pack(side="left", padx=16, pady=10)

        box = tk.Frame(hdr, bg=C["bg_panel"])
        box.pack(side="left", padx=10)
        tk.Label(box, text="BestElectronics", font=("Segoe UI", 20, "bold"),
                 fg=C["brand"], bg=C["bg_panel"]).pack(anchor="w")
        tk.Label(box, text="USB Backup Manager", font=("Segoe UI", 12),
                 fg=C["fg_muted"], bg=C["bg_panel"]).pack(anchor="w")

        tk.Frame(self.root, bg=C["brand"], height=2).pack(fill="x")

    def _build_statusbar(self):
        C = self.C
        bar = tk.Frame(self.root, bg=C["bg_panel"], height=28)
        bar.pack(fill="x", side="bottom")
        bar.pack_propagate(False)
        tk.Label(bar, text="© BestElectronics", font=("Segoe UI", 10),
                 fg=C["fg_muted"], bg=C["bg_panel"]).pack(side="left", padx=12)
        tk.Label(bar, textvariable=self.mon_status, font=("Segoe UI", 10),
                 fg=C["brand"], bg=C["bg_panel"]).pack(side="right", padx=12)

    def _build_ui(self):
        self._setup_styles()
        self._build_header()
        self._build_statusbar()
        self.nb = ttk.Notebook(self.root)
        self.nb.pack(fill="both", expand=True)
        self._build_workers_tab(self.nb)
        self._build_search_tab(self.nb)
        self._build_devices_tab(self.nb)
        self._build_settings_tab(self.nb)
        self.nb.bind("<<NotebookTabChanged>>", self._on_tab_changed)

    def _prompt_unlock(self):
        result = {"ok": False}
        C = self.C
        dlg = tk.Toplevel(self.root)
        dlg.title("Доступ к разделу")
        dlg.configure(bg=C["bg_panel"])
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        w, h = sw // 2, sh // 2
        x, y = (sw - w) // 2, (sh - h) // 2
        dlg.geometry(f"{w}x{h}+{x}+{y}")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.wait_visibility(dlg)
        dlg.grab_set()

        tk.Label(dlg, text="Доступ к разделу защищён.",
                 font=("Segoe UI", 20, "bold"),
                 fg=C["fg_main"], bg=C["bg_panel"]).pack(pady=(h // 6, 8))
        tk.Label(dlg, text="Введите пароль:",
                 font=("Segoe UI", 14),
                 fg=C["fg_muted"], bg=C["bg_panel"]).pack()
        pw_var = tk.StringVar()
        pw_entry = ttk.Entry(dlg, textvariable=pw_var, show="*",
                             width=w // 14, font=("Segoe UI", 14))
        pw_entry.pack(pady=16, ipadx=8, ipady=6)
        pw_entry.focus_set()
        err_var = tk.StringVar()
        tk.Label(dlg, textvariable=err_var, font=("Segoe UI", 12),
                 fg="#f87171", bg=C["bg_panel"]).pack()

        def confirm():
            if pw_var.get().strip() == _get_exit_password():
                result["ok"] = True
                dlg.destroy()
            else:
                err_var.set("Неверный пароль")

        ttk.Button(dlg, text="Войти", command=confirm).pack(pady=20)
        pw_entry.bind("<Return>", lambda e: confirm())
        dlg.bind("<Escape>", lambda e: dlg.destroy())
        dlg.protocol("WM_DELETE_WINDOW", dlg.destroy)
        dlg.wait_window()
        return result["ok"]

    def _on_tab_changed(self, _event):
        if self._unlock_in_progress:
            return
        idx = self.nb.index(self.nb.select())
        if idx == self.public_tab_index or self.tabs_unlocked:
            self._last_activity = time.time()
            self._last_tab = idx
            return
        self._unlock_in_progress = True
        try:
            self.nb.select(self._last_tab)
            if self._prompt_unlock():
                self.tabs_unlocked = True
                self._last_activity = time.time()
                self.nb.select(idx)
                self._last_tab = idx
        finally:
            self._unlock_in_progress = False

    def _touch_activity(self, _event=None):
        self._last_activity = time.time()

    def _check_lock_timeout(self):
        try:
            if self.tabs_unlocked and self._lock_timeout > 0:
                if time.time() - self._last_activity > self._lock_timeout:
                    self.tabs_unlocked = False
                    if self.nb.index(self.nb.select()) != self.public_tab_index:
                        self.nb.select(self.public_tab_index)
                        self._last_tab = self.public_tab_index
            self.root.after(30_000, self._check_lock_timeout)
        except tk.TclError:
            pass

    def _build_search_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Поиск")

        top = ttk.Frame(f)
        top.pack(fill="x", padx=5, pady=5)

        days = [f"{d:02d}" for d in range(1, 32)]
        months = [f"{m:02d}" for m in range(1, 13)]
        years = [str(y) for y in range(2020, 2036)]
        hours = [f"{h:02d}" for h in range(0, 24)]
        minutes = [f"{m:02d}" for m in range(0, 60)]

        # Row 0: date range
        ttk.Label(top, text="От:").grid(row=0, column=0, padx=2, sticky="w")
        dt_from_frame = ttk.Frame(top)
        dt_from_frame.grid(row=0, column=1, padx=2, sticky="w")
        self._from_day = ttk.Combobox(dt_from_frame, values=days, width=3, state="readonly")
        self._from_day.pack(side="left")
        ttk.Label(dt_from_frame, text=".").pack(side="left")
        self._from_mon = ttk.Combobox(dt_from_frame, values=months, width=3, state="readonly")
        self._from_mon.pack(side="left")
        ttk.Label(dt_from_frame, text=".").pack(side="left")
        self._from_year = ttk.Combobox(dt_from_frame, values=years, width=5, state="readonly")
        self._from_year.pack(side="left")
        ttk.Label(dt_from_frame, text="  ").pack(side="left")
        self._from_hour = ttk.Combobox(dt_from_frame, values=hours, width=3, state="readonly")
        self._from_hour.pack(side="left")
        ttk.Label(dt_from_frame, text=":").pack(side="left")
        self._from_min = ttk.Combobox(dt_from_frame, values=minutes, width=3, state="readonly")
        self._from_min.pack(side="left")

        ttk.Label(top, text="До:").grid(row=0, column=2, padx=(10, 2), sticky="w")
        dt_to_frame = ttk.Frame(top)
        dt_to_frame.grid(row=0, column=3, padx=2, sticky="w")
        self._to_day = ttk.Combobox(dt_to_frame, values=days, width=3, state="readonly")
        self._to_day.pack(side="left")
        ttk.Label(dt_to_frame, text=".").pack(side="left")
        self._to_mon = ttk.Combobox(dt_to_frame, values=months, width=3, state="readonly")
        self._to_mon.pack(side="left")
        ttk.Label(dt_to_frame, text=".").pack(side="left")
        self._to_year = ttk.Combobox(dt_to_frame, values=years, width=5, state="readonly")
        self._to_year.pack(side="left")
        ttk.Label(dt_to_frame, text="  ").pack(side="left")
        self._to_hour = ttk.Combobox(dt_to_frame, values=hours, width=3, state="readonly")
        self._to_hour.pack(side="left")
        ttk.Label(dt_to_frame, text=":").pack(side="left")
        self._to_min = ttk.Combobox(dt_to_frame, values=minutes, width=3, state="readonly")
        self._to_min.pack(side="left")

        # Row 1: device / person / file type / filename
        ttk.Label(top, text="Устройство:").grid(row=1, column=0, padx=2, pady=(5, 0), sticky="w")
        self.search_device = ttk.Combobox(top, width=14, state="readonly")
        self.search_device.grid(row=1, column=1, padx=2, pady=(5, 0), sticky="w")

        ttk.Label(top, text="Человек:").grid(row=1, column=2, padx=(10, 2), pady=(5, 0), sticky="w")
        self.search_person = ttk.Combobox(top, width=14, state="readonly")
        self.search_person.grid(row=1, column=3, padx=2, pady=(5, 0), sticky="w")

        # Row 2: file type / filename / buttons
        ttk.Label(top, text="Тип файла:").grid(row=2, column=0, padx=2, pady=(5, 0), sticky="w")
        self.search_filetype = ttk.Combobox(
            top, width=14, state="readonly",
            values=["Все", "Фото", "Видео", "Документы"],
        )
        self.search_filetype.set("Все")
        self.search_filetype.grid(row=2, column=1, padx=2, pady=(5, 0), sticky="w")

        ttk.Label(top, text="Имя файла:").grid(row=2, column=2, padx=(10, 2), pady=(5, 0), sticky="w")
        self.search_filename_var = tk.StringVar()
        ttk.Entry(top, textvariable=self.search_filename_var, width=20).grid(
            row=2, column=3, padx=2, pady=(5, 0), sticky="w")

        btn_frame = ttk.Frame(top)
        btn_frame.grid(row=2, column=4, padx=8, pady=(5, 0), sticky="w")
        ttk.Button(btn_frame, text="Найти", command=self._do_search).pack(side="left", padx=2)
        ttk.Button(btn_frame, text="Сброс", command=self._reset_search).pack(side="left", padx=2)
        self._export_btn = ttk.Button(btn_frame, text="Выгрузить (0)", command=self._export_found_files, state="disabled")
        self._export_btn.pack(side="left", padx=2)

        # Status label for search progress
        self._search_status_var = tk.StringVar(value="")
        ttk.Label(f, textvariable=self._search_status_var, foreground=self.C["brand"],
                  font=("Segoe UI", 10)).pack(anchor="w", padx=8)

        cols = ("datetime", "device", "person", "filename", "ext", "size", "path")
        self.search_tree = ttk.Treeview(f, columns=cols, show="headings", height=16)
        headings = {
            "datetime": "Дата изм.", "device": "Устройство", "person": "Человек",
            "filename": "Файл", "ext": "Тип", "size": "Размер", "path": "Путь",
        }
        col_widths = {"datetime": 140, "device": 90, "person": 130,
                      "filename": 200, "ext": 60, "size": 80, "path": 320}
        for c in cols:
            self.search_tree.heading(c, text=headings[c])
            self.search_tree.column(c, width=col_widths[c])
        vsb = ttk.Scrollbar(f, orient="vertical", command=self.search_tree.yview)
        self.search_tree.configure(yscrollcommand=vsb.set)
        self.search_tree.pack(fill="both", expand=True, padx=5, pady=(0, 5), side="left")
        vsb.pack(fill="y", pady=(0, 5), side="right")

        self.search_tree.bind("<Double-1>", self._on_search_dblclick)

        self._reset_search_dates()
        self._refresh_search_filters()

    def _reset_search_dates(self):
        now = datetime.now()
        dt_from = now - timedelta(hours=24)
        self._from_day.set(f"{dt_from.day:02d}")
        self._from_mon.set(f"{dt_from.month:02d}")
        self._from_year.set(str(dt_from.year))
        self._from_hour.set(f"{dt_from.hour:02d}")
        self._from_min.set(f"{dt_from.minute:02d}")
        self._to_day.set(f"{now.day:02d}")
        self._to_mon.set(f"{now.month:02d}")
        self._to_year.set(str(now.year))
        self._to_hour.set(f"{now.hour:02d}")
        self._to_min.set(f"{now.minute:02d}")

    def _get_dt_from(self):
        try:
            return f"{self._from_year.get()}-{self._from_mon.get()}-{self._from_day.get()} {self._from_hour.get()}:{self._from_min.get()}:00"
        except Exception:
            return ""

    def _get_dt_to(self):
        try:
            return f"{self._to_year.get()}-{self._to_mon.get()}-{self._to_day.get()} {self._to_hour.get()}:{self._to_min.get()}:59"
        except Exception:
            return ""

    def _build_devices_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Устройства")

        cols = ("id", "serial", "label", "person", "first_seen", "last_seen")
        self.dev_tree = ttk.Treeview(f, columns=cols, show="headings", height=16)
        headings = {"id": "ID", "serial": "Серийный", "label": "Метка",
                    "person": "Человек", "first_seen": "Впервые", "last_seen": "Последний раз"}
        dev_col_widths = {"id": 50, "serial": 200, "label": 150, "person": 150,
                          "first_seen": 160, "last_seen": 160}
        for c in cols:
            self.dev_tree.heading(c, text=headings[c])
            self.dev_tree.column(c, width=dev_col_widths[c])
        self.dev_tree.pack(fill="both", expand=True, padx=5, pady=5)

        edit_frame = ttk.Frame(f)
        edit_frame.pack(fill="x", padx=5, pady=(0, 5))
        ttk.Label(edit_frame, text="Device ID:").pack(side="left", padx=2)
        self.edit_dev_id = ttk.Entry(edit_frame, width=6)
        self.edit_dev_id.pack(side="left", padx=2)
        ttk.Label(edit_frame, text="Человек:").pack(side="left", padx=2)
        self.edit_person = ttk.Entry(edit_frame, width=20)
        self.edit_person.pack(side="left", padx=2)
        ttk.Button(edit_frame, text="Назначить", command=self._assign_person).pack(side="left", padx=4)
        ttk.Button(edit_frame, text="Очистить видео", command=self._clean_device_videos, style="Danger.TButton").pack(side="left", padx=4)
        ttk.Button(edit_frame, text="Обновить список", command=self._refresh_devices).pack(side="right", padx=4)

        self.dev_tree.bind("<<TreeviewSelect>>", self._on_device_select)
        self._refresh_devices()

    def _build_workers_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Загрузка")

        self.ports = []

        grid = ttk.Frame(f)
        grid.pack(fill="both", expand=True, padx=10, pady=10)

        rows, cols = 3, 4
        for i in range(rows * cols):
            r, c = divmod(i, cols)
            cell = tk.Frame(grid, bg=self.C["border"], bd=0)
            cell.grid(row=r, column=c, padx=10, pady=10, sticky="nsew")
            grid.columnconfigure(c, weight=1, uniform="port")
            grid.rowconfigure(r, weight=1, uniform="port")

            inner = tk.Frame(cell, bg=self.C["bg_panel"], bd=0)
            inner.pack(fill="both", expand=True, padx=1, pady=1)

            preview = tk.Label(inner, text="Простой", font=("Segoe UI", 18, "bold"),
                               fg="white", bg=self.C["accent"])
            preview.pack(fill="both", expand=True)

            status = tk.Label(inner, text="Нет передачи данных", font=("Segoe UI", 11),
                              fg=self.C["fg_main"], bg=self.C["bg_panel"])
            status.pack(fill="x", ipady=8)

            self.ports.append({"frame": cell, "preview": preview, "status": status, "device_id": None})

        for i in range(10, rows * cols):
            self.ports[i]["frame"].grid_remove()

    def _build_settings_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Настройки")

        folder_frame = ttk.LabelFrame(f, text="Папка для резервных копий", padding=10)
        folder_frame.pack(fill="x", padx=10, pady=10)

        ttk.Label(folder_frame, text="Текущая папка:").pack(anchor="w")
        self.backup_dest_var = tk.StringVar(value=get_dest_base())
        ttk.Label(folder_frame, textvariable=self.backup_dest_var,
                  foreground=self.C["brand"], wraplength=900,
                  font=("Segoe UI", 11)).pack(anchor="w", pady=(2, 8))
        ttk.Button(folder_frame, text="Выбрать папку", command=self._change_backup_dest).pack(anchor="w")

        frame = ttk.LabelFrame(f, text="Защита выхода", padding=10)
        frame.pack(fill="x", padx=10, pady=10)

        ttk.Label(frame, text="Выход из программы защищён паролем.").pack(anchor="w")
        self.pw_status = tk.StringVar()
        ttk.Label(frame, textvariable=self.pw_status, foreground="gray").pack(anchor="w", pady=(0, 10))

        ttk.Button(frame, text="Сменить пароль", command=self._change_password).pack(anchor="w")

        self._refresh_pw_status()

        lock_frame = ttk.LabelFrame(f, text="Автоблокировка разделов", padding=10)
        lock_frame.pack(fill="x", padx=10, pady=(0, 10))

        ttk.Label(lock_frame, text="Время до блокировки (минут, 0 — отключено):").pack(anchor="w")
        self._timeout_var = tk.StringVar(value=str(int(self._lock_timeout / 60)))
        timeout_row = ttk.Frame(lock_frame)
        timeout_row.pack(anchor="w", pady=(4, 0))
        ttk.Entry(timeout_row, textvariable=self._timeout_var, width=6).pack(side="left")
        ttk.Button(timeout_row, text="Сохранить", command=self._save_lock_timeout).pack(side="left", padx=6)
        self._timeout_status = tk.StringVar()
        ttk.Label(lock_frame, textvariable=self._timeout_status, foreground="gray").pack(anchor="w", pady=(4, 0))
        self._refresh_timeout_status()

        about = ttk.LabelFrame(f, text="О программе", padding=16)
        about.pack(fill="x", padx=10, pady=(0, 10))
        ttk.Label(about, text="BestElectronics USB Backup Manager").pack(anchor="w")
        ttk.Label(about, text="Автоматическое резервное копирование USB-устройств.", foreground=self.C["fg_muted"]).pack(anchor="w")

    def _refresh_timeout_status(self):
        m = int(self._lock_timeout / 60)
        if m == 0:
            self._timeout_status.set("Автоблокировка отключена")
        else:
            self._timeout_status.set(f"Блокировка через {m} мин. бездействия")

    def _save_lock_timeout(self):
        try:
            minutes = int(self._timeout_var.get().strip())
            minutes = max(0, minutes)
        except ValueError:
            messagebox.showwarning("Ошибка", "Введите целое число минут")
            return
        self._lock_timeout = minutes * 60
        cfg = _load_config()
        cfg["lock_timeout_minutes"] = minutes
        _save_config(cfg)
        self._refresh_timeout_status()

    def _change_backup_dest(self):
        current = get_dest_base()
        new_path = filedialog.askdirectory(
            title="Выберите папку для резервных копий",
            initialdir=current if os.path.isdir(current) else os.path.expanduser("~"),
            parent=self.root,
        )
        if not new_path:
            return
        cfg = _load_config()
        cfg["backup_dest"] = new_path
        _save_config(cfg)
        self.backup_dest_var.set(new_path)
        messagebox.showinfo("Готово", f"Папка для резервных копий изменена:\n{new_path}")

    def _refresh_pw_status(self):
        pw = _get_exit_password()
        self.pw_status.set(f"Текущий пароль: {'*' * len(pw)} (длина {len(pw)} симв.)")

    def _change_password(self):
        old = _get_exit_password()
        dlg = tk.Toplevel(self.root)
        dlg.title("Смена пароля")
        dlg.geometry("350x200")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.wait_visibility(dlg)
        dlg.grab_set()

        ttk.Label(dlg, text="Старый пароль:").pack(pady=(10, 0))
        old_var = tk.StringVar()
        old_entry = ttk.Entry(dlg, textvariable=old_var, show="*", width=30)
        old_entry.pack(pady=5)
        old_entry.focus()

        ttk.Label(dlg, text="Новый пароль:").pack(pady=(5, 0))
        new_var = tk.StringVar()
        new_entry = ttk.Entry(dlg, textvariable=new_var, show="*", width=30)
        new_entry.pack(pady=5)

        err_var = tk.StringVar()
        ttk.Label(dlg, textvariable=err_var, foreground="red").pack()

        def submit():
            if old_var.get().strip() != old:
                err_var.set("Неверный старый пароль")
                return
            if not new_var.get().strip():
                err_var.set("Новый пароль не может быть пустым")
                return
            _set_exit_password(new_var.get().strip())
            self._refresh_pw_status()
            dlg.destroy()
            messagebox.showinfo("Готово", "Пароль изменён")

        ttk.Button(dlg, text="Сохранить", command=submit).pack(pady=10)

    def _on_close(self):
        C = self.C
        dlg = tk.Toplevel(self.root)
        dlg.title("Подтверждение выхода")
        dlg.configure(bg=C["bg_panel"])
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        w, h = sw // 2, sh // 2
        x, y = (sw - w) // 2, (sh - h) // 2
        dlg.geometry(f"{w}x{h}+{x}+{y}")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.wait_visibility(dlg)
        dlg.grab_set()

        tk.Label(dlg, text="Выход из приложения",
                 font=("Segoe UI", 20, "bold"),
                 fg=C["fg_main"], bg=C["bg_panel"]).pack(pady=(h // 6, 8))
        tk.Label(dlg, text="Введите пароль для выхода:",
                 font=("Segoe UI", 14),
                 fg=C["fg_muted"], bg=C["bg_panel"]).pack()

        pw_var = tk.StringVar()
        pw_entry = ttk.Entry(dlg, textvariable=pw_var, show="*",
                             width=w // 14, font=("Segoe UI", 14))
        pw_entry.pack(pady=16, ipadx=8, ipady=6)
        pw_entry.focus_set()

        err_var = tk.StringVar()
        tk.Label(dlg, textvariable=err_var, font=("Segoe UI", 12),
                 fg="#f87171", bg=C["bg_panel"]).pack()

        def confirm():
            pw_in = pw_var.get().strip()
            expected = _get_exit_password()
            if pw_in == expected:
                dlg.destroy()
                self.stop_event.set()
                self.root.destroy()
            else:
                err_var.set("Неверный пароль")

        ttk.Button(dlg, text="Выйти", style="Danger.TButton", command=confirm).pack(pady=20)
        pw_entry.bind("<Return>", lambda e: confirm())
        dlg.bind("<Return>", lambda e: confirm())
        dlg.bind("<Escape>", lambda e: dlg.destroy())
        dlg.protocol("WM_DELETE_WINDOW", dlg.destroy)

    def _get_db(self):
        return sqlite3.connect(DB_PATH)

    def _refresh_search_filters(self):
        conn = self._get_db()
        try:
            devices = conn.execute("SELECT DISTINCT label FROM devices WHERE label != '' ORDER BY label").fetchall()
            people = conn.execute("SELECT DISTINCT person FROM devices WHERE person != '' ORDER BY person").fetchall()
            dev_list = [""] + [r[0] for r in devices]
            per_list = [""] + [r[0] for r in people]
            self.search_device["values"] = dev_list
            self.search_person["values"] = per_list
        finally:
            conn.close()

    def _do_search(self):
        for row in self.search_tree.get_children():
            self.search_tree.delete(row)
        self._search_results = []
        self._export_btn.configure(state="disabled", text="Выгрузить (0)")
        self._search_status_var.set("Поиск...")

        self._search_gen += 1
        gen = self._search_gen
        params_snapshot = {
            "dt_from": self._get_dt_from(),
            "dt_to": self._get_dt_to(),
            "dev": self.search_device.get(),
            "person": self.search_person.get(),
            "filetype": self.search_filetype.get(),
            "filename": self.search_filename_var.get().strip().lower(),
        }
        threading.Thread(target=self._search_worker, args=(params_snapshot, gen), daemon=True).start()

    def _search_worker(self, p, gen):
        results = []
        try:
            conn = self._get_db()
            try:
                sql = """
                    SELECT d.id, d.person, b.dest_path
                    FROM backups b
                    JOIN devices d ON d.id = b.device_id
                    WHERE 1=1
                """
                params = []
                if p["dt_from"]:
                    sql += " AND b.started_at >= ?"
                    params.append(p["dt_from"])
                if p["dt_to"]:
                    sql += " AND b.started_at <= ?"
                    params.append(p["dt_to"])
                if p["dev"]:
                    sql += " AND d.label = ?"
                    params.append(p["dev"])
                if p["person"]:
                    sql += " AND d.person = ?"
                    params.append(p["person"])
                sessions = conn.execute(sql, params).fetchall()
            finally:
                conn.close()

            ft = p["filetype"]
            fn_filter = p["filename"]

            seen_paths = set()
            for dev_id, person, dest_path in sessions:
                if not dest_path or not os.path.isdir(dest_path):
                    continue
                for root, _dirs, files in os.walk(dest_path):
                    for fname in files:
                        ext = os.path.splitext(fname)[1].lower()
                        if ft == "Фото" and ext not in IMAGE_EXTS:
                            continue
                        if ft == "Видео" and ext not in VIDEO_EXTS:
                            continue
                        if ft == "Документы" and ext not in DOC_EXTS:
                            continue
                        if fn_filter and fn_filter not in fname.lower():
                            continue
                        fpath = os.path.join(root, fname)
                        if fpath in seen_paths:
                            continue
                        seen_paths.add(fpath)
                        try:
                            mtime = os.path.getmtime(fpath)
                            fsize = os.path.getsize(fpath)
                        except OSError:
                            continue
                        dt_str = datetime.fromtimestamp(mtime).strftime("%d.%m.%Y %H:%M")
                        results.append({
                            "path": fpath,
                            "filename": fname,
                            "ext": ext,
                            "size": fsize,
                            "device": f"Device{dev_id}",
                            "person": person or "",
                            "datetime": dt_str,
                        })
                        if len(results) >= 500:
                            break
                    if len(results) >= 500:
                        break
                if len(results) >= 500:
                    break
        except Exception as e:
            print(f"[search] error: {e}", flush=True)

        try:
            self.root.after(0, self._apply_search_results, results, gen)
        except tk.TclError:
            pass

    def _apply_search_results(self, results, gen):
        if gen != self._search_gen:
            return
        for row in self.search_tree.get_children():
            self.search_tree.delete(row)
        self._search_results = results
        for r in results:
            self.search_tree.insert("", "end", values=(
                r["datetime"], r["device"], r["person"],
                r["filename"], r["ext"], self._fmt_size(r["size"]), r["path"],
            ))
        count = len(results)
        limit_note = " (лимит 500)" if count >= 500 else ""
        self._search_status_var.set(f"Найдено: {count} файлов{limit_note}")
        if count:
            self._export_btn.configure(state="normal", text=f"Выгрузить ({count})")
        else:
            self._export_btn.configure(state="disabled", text="Выгрузить (0)")

    def _open_externally(self, path):
        try:
            if platform.system() == "Windows":
                os.startfile(path)
            elif platform.system() == "Darwin":
                subprocess.Popen(["open", path])
            else:
                subprocess.Popen(["xdg-open", path])
        except Exception as e:
            messagebox.showerror("Ошибка", f"Не удалось открыть файл:\n{e}")

    def _on_search_dblclick(self, _event):
        sel = self.search_tree.selection()
        if not sel:
            return
        vals = self.search_tree.item(sel[0], "values")
        path = vals[6] if len(vals) > 6 else ""
        if not path or not os.path.isfile(path):
            messagebox.showwarning("Файл не найден", f"Файл не существует:\n{path}")
            return
        ext = os.path.splitext(path)[1].lower()
        if ext in IMAGE_EXTS:
            self._show_image_viewer(path)
        else:
            self._open_externally(path)

    def _show_image_viewer(self, path):
        win = tk.Toplevel(self.root)
        win.title(os.path.basename(path))
        win.configure(bg=self.C["bg_app"])
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        w, h = int(sw * 0.8), int(sh * 0.8)
        win.geometry(f"{w}x{h}+{(sw-w)//2}+{(sh-h)//2}")

        if _HAVE_PIL:
            try:
                img = Image.open(path)
                img.thumbnail((w - 20, h - 60))
                photo = _ImageTk.PhotoImage(img)
                lbl = tk.Label(win, image=photo, bg=self.C["bg_app"])
                lbl.image = photo
                lbl.pack(expand=True)
                tk.Label(win, text=path, fg=self.C["fg_muted"], bg=self.C["bg_app"],
                         font=("Segoe UI", 9)).pack(pady=4)
                return
            except Exception:
                pass
        try:
            photo = tk.PhotoImage(file=path)
            lbl = tk.Label(win, image=photo, bg=self.C["bg_app"])
            lbl.image = photo
            lbl.pack(expand=True)
        except Exception:
            win.destroy()
            self._open_externally(path)

    def _export_found_files(self):
        if not self._search_results:
            return
        dest = filedialog.askdirectory(
            title="Выберите папку для выгрузки файлов",
            parent=self.root,
        )
        if not dest:
            return
        count = len(self._search_results)
        if not messagebox.askyesno(
            "Подтверждение",
            f"Выгрузить {count} файлов в:\n{dest}?",
            parent=self.root,
        ):
            return
        self._export_btn.configure(state="disabled", text="Выгрузка...")
        threading.Thread(target=self._export_worker,
                         args=(list(self._search_results), dest), daemon=True).start()

    def _export_worker(self, results, dest):
        ok = 0
        errors = []
        for r in results:
            try:
                dst = os.path.join(dest, r["filename"])
                if os.path.exists(dst):
                    base, ext = os.path.splitext(r["filename"])
                    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
                    dst = os.path.join(dest, f"{base}_{ts}{ext}")
                shutil.copy2(r["path"], dst)
                ok += 1
            except Exception as e:
                errors.append(str(e))
        try:
            self.root.after(0, self._export_done, ok, errors)
        except tk.TclError:
            pass

    def _export_done(self, ok, errors):
        n = len(self._search_results)
        state = "normal" if n > 0 else "disabled"
        self._export_btn.configure(state=state, text=f"Выгрузить ({n})")
        if errors:
            shown = "\n".join(errors[:5])
            more = f"\n...и ещё {len(errors)-5}" if len(errors) > 5 else ""
            messagebox.showwarning("Выгрузка завершена",
                                   f"Скопировано: {ok}\nОшибок: {len(errors)}\n\n{shown}{more}")
        else:
            messagebox.showinfo("Выгрузка завершена", f"Скопировано файлов: {ok}")

    def _reset_search(self):
        self._reset_search_dates()
        self.search_device.set("")
        self.search_person.set("")
        self.search_filetype.set("Все")
        self.search_filename_var.set("")
        self._refresh_search_filters()
        self._do_search()

    def _refresh_devices(self):
        for row in self.dev_tree.get_children():
            self.dev_tree.delete(row)
        conn = self._get_db()
        try:
            for row in conn.execute("SELECT id, serial, label, person, first_seen, last_seen FROM devices ORDER BY id"):
                self.dev_tree.insert("", "end", values=(
                    row[0], row[1], row[2], row[3], row[4][:19], row[5][:19],
                ))
        finally:
            conn.close()

    def _on_device_select(self, _e):
        sel = self.dev_tree.selection()
        if sel:
            vals = self.dev_tree.item(sel[0], "values")
            self.edit_dev_id.delete(0, "end")
            self.edit_dev_id.insert(0, vals[0])
            self.edit_person.delete(0, "end")
            self.edit_person.insert(0, vals[3])

    def _assign_person(self):
        dev_id = self.edit_dev_id.get().strip()
        person = self.edit_person.get().strip()
        if not dev_id:
            messagebox.showwarning("Ошибка", "Выберите устройство из списка")
            return
        conn = self._get_db()
        try:
            conn.execute("UPDATE devices SET person = ? WHERE id = ?", (person, int(dev_id)))
            conn.commit()
            messagebox.showinfo("Готово", f"Device{dev_id} назначен на {person or '(не указан)'}")
            self._refresh_devices()
            self._refresh_search_filters()
        except Exception as e:
            messagebox.showerror("Ошибка", str(e))
        finally:
            conn.close()

    def _clean_device_videos(self):
        dev_id = self.edit_dev_id.get().strip()
        if not dev_id:
            messagebox.showwarning("Ошибка", "Выберите устройство из списка")
            return
        if not dev_id.isdigit():
            messagebox.showwarning("Ошибка", "Некорректный Device ID")
            return

        dev_dir = os.path.join(get_dest_base(), f"Device{dev_id}")
        if not os.path.isdir(dev_dir):
            messagebox.showinfo("Нет данных", f"Папка устройства Device{dev_id} не найдена")
            return

        videos = []
        total_size = 0
        for root, _dirs, files in os.walk(dev_dir):
            for name in files:
                if os.path.splitext(name)[1].lower() in VIDEO_EXTS:
                    fp = os.path.join(root, name)
                    try:
                        total_size += os.path.getsize(fp)
                    except OSError:
                        pass
                    videos.append(fp)

        if not videos:
            messagebox.showinfo("Нет видео", f"В Device{dev_id} нет видеофайлов")
            return

        size_mb = total_size / (1024 * 1024)
        if not messagebox.askyesno(
            "Подтверждение",
            f"Удалить {len(videos)} видеофайлов "
            f"({size_mb:.1f} МБ) из папки Device{dev_id}?\n\n"
            f"Это действие необратимо."
        ):
            return

        deleted = 0
        errors = []
        for fp in videos:
            try:
                os.remove(fp)
                deleted += 1
            except OSError as e:
                errors.append(f"{os.path.basename(fp)}: {e}")

        msg = f"Удалено видеофайлов: {deleted} из {len(videos)}"
        if errors:
            shown = "\n".join(errors[:5])
            more = f"\n...и ещё {len(errors) - 5}" if len(errors) > 5 else ""
            messagebox.showwarning("Завершено с ошибками", f"{msg}\n\nОшибки:\n{shown}{more}")
        else:
            messagebox.showinfo("Готово", msg)

    def _start_monitor(self):
        self.stop_event.clear()
        self.mon_status.set("Мониторинг: запуск...")

        def _run():
            try:
                self.progress_queue.put(("_status_", "", "info", 0, 0, "Мониторинг USB запущен"))
                monitor_usb(2, self.stop_event, self.progress_queue)
            except Exception as e:
                self.progress_queue.put(("_status_", "", "error", 0, 0, f"Ошибка: {e}"))

        self.monitor_thread = threading.Thread(target=_run, daemon=True)
        self.monitor_thread.start()

    def _poll_queue(self):
        try:
            while True:
                raw = self.progress_queue.get_nowait()
                device_id, display_id, state, current, total, msg = raw[:6]
                devname = raw[6] if len(raw) > 6 else ""

                if device_id == "_removed_":
                    for did, data in list(self.workers_data.items()):
                        if data.get("devname") == display_id:
                            self._done_times.pop(did, None)
                            self.workers_data.pop(did, None)
                    self._refresh_workers()
                    continue

                if device_id == "_status_":
                    self.mon_status.set(msg)
                    self._refresh_workers()
                    continue

                if total and total > 0:
                    pct = int(current / total * 100)
                else:
                    pct = 0

                self.workers_data[device_id] = {
                    "device": display_id,
                    "state": {"scanning": "Сканирование", "copying": "Копирование", "done": "Готово"}.get(state, state),
                    "progress": f"{pct}% ({self._fmt_size(current)} / {self._fmt_size(total)})" if total else msg,
                    "files": str(current) if state == "copying" else "",
                    "size": self._fmt_size(total),
                    "message": msg,
                    "devname": devname,
                }
                self._refresh_workers()
        except queue.Empty:
            pass
        try:
            self.root.after(POLL_MS, self._poll_queue)
        except tk.TclError:
            pass

    def _refresh_workers(self):
        now = time.time()
        tracked = set()
        for dev_id, data in self.workers_data.items():
            tracked.add(dev_id)
            state = data["state"]
            if dev_id in self.port_assignment:
                pi = self.port_assignment[dev_id]
            else:
                used = set(self.port_assignment.values())
                pi = next((i for i in range(len(self.ports)) if i not in used), None)
                if pi is None:
                    continue
                self.port_assignment[dev_id] = pi
            port = self.ports[pi]
            port["device_id"] = dev_id

            if state == "Сканирование":
                bg = self.C["accent"]
                preview_text = data["device"]
                status_text = f"Сканирование... {data.get('message', '')}"
            elif state == "Копирование":
                bg = self.C["accent_warn"]
                preview_text = data["device"]
                status_text = data.get("progress", "Копирование...")
            elif state == "Готово":
                bg = self.C["accent_ok"]
                preview_text = data["device"]
                status_text = data.get("message", "Готово")
            else:
                bg = self.C["bg_surface"]
                preview_text = data["device"]
                status_text = data.get("message", state)

            port["preview"].configure(text=preview_text, bg=bg)
            port["status"].configure(text=status_text)

        for pi, port in enumerate(self.ports):
            did = port["device_id"]
            if did is not None and did not in tracked:
                port["device_id"] = None
                self.port_assignment.pop(did, None)
                port["preview"].configure(text="Простой", bg=self.C["accent"])
                port["status"].configure(text="Нет передачи данных")

        done_ids = [did for did, d in self.workers_data.items() if d["state"] == "Готово"]
        for did in done_ids:
            if did not in self._done_times:
                self._done_times[did] = now
            elif now - self._done_times[did] >= 5.0:
                self._done_times.pop(did, None)
                self.workers_data.pop(did, None)

    def _fmt_size(self, b):
        for unit in ("B", "KB", "MB", "GB", "TB"):
            if b < 1024:
                return f"{b:.1f} {unit}"
            b /= 1024
        return f"{b:.1f} PB"

    def run(self):
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.root.mainloop()


def launch():
    # Load the TTS model first (with a splash), then start the app + interface.
    _preload_with_splash()
    App().run()


if __name__ == "__main__":
    import sys
    if "--voice-test" in sys.argv:
        # Diagnose the greeting voice from a console (prints which engine runs
        # and why others are skipped). Run: python gui.py --voice-test
        print("[nanosuit] voice diagnostic — trying engines in quality order…", flush=True)
        print(f"[nanosuit] reference clip: {_CLONE_REF_PATH} "
              f"(exists={os.path.exists(_CLONE_REF_PATH)})", flush=True)
        print(f"[nanosuit] NANOSUIT_VOICE={_VOICE_FORCE or '(auto)'} "
              f"NANOSUIT_CLONE_FX={int(_CLONE_FX)}", flush=True)
        used = _nanosuit_greeting()
        print(f"[nanosuit] engine that produced audio: {used or 'none'}", flush=True)
    else:
        launch()
