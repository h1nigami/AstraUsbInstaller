import os
import json
import platform
import queue
import shutil
import sqlite3
import subprocess
import time
import threading
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog, filedialog
from datetime import datetime, timedelta

from usb_monitor import monitor_usb, DB_PATH, _init_db, DEST_BASE, get_dest_base, ensure_dest_marker, describe_dest_path, VIDEO_EXTS, cleanup_old_backup_videos, _format_size, format_filter_dt, read_version, touch_copying_marker

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".raw", ".cr2", ".nef"}
DOC_EXTS   = {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".odt", ".ods"}

try:
    from PIL import Image, ImageTk as _ImageTk
    _HAVE_PIL = True
except ImportError:
    _HAVE_PIL = False

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


def _is_busy(workers_data):
    """True while any tracked device is scanning or copying.

    Tests the raw state ("scanning"/"copying"), not the localized display
    string — renaming a label must not silently disable the updater's busy
    marker.
    """
    return any(d.get("state_raw") in ("scanning", "copying") for d in workers_data.values())


class App:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("BestCam USB Backup Manager")
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
        self._cleanup_enabled = bool(cfg.get("auto_cleanup_enabled", False))
        self._cleanup_days = int(cfg.get("auto_cleanup_days", 30))

        self.mon_status = tk.StringVar(value="Мониторинг: запуск...")

        self.progress_queue = queue.Queue()
        self.stop_event = threading.Event()
        self.monitor_thread = None
        self.workers_data = {}
        self.port_assignment = {}
        self._search_results = []
        self._search_gen = 0

        conn = _init_db()
        conn.close()

        self._build_ui()
        self.root.bind_all("<Button>", self._touch_activity, add=True)
        self.root.bind_all("<Key>", self._touch_activity, add=True)
        self._poll_queue()
        self._start_monitor()
        self._check_lock_timeout()
        if self._cleanup_enabled:
            threading.Thread(target=self._run_startup_cleanup, daemon=True).start()

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
        tk.Label(box, text="BestCam", font=("Segoe UI", 20, "bold"),
                 fg=C["brand"], bg=C["bg_panel"]).pack(anchor="w")
        tk.Label(box, text="USB Backup Manager", font=("Segoe UI", 12),
                 fg=C["fg_muted"], bg=C["bg_panel"]).pack(anchor="w")

        # Кнопка выхода (защищена паролем). В полноэкранном режиме у окна нет
        # системной кнопки закрытия, поэтому это единственный видимый способ выйти.
        ttk.Button(hdr, text="⏻ Выход", style="Danger.TButton",
                   command=self._on_close).pack(side="right", padx=16, pady=20)

        tk.Frame(self.root, bg=C["brand"], height=2).pack(fill="x")

    def _build_statusbar(self):
        C = self.C
        bar = tk.Frame(self.root, bg=C["bg_panel"], height=28)
        bar.pack(fill="x", side="bottom")
        bar.pack_propagate(False)
        tk.Label(bar, text="© BestCam", font=("Segoe UI", 10),
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

        # Кнопки отдельной строкой, а не пятой колонкой: на экране 1024 пикселя
        # пятая колонка не помещалась и «Сброс» с «Выгрузить» уезжали за край.
        btn_frame = ttk.Frame(top)
        btn_frame.grid(row=3, column=0, columnspan=4, padx=2, pady=(8, 0), sticky="w")
        ttk.Button(btn_frame, text="Найти", command=self._do_search).pack(side="left", padx=2)
        ttk.Button(btn_frame, text="Сброс", command=self._reset_search).pack(side="left", padx=2)
        self._export_btn = ttk.Button(btn_frame, text="Выгрузить (0)", command=self._export_found_files, state="disabled")
        self._export_btn.pack(side="left", padx=2)

        # Status label for search progress
        self._search_status_var = tk.StringVar(value="")
        ttk.Label(f, textvariable=self._search_status_var, foreground=self.C["brand"],
                  font=("Segoe UI", 10)).pack(anchor="w", padx=8)

        cols = ("datetime", "device", "person", "filename", "ext", "size", "path")
        # height=5 — это минимум, а не фактический размер: таблица упакована с
        # expand=True и занимает весь остаток вкладки. Прежние 16 строк задирали
        # требуемую высоту так, что кнопки и статус уходили за нижний край.
        self.search_tree = ttk.Treeview(f, columns=cols, show="headings", height=5)
        headings = {
            "datetime": "Дата изм.", "device": "Устройство", "person": "Человек",
            "filename": "Файл", "ext": "Тип", "size": "Размер", "path": "Путь",
        }
        col_widths = {"datetime": 140, "device": 160, "person": 130,
                      "filename": 200, "ext": 60, "size": 80, "path": 320}
        for c in cols:
            self.search_tree.heading(c, text=headings[c])
            self.search_tree.column(c, width=col_widths[c])
        vsb = ttk.Scrollbar(f, orient="vertical", command=self.search_tree.yview)
        self.search_tree.configure(yscrollcommand=vsb.set)
        self.search_tree.pack(fill="both", expand=True, padx=5, pady=(0, 5), side="left")
        vsb.pack(fill="y", pady=(0, 5), side="right")

        self.search_tree.bind("<Double-1>", self._on_search_dblclick)

        self._device_filter_ids = {}
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
            return format_filter_dt(self._from_year.get(), self._from_mon.get(),
                                    self._from_day.get(), self._from_hour.get(),
                                    self._from_min.get(), "00")
        except Exception:
            return ""

    def _get_dt_to(self):
        try:
            return format_filter_dt(self._to_year.get(), self._to_mon.get(),
                                    self._to_day.get(), self._to_hour.get(),
                                    self._to_min.get(), "59")
        except Exception:
            return ""

    def _build_devices_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Устройства")

        cols = ("id", "serial", "label", "name", "person", "first_seen", "last_seen")
        self.dev_tree = ttk.Treeview(f, columns=cols, show="headings", height=5)
        headings = {"id": "ID", "serial": "Серийный", "label": "Метка", "name": "Имя",
                    "person": "Человек", "first_seen": "Впервые", "last_seen": "Последний раз"}
        dev_col_widths = {"id": 50, "serial": 200, "label": 150, "name": 150, "person": 150,
                          "first_seen": 160, "last_seen": 160}
        for c in cols:
            self.dev_tree.heading(c, text=headings[c])
            self.dev_tree.column(c, width=dev_col_widths[c])

        # Панель с кнопками пакуется раньше таблицы и прижата к низу: pack
        # отдаёт место в порядке упаковки, поэтому при нехватке высоты
        # ужимается таблица, а кнопки остаются на экране.
        edit_frame = ttk.Frame(f)
        edit_frame.pack(side="bottom", fill="x", padx=5, pady=(0, 5))
        self.dev_tree.pack(fill="both", expand=True, padx=5, pady=5)
        # Два ряда, а не один: в ширину 1024 весь набор полей и кнопок не
        # помещался, и «Очистить видео» с «Обновить список» уезжали за край.
        row1 = ttk.Frame(edit_frame)
        row1.pack(fill="x")
        ttk.Label(row1, text="Номер устройства:").pack(side="left", padx=2)
        self.edit_dev_id = ttk.Entry(row1, width=6)
        self.edit_dev_id.pack(side="left", padx=2)
        ttk.Label(row1, text="Имя:").pack(side="left", padx=2)
        self.edit_name = ttk.Entry(row1, width=20)
        self.edit_name.pack(side="left", padx=2)
        ttk.Button(row1, text="Переименовать", command=self._rename_device).pack(side="left", padx=4)
        ttk.Label(row1, text="Человек:").pack(side="left", padx=2)
        self.edit_person = ttk.Entry(row1, width=20)
        self.edit_person.pack(side="left", padx=2)
        ttk.Button(row1, text="Назначить", command=self._assign_person).pack(side="left", padx=4)

        row2 = ttk.Frame(edit_frame)
        row2.pack(fill="x", pady=(6, 0))
        ttk.Button(row2, text="Очистить видео", command=self._clean_device_videos, style="Danger.TButton").pack(side="left", padx=2)
        ttk.Button(row2, text="Обновить список", command=self._refresh_devices).pack(side="left", padx=4)

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

        # Две колонки. В один столбец пять блоков просят 805 пикселей, а на
        # док-станции вкладке достаётся 440: автоочистка и «О программе»
        # оказывались за нижним краем и были недостижимы.
        f.columnconfigure(0, weight=1, uniform="settings")
        f.columnconfigure(1, weight=1, uniform="settings")
        left = ttk.Frame(f)
        left.grid(row=0, column=0, sticky="nsew")
        right = ttk.Frame(f)
        right.grid(row=0, column=1, sticky="nsew")

        folder_frame = ttk.LabelFrame(left, text="Папка для резервных копий", padding=10)
        folder_frame.pack(fill="x", padx=10, pady=5)

        ttk.Label(folder_frame, text="Текущая папка:").pack(anchor="w")
        self.backup_dest_var = tk.StringVar(value=get_dest_base())
        ttk.Label(folder_frame, textvariable=self.backup_dest_var,
                  foreground=self.C["brand"], wraplength=420,
                  font=("Segoe UI", 11)).pack(anchor="w", pady=(2, 8))
        ttk.Button(folder_frame, text="Выбрать папку", command=self._change_backup_dest).pack(anchor="w")

        frame = ttk.LabelFrame(left, text="Защита выхода", padding=10)
        frame.pack(fill="x", padx=10, pady=5)

        ttk.Label(frame, text="Выход из программы защищён паролем.").pack(anchor="w")
        self.pw_status = tk.StringVar()
        ttk.Label(frame, textvariable=self.pw_status, foreground="gray").pack(anchor="w", pady=(0, 10))

        ttk.Button(frame, text="Сменить пароль", command=self._change_password).pack(anchor="w")

        self._refresh_pw_status()

        lock_frame = ttk.LabelFrame(right, text="Автоблокировка разделов", padding=10)
        lock_frame.pack(fill="x", padx=10, pady=5)

        ttk.Label(lock_frame, text="Время до блокировки (минут, 0 — отключено):").pack(anchor="w")
        self._timeout_var = tk.StringVar(value=str(int(self._lock_timeout / 60)))
        timeout_row = ttk.Frame(lock_frame)
        timeout_row.pack(anchor="w", pady=(4, 0))
        ttk.Entry(timeout_row, textvariable=self._timeout_var, width=6).pack(side="left")
        ttk.Button(timeout_row, text="Сохранить", command=self._save_lock_timeout).pack(side="left", padx=6)
        self._timeout_status = tk.StringVar()
        ttk.Label(lock_frame, textvariable=self._timeout_status, foreground="gray").pack(anchor="w", pady=(4, 0))
        self._refresh_timeout_status()

        cleanup_frame = ttk.LabelFrame(right, text="Автоочистка старых видео", padding=10)
        cleanup_frame.pack(fill="x", padx=10, pady=5)

        self._cleanup_enabled_var = tk.BooleanVar(value=self._cleanup_enabled)
        ttk.Checkbutton(
            cleanup_frame,
            text="Включить автоочистку при запуске",
            variable=self._cleanup_enabled_var,
        ).pack(anchor="w")

        days_row = ttk.Frame(cleanup_frame)
        days_row.pack(anchor="w", pady=(6, 0))
        ttk.Label(days_row, text="Удалять видео старше (дней):").pack(side="left")
        self._cleanup_days_var = tk.StringVar(value=str(self._cleanup_days))
        ttk.Entry(days_row, textvariable=self._cleanup_days_var, width=6).pack(side="left", padx=6)

        btn_row = ttk.Frame(cleanup_frame)
        btn_row.pack(anchor="w", pady=(8, 0))
        ttk.Button(btn_row, text="Сохранить", command=self._save_cleanup_settings).pack(side="left")
        ttk.Button(btn_row, text="Запустить очистку сейчас", command=self._run_cleanup_now).pack(side="left", padx=8)

        self._cleanup_status_var = tk.StringVar(value="")
        ttk.Label(cleanup_frame, textvariable=self._cleanup_status_var,
                  foreground=self.C["fg_muted"]).pack(anchor="w", pady=(6, 0))

        about = ttk.LabelFrame(left, text="О программе", padding=10)
        about.pack(fill="x", padx=10, pady=5)
        ttk.Label(about, text="BestCam USB Backup Manager").pack(anchor="w")
        ttk.Label(about, text="Автоматическое резервное копирование USB-устройств.", foreground=self.C["fg_muted"]).pack(anchor="w")
        ttk.Label(about, text=self._version_text(),
                  foreground=self.C["fg_muted"]).pack(anchor="w", pady=(6, 0))

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

    def _save_cleanup_settings(self):
        try:
            days = int(self._cleanup_days_var.get().strip())
            if days < 1:
                raise ValueError
        except ValueError:
            messagebox.showwarning("Ошибка", "Введите целое число дней (не менее 1)")
            return
        self._cleanup_enabled = self._cleanup_enabled_var.get()
        self._cleanup_days = days
        cfg = _load_config()
        cfg["auto_cleanup_enabled"] = self._cleanup_enabled
        cfg["auto_cleanup_days"] = days
        _save_config(cfg)
        self._cleanup_status_var.set("Настройки сохранены")

    def _run_startup_cleanup(self):
        deleted, freed = cleanup_old_backup_videos(older_than_days=self._cleanup_days)
        if deleted:
            msg = f"Автоочистка при запуске: удалено {deleted} видео, освобождено {_format_size(freed)}"
        else:
            msg = "Автоочистка при запуске: старых видео не найдено"
        try:
            self._cleanup_status_var.set(msg)
        except Exception:
            pass

    def _run_cleanup_now(self):
        try:
            days = int(self._cleanup_days_var.get().strip())
            if days < 1:
                raise ValueError
        except ValueError:
            messagebox.showwarning("Ошибка", "Введите целое число дней (не менее 1)")
            return
        self._cleanup_status_var.set("Очистка...")

        def _do():
            deleted, freed = cleanup_old_backup_videos(older_than_days=days)
            if deleted:
                msg = f"Удалено {deleted} видео, освобождено {_format_size(freed)}"
            else:
                msg = "Старых видео не найдено"
            try:
                self._cleanup_status_var.set(msg)
            except Exception:
                pass

        threading.Thread(target=_do, daemon=True).start()

    def _change_backup_dest(self):
        current = get_dest_base()
        new_path = filedialog.askdirectory(
            title="Выберите папку для резервных копий",
            initialdir=current if os.path.isdir(current) else os.path.expanduser("~"),
            parent=self.root,
        )
        if not new_path:
            return
        # Пробная запись + файл-маркер. Маркер пишется на реально подключённый
        # диск; если позже диск окажется не смонтирован, копирование остановится
        # с ошибкой вместо тихой записи в пустую папку на системном диске.
        if not ensure_dest_marker(new_path):
            messagebox.showerror(
                "Ошибка",
                f"Папка недоступна для записи:\n{new_path}\n\n"
                f"Убедитесь, что диск подключён и смонтирован.")
            return
        cfg = _load_config()
        for key in ("backup_dest", "backup_mount_relpath", "backup_fs_uuid", "backup_device_serial"):
            cfg.pop(key, None)
        cfg.update(describe_dest_path(new_path))
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

    def _device_label(self, dev_id):
        """Human-facing label for a device: its custom name if set, else the
        bare number. The backup folder is named Device{id} regardless and is
        never renamed."""
        conn = self._get_db()
        try:
            row = conn.execute("SELECT name FROM devices WHERE id = ?", (int(dev_id),)).fetchone()
        finally:
            conn.close()
        name = (row[0] if row else "") or ""
        return name if name else str(dev_id)

    def _refresh_search_filters(self):
        conn = self._get_db()
        try:
            devices = conn.execute("SELECT id, name FROM devices ORDER BY id").fetchall()
            people = conn.execute("SELECT DISTINCT person FROM devices WHERE person != '' ORDER BY person").fetchall()
            self._device_filter_ids = {
                (r[1] if r[1] else str(r[0])): r[0] for r in devices
            }
            dev_list = [""] + list(self._device_filter_ids.keys())
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
        dev_sel = self.search_device.get()
        params_snapshot = {
            "dt_from": self._get_dt_from(),
            "dt_to": self._get_dt_to(),
            "dev_id": self._device_filter_ids.get(dev_sel) if dev_sel else None,
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
                    SELECT d.id, d.person, d.name, b.dest_path
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
                if p["dev_id"] is not None:
                    sql += " AND d.id = ?"
                    params.append(p["dev_id"])
                if p["person"]:
                    sql += " AND d.person = ?"
                    params.append(p["person"])
                sessions = conn.execute(sql, params).fetchall()
            finally:
                conn.close()

            ft = p["filetype"]
            fn_filter = p["filename"]

            seen_paths = set()
            for dev_id, person, dev_name, dest_path in sessions:
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
                            "device": dev_name if dev_name else str(dev_id),
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
        system = platform.system()
        if system in ("Windows", "Darwin"):
            try:
                if system == "Windows":
                    os.startfile(path)
                else:
                    subprocess.Popen(["open", path])
            except Exception as e:
                messagebox.showerror("Ошибка", f"Не удалось открыть файл:\n{e}")
            return

        # Linux/kiosk: xdg-open often has no MIME handler inside the container
        # and exits non-zero without any visible feedback, so try real players
        # first and verify the launched process didn't die immediately.
        ext = os.path.splitext(path)[1].lower()
        if ext in VIDEO_EXTS:
            candidates = [
                ["mpv", "--keep-open=yes"],
                ["ffplay", "-autoexit"],
                ["vlc"],
                ["totem"],
                ["xdg-open"],
            ]
        else:
            candidates = [["xdg-open"], ["eog"], ["feh"]]
        for cmd in candidates:
            exe = shutil.which(cmd[0])
            if not exe:
                continue
            try:
                proc = subprocess.Popen(
                    [exe, *cmd[1:], path],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                )
            except Exception:
                continue
            try:
                rc = proc.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                return  # still running after a second — assume it opened
            if rc == 0:
                return
        messagebox.showerror(
            "Ошибка",
            "Не найдено приложение, способное открыть файл:\n"
            f"{path}\n\n"
            "Установите видеоплеер (mpv/vlc) или настройте xdg-open.",
        )

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
        # In kiosk mode the WM may not draw window decorations, so the viewer
        # needs its own way to close.
        win.bind("<Escape>", lambda _e: win.destroy())

        photo = None
        if _HAVE_PIL:
            try:
                img = Image.open(path)
                img.thumbnail((w - 20, h - 90))
                photo = _ImageTk.PhotoImage(img)
            except Exception:
                photo = None
        if photo is None:
            try:
                photo = tk.PhotoImage(file=path)
            except Exception:
                win.destroy()
                self._open_externally(path)
                return

        lbl = tk.Label(win, image=photo, bg=self.C["bg_app"])
        lbl.image = photo
        lbl.pack(expand=True)
        bottom = tk.Frame(win, bg=self.C["bg_app"])
        bottom.pack(fill="x", pady=4)
        tk.Label(bottom, text=path, fg=self.C["fg_muted"], bg=self.C["bg_app"],
                 font=("Segoe UI", 9)).pack(side="left", padx=8)
        ttk.Button(bottom, text="Закрыть", command=win.destroy).pack(side="right", padx=8)

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
            for row in conn.execute("SELECT id, serial, label, name, person, first_seen, last_seen FROM devices ORDER BY id"):
                self.dev_tree.insert("", "end", values=(
                    row[0], row[1], row[2], row[3], row[4], row[5][:19], row[6][:19],
                ))
        finally:
            conn.close()

    def _on_device_select(self, _e):
        sel = self.dev_tree.selection()
        if sel:
            vals = self.dev_tree.item(sel[0], "values")
            self.edit_dev_id.delete(0, "end")
            self.edit_dev_id.insert(0, vals[0])
            self.edit_name.delete(0, "end")
            self.edit_name.insert(0, vals[3])
            self.edit_person.delete(0, "end")
            self.edit_person.insert(0, vals[4])

    def _rename_device(self):
        dev_id = self.edit_dev_id.get().strip()
        name = self.edit_name.get().strip()
        if not dev_id:
            messagebox.showwarning("Ошибка", "Выберите устройство из списка")
            return
        conn = self._get_db()
        try:
            conn.execute("UPDATE devices SET name = ? WHERE id = ?", (name, int(dev_id)))
            conn.commit()
            messagebox.showinfo("Готово", f"Устройство {dev_id} переименовано в {name or '(без имени)'}")
            self._refresh_devices()
            self._refresh_search_filters()
        except Exception as e:
            messagebox.showerror("Ошибка", str(e))
        finally:
            conn.close()

    def _assign_person(self):
        dev_id = self.edit_dev_id.get().strip()
        person = self.edit_person.get().strip()
        if not dev_id:
            messagebox.showwarning("Ошибка", "Выберите устройство из списка")
            return
        label = self._device_label(dev_id)
        conn = self._get_db()
        try:
            conn.execute("UPDATE devices SET person = ? WHERE id = ?", (person, int(dev_id)))
            conn.commit()
            messagebox.showinfo("Готово", f"{label} назначен на {person or '(не указан)'}")
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
            messagebox.showwarning("Ошибка", "Некорректный номер устройства")
            return

        label = self._device_label(dev_id)
        dev_dir = os.path.join(get_dest_base(), f"Device{dev_id}")
        if not os.path.isdir(dev_dir):
            messagebox.showinfo("Нет данных", f"Папка устройства {label} не найдена")
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
            messagebox.showinfo("Нет видео", f"В {label} нет видеофайлов")
            return

        size_mb = total_size / (1024 * 1024)
        if not messagebox.askyesno(
            "Подтверждение",
            f"Удалить {len(videos)} видеофайлов "
            f"({size_mb:.1f} МБ) из папки {label}?\n\n"
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
                    "state": {"scanning": "Сканирование", "copying": "Копирование", "done": "Готово", "error": "Ошибка"}.get(state, state),
                    "state_raw": state,
                    "progress": f"{pct}% ({self._fmt_size(current)} / {self._fmt_size(total)})" if total else msg,
                    "files": str(current) if state == "copying" else "",
                    "size": self._fmt_size(total),
                    "message": msg,
                    "devname": devname,
                }
                self._refresh_workers()
        except queue.Empty:
            pass

        # Выполняется на каждом тике (раз в 200мс) независимо от того, было ли
        # что-то в очереди — один большой файл может копироваться минутами без
        # единого сообщения в очереди, и маркер не должен за это время устареть.
        if _is_busy(self.workers_data):
            touch_copying_marker()

        try:
            self.root.after(POLL_MS, self._poll_queue)
        except tk.TclError:
            pass

    def _refresh_workers(self):
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
            elif state == "Ошибка":
                bg = "#dc2626"
                preview_text = data["device"]
                status_text = data.get("message", "Ошибка")
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
