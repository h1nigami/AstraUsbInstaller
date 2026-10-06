import os
import sys


def main():
    from usb_monitor import update_log_location
    update_log_location(start=True)
    if os.environ.get("DISPLAY") or sys.platform == "win32":
        try:
            from gui import launch, tk
        except ImportError as e:
            print(f"Интерфейс недоступен: {e}", flush=True)
        else:
            try:
                launch()
                return
            except tk.TclError as e:  # нет дисплея или X11 не пускает
                print(f"Интерфейс не запустился: {e}", flush=True)

    from usb_monitor import monitor_usb
    monitor_usb()


if __name__ == "__main__":
    main()
