import os
import json
import platform
import queue
import sqlite3
import subprocess
import io
import tempfile
import wave
import threading
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog
from datetime import datetime

from usb_monitor import monitor_usb, DB_PATH, _init_db, DEST_BASE

try:
    import numpy as np
    from scipy import signal as _scipy_signal
    _HAVE_DSP = True
except Exception:
    _HAVE_DSP = False

POLL_MS = 200
VIDEO_EXTS = {".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".mpeg",
              ".m4v", ".3gp", ".ts", ".flv", ".webm", ".m2ts", ".vob", ".mts"}
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


def _nanosuit_greeting():
    if platform.system() == "Windows":
        _nanosuit_greeting_windows()
    else:
        _nanosuit_greeting_linux()


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
        if platform.system() == "Windows":
            import winsound
            winsound.PlaySound(path, winsound.SND_FILENAME)
        else:
            res = subprocess.run(["aplay", "-q", path], capture_output=True, timeout=20)
            if res.returncode != 0:
                err = res.stderr.decode(errors="replace").strip()
                print(f"[nanosuit] aplay error (rc={res.returncode}): {err}", flush=True)
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def _silero_synthesize(text):
    """Render text with Silero neural TTS → (float32 mono, sample_rate).

    Returns None if torch or the model is unavailable. The model file is
    downloaded once to data/ and then loaded locally (offline afterward).
    API: torch.package.PackageImporter(...).load_pickle("tts_models", "model")
    then model.apply_tts(text=, speaker=, sample_rate=) → torch tensor.
    """
    global _silero_model
    if not _HAVE_DSP:
        return None
    try:
        import torch
    except Exception:
        return None
    try:
        if _silero_model is None:
            if not os.path.exists(_SILERO_MODEL_PATH):
                os.makedirs(os.path.dirname(_SILERO_MODEL_PATH), exist_ok=True)
                print("[nanosuit] downloading Silero voice model (~60 MB, one time)…", flush=True)
                torch.hub.download_url_to_file(_SILERO_MODEL_URL, _SILERO_MODEL_PATH)
            imp = torch.package.PackageImporter(_SILERO_MODEL_PATH)
            _silero_model = imp.load_pickle("tts_models", "model")
            _silero_model.to(torch.device("cpu"))
        torch.set_num_threads(max(1, os.cpu_count() or 1))
        tensor = _silero_model.apply_tts(
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


def _nanosuit_greeting_windows():
    # Silero neural voice + DSP: real human voice → closest to the game
    if _play_silero_fx():
        return
    binary = _has_bin("espeak-ng", "espeak")
    # espeak + DSP: robotic synth fallback when Silero/torch unavailable
    if binary and _play_with_python_fx(binary):
        return
    # SAPI → WAV → DSP: no espeak, but same nanosuit chain applied
    text = " ".join(_NANOSUIT_LINES)
    if _sapi_to_wav_and_play(text):
        return
    # Plain espeak without DSP
    if binary:
        _play_plain_espeak(binary)
        print("[nanosuit] install numpy+scipy for full Crysis sound: pip install numpy scipy", flush=True)
        return
    # Last resort: plain SAPI, no DSP
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
    except Exception as e:
        print(f"[nanosuit] Windows TTS error: {e}", flush=True)


def _nanosuit_greeting_linux():
    # Silero neural voice + DSP: real human voice → closest to the game
    if _play_silero_fx():
        return
    binary = _has_bin("espeak-ng", "espeak")
    if not binary:
        print("[nanosuit] espeak-ng not found — install: sudo apt install espeak-ng espeak-ng-data alsa-utils", flush=True)
        print("[nanosuit] for the neural nanosuit voice: pip install torch numpy scipy", flush=True)
        return
    if _play_with_python_fx(binary):
        return
    _play_plain_espeak(binary)
    print("[nanosuit] for the neural nanosuit voice: pip install torch numpy scipy", flush=True)


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

        self.mon_status = tk.StringVar(value="Мониторинг: запуск...")

        self.progress_queue = queue.Queue()
        self.stop_event = threading.Event()
        self.monitor_thread = None
        self.workers_data = {}

        conn = _init_db()
        conn.close()

        threading.Thread(target=_nanosuit_greeting, daemon=True).start()
        self._build_ui()
        self._poll_queue()
        self._start_monitor()

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
            self._last_tab = idx
            return
        self._unlock_in_progress = True
        try:
            self.nb.select(self._last_tab)
            if self._prompt_unlock():
                self.tabs_unlocked = True
                self.nb.select(idx)
                self._last_tab = idx
        finally:
            self._unlock_in_progress = False

    def _build_search_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Поиск")

        top = ttk.Frame(f)
        top.pack(fill="x", padx=5, pady=5)

        ttk.Label(top, text="От:").grid(row=0, column=0, padx=2)
        self.search_date_from = ttk.Entry(top, width=12)
        self.search_date_from.grid(row=0, column=1, padx=2)
        self.search_date_from.insert(0, "")

        ttk.Label(top, text="До:").grid(row=0, column=2, padx=2)
        self.search_date_to = ttk.Entry(top, width=12)
        self.search_date_to.grid(row=0, column=3, padx=2)

        ttk.Label(top, text="Устройство:").grid(row=0, column=4, padx=2)
        self.search_device = ttk.Combobox(top, width=14, state="readonly")
        self.search_device.grid(row=0, column=5, padx=2)

        ttk.Label(top, text="Человек:").grid(row=0, column=6, padx=2)
        self.search_person = ttk.Combobox(top, width="14", state="readonly")
        self.search_person.grid(row=0, column=7, padx=2)

        ttk.Button(top, text="Найти", command=self._do_search).grid(row=0, column=8, padx=4)
        ttk.Button(top, text="Сброс", command=self._reset_search).grid(row=0, column=9, padx=4)

        cols = ("datetime", "device", "label", "person", "files", "size", "path")
        self.search_tree = ttk.Treeview(f, columns=cols, show="headings", height=18)
        headings = {"datetime": "Дата/время", "device": "Устройство", "label": "Метка",
                    "person": "Человек", "files": "Файлы", "size": "Размер", "path": "Путь"}
        col_widths = {"datetime": 150, "device": 90, "label": 140, "person": 140,
                      "files": 70, "size": 90, "path": 280}
        for c in cols:
            self.search_tree.heading(c, text=headings[c])
            self.search_tree.column(c, width=col_widths[c])
        vsb = ttk.Scrollbar(f, orient="vertical", command=self.search_tree.yview)
        self.search_tree.configure(yscrollcommand=vsb.set)
        self.search_tree.pack(fill="both", expand=True, padx=5, pady=(0, 5), side="left")
        vsb.pack(fill="y", pady=(0, 5), side="right")

        self._refresh_search_filters()

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
        self.port_assignment = {}
        self.next_port = 0

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

        frame = ttk.LabelFrame(f, text="Защита выхода", padding=10)
        frame.pack(fill="x", padx=10, pady=10)

        ttk.Label(frame, text="Выход из программы защищён паролем.").pack(anchor="w")
        self.pw_status = tk.StringVar()
        ttk.Label(frame, textvariable=self.pw_status, foreground="gray").pack(anchor="w", pady=(0, 10))

        ttk.Button(frame, text="Сменить пароль", command=self._change_password).pack(anchor="w")

        self._refresh_pw_status()

        about = ttk.LabelFrame(f, text="О программе", padding=16)
        about.pack(fill="x", padx=10, pady=(0, 10))
        ttk.Label(about, text="BestElectronics USB Backup Manager").pack(anchor="w")
        ttk.Label(about, text="Автоматическое резервное копирование USB-устройств.", foreground=self.C["fg_muted"]).pack(anchor="w")

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

        conn = self._get_db()
        try:
            sql = """
                SELECT b.started_at, d.id, d.label, d.person,
                       b.total_files, b.total_bytes, b.dest_path
                FROM backups b
                JOIN devices d ON d.id = b.device_id
                WHERE 1=1
            """
            params = []

            dt_from = self.search_date_from.get().strip()
            dt_to = self.search_date_to.get().strip()
            dev = self.search_device.get()
            person = self.search_person.get()

            if dt_from:
                sql += " AND b.started_at >= ?"
                params.append(dt_from)
            if dt_to:
                sql += " AND b.started_at <= ?"
                params.append(dt_to + "T23:59:59")
            if dev:
                sql += " AND d.label = ?"
                params.append(dev)
            if person:
                sql += " AND d.person = ?"
                params.append(person)

            sql += " ORDER BY b.started_at DESC"

            for row in conn.execute(sql, params):
                self.search_tree.insert("", "end", values=(
                    row[0][:19],
                    f"Device{row[1]}",
                    row[2],
                    row[3],
                    row[4],
                    self._fmt_size(row[5]),
                    row[6],
                ))
        finally:
            conn.close()

    def _reset_search(self):
        self.search_date_from.delete(0, "end")
        self.search_date_to.delete(0, "end")
        self.search_device.set("")
        self.search_person.set("")
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

        dev_dir = os.path.join(DEST_BASE, f"Device{dev_id}")
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
        self.root.after(POLL_MS, self._poll_queue)

    def _refresh_workers(self):
        tracked = set()
        for dev_id, data in self.workers_data.items():
            tracked.add(dev_id)
            state = data["state"]
            if dev_id in self.port_assignment:
                pi = self.port_assignment[dev_id]
            else:
                pi = self.next_port % 10
                self.next_port = (self.next_port + 1) % 10
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
    App().run()


if __name__ == "__main__":
    launch()
