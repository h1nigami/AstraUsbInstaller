import os
import queue
import sqlite3
import threading
import tkinter as tk
from tkinter import ttk, messagebox
from datetime import datetime

from usb_monitor import monitor_usb, DB_PATH, _init_db

POLL_MS = 200


class App:
    def __init__(self):
        self.root = tk.Tk()
        self.root.title("USB Backup Manager")
        self.root.geometry("900x500")

        self.progress_queue = queue.Queue()
        self.stop_event = threading.Event()
        self.monitor_thread = None
        self.workers_data = {}

        _init_db()

        self._build_ui()
        self._poll_queue()
        self._start_monitor()

    def _build_ui(self):
        nb = ttk.Notebook(self.root)
        nb.pack(fill="both", expand=True)
        self._build_search_tab(nb)
        self._build_devices_tab(nb)
        self._build_workers_tab(nb)

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
        ttk.Button(edit_frame, text="Обновить список", command=self._refresh_devices).pack(side="right", padx=4)

        self.dev_tree.bind("<<TreeviewSelect>>", self._on_device_select)
        self._refresh_devices()

    def _build_workers_tab(self, nb):
        f = ttk.Frame(nb)
        nb.add(f, text="Загрузка")

        cols = ("device", "state", "progress", "files", "size", "message")
        self.work_tree = ttk.Treeview(f, columns=cols, show="headings", height=18)
        headings = {"device": "Устройство", "state": "Статус", "progress": "Прогресс",
                    "files": "Файлы", "size": "Размер", "message": "Сообщение"}
        for c in cols:
            self.work_tree.heading(c, text=headings[c])
            self.work_tree.column(c, width=120)
        self.work_tree.column("message", width=250)
        self.work_tree.pack(fill="both", expand=True, padx=5, pady=5)

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

    def _start_monitor(self):
        self.stop_event.clear()
        self.monitor_thread = threading.Thread(
            target=monitor_usb,
            args=(2, self.stop_event, self.progress_queue),
            daemon=True,
        )
        self.monitor_thread.start()

    def _poll_queue(self):
        try:
            while True:
                device_id, display_id, state, current, total, msg = self.progress_queue.get_nowait()
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
                }
                self._refresh_workers()
        except queue.Empty:
            pass
        self.root.after(POLL_MS, self._poll_queue)

    def _refresh_workers(self):
        for row in self.work_tree.get_children():
            self.work_tree.delete(row)
        for d in sorted(self.workers_data.values(), key=lambda x: x["device"]):
            self.work_tree.insert("", "end", values=(
                d["device"], d["state"], d["progress"], d["files"], d["size"], d["message"],
            ))

    def _fmt_size(self, b):
        for unit in ("B", "KB", "MB", "GB", "TB"):
            if b < 1024:
                return f"{b:.1f} {unit}"
            b /= 1024
        return f"{b:.1f} PB"

    def run(self):
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.root.mainloop()

    def _on_close(self):
        self.stop_event.set()
        self.root.destroy()


def launch():
    App().run()


if __name__ == "__main__":
    launch()
