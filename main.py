import os
import sys

if os.environ.get("DISPLAY") or sys.platform == "win32":
    from gui import launch
    launch()
else:
    from usb_monitor import monitor_usb
    monitor_usb()
