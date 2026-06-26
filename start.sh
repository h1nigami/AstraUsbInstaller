#!/bin/bash
# If DISPLAY is set (desktop environment), use python3 with tkinter for GUI
if [ -n "$DISPLAY" ]; then
    exec python3 /app/main.py
else
    exec python /app/main.py
fi
