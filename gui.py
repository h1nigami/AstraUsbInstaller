import os
import json
import platform
import queue
import sqlite3
import subprocess
import threading
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog
from datetime import datetime

from usb_monitor import monitor_usb, DB_PATH, _init_db, DEST_BASE

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


def _nanosuit_greeting_windows():
    text = " ".join(_NANOSUIT_LINES)
    # PowerShell SAPI — встроен в Windows, ничего не нужно устанавливать.
    # Rate: -10 (медленно) до 10 (быстро); -4 даёт медленную роботизированную речь.
    # Пытаемся выбрать русский голос если установлен, иначе говорит дефолтным.
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
    # Try espeak-ng then espeak — both available on Debian/Astra Linux.
    # Parameters: very low pitch (-p 8), slow deliberate speech (-s 82),
    # loud amplitude (-a 200), male voice variant — gives the Crysis robotic tone.
    args_base = ["-v", "ru+m3", "-s", "82", "-p", "8", "-a", "200"]
    for binary in ("espeak-ng", "espeak"):
        try:
            subprocess.run([binary, "--version"], capture_output=True, timeout=3, check=True)
        except FileNotFoundError:
            continue
        except Exception:
            continue
        for line in _NANOSUIT_LINES:
            try:
                subprocess.run([binary, *args_base, line], capture_output=True, timeout=10)
            except Exception as e:
                print(f"[nanosuit] speech error: {e}", flush=True)
        return
    print("[nanosuit] espeak-ng/espeak not found — install: sudo apt install espeak-ng espeak-ng-data", flush=True)


class App:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("USB Backup Manager")
        self.root.attributes("-fullscreen", True)
        self.root.bind("<Escape>", lambda e: None)

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

    def _build_ui(self):
        nb = ttk.Notebook(self.root)
        nb.pack(fill="both", expand=True)
        self._build_workers_tab(nb)
        self._build_search_tab(nb)
        self._build_devices_tab(nb)
        self._build_settings_tab(nb)

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
        for c in cols:
            self.search_tree.heading(c, text=headings[c])
            self.search_tree.column(c, width=100)
        self.search_tree.column("path", width=200)
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
        for c in cols:
            self.dev_tree.heading(c, text=headings[c])
            self.dev_tree.column(c, width=120)
        self.dev_tree.column("serial", width=180)
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
        ttk.Button(edit_frame, text="Очистить видео", command=self._clean_device_videos).pack(side="left", padx=4)
        ttk.Button(edit_frame, text="Обновить список", command=self._refresh_devices).pack(side="right", padx=4)

        self.dev_tree.bind("<<TreeviewSelect>>", self._on_device_select)
        self._refresh_devices()

    def _build_workers_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Загрузка")

        self.mon_status = tk.StringVar(value="Мониторинг: запуск...")
        ttk.Label(f, textvariable=self.mon_status, foreground="gray").pack(anchor="w", padx=5, pady=(5, 0))

        self.ports = []
        self.port_assignment = {}
        self.next_port = 0

        grid = ttk.Frame(f)
        grid.pack(fill="both", expand=True, padx=10, pady=10)

        rows, cols = 3, 4
        for i in range(rows * cols):
            r, c = divmod(i, cols)
            cell = ttk.Frame(grid, relief="solid", borderwidth=2)
            cell.grid(row=r, column=c, padx=6, pady=6, sticky="nsew")
            grid.columnconfigure(c, weight=1, uniform="port")
            grid.rowconfigure(r, weight=1, uniform="port")

            preview = tk.Label(cell, text="Простой", font=("Segoe UI", 14, "bold"),
                               fg="white", bg="#2563eb")
            preview.pack(fill="both", expand=True)

            status = tk.Label(cell, text="Нет передачи данных", font=("Segoe UI", 10),
                              fg="#444", bg="white")
            status.pack(fill="x", ipady=4)

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
        dlg = tk.Toplevel(self.root)
        dlg.title("Подтверждение выхода")
        dlg.geometry("300x150")
        dlg.resizable(False, False)
        dlg.transient(self.root)
        dlg.grab_set()

        ttk.Label(dlg, text="Введите пароль для выхода:").pack(pady=(15, 5))
        pw_var = tk.StringVar()
        pw_entry = ttk.Entry(dlg, textvariable=pw_var, show="*", width=25)
        pw_entry.pack(pady=5)
        pw_entry.focus_set()

        err_var = tk.StringVar()
        ttk.Label(dlg, textvariable=err_var, foreground="red").pack()

        def confirm():
            pw_in = pw_var.get().strip()
            expected = _get_exit_password()
            if pw_in == expected:
                dlg.destroy()
                self.stop_event.set()
                self.root.destroy()
            else:
                err_var.set("Неверный пароль")

        ttk.Button(dlg, text="Выйти", command=confirm).pack(pady=10)
        pw_entry.bind("<Return>", lambda e: confirm())
        dlg.bind("<Return>", lambda e: confirm())

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
                bg = "#2563eb"
                preview_text = data["device"]
                status_text = f"Сканирование... {data.get('message', '')}"
            elif state == "Копирование":
                bg = "#d97706"
                preview_text = data["device"]
                status_text = data.get("progress", "Копирование...")
            elif state == "Готово":
                bg = "#16a34a"
                preview_text = data["device"]
                status_text = data.get("message", "Готово")
            else:
                bg = "#6b7280"
                preview_text = data["device"]
                status_text = data.get("message", state)

            port["preview"].configure(text=preview_text, bg=bg)
            port["status"].configure(text=status_text)

        for pi, port in enumerate(self.ports):
            did = port["device_id"]
            if did is not None and did not in tracked:
                port["device_id"] = None
                self.port_assignment.pop(did, None)
                port["preview"].configure(text="Простой", bg="#2563eb")
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
