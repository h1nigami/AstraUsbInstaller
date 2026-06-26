import os
import sys


def main():
    if os.environ.get("DISPLAY") or sys.platform == "win32":
        try:
            from gui import launch
            launch()
            return
        except Exception as e:
            print(f"GUI failed: {e}", flush=True)

    from usb_monitor import monitor_usb
    monitor_usb()


if __name__ == "__main__":
    main()
