#!/bin/bash
# Auto-detect: use GUI only if X11 is actually reachable
if [ -n "$DISPLAY" ]; then
    if command -v xdpyinfo &>/dev/null; then
        if xdpyinfo -display "$DISPLAY" &>/dev/null 2>&1; then
            exec python3 /app/main.py
        fi
    elif [ -S /tmp/.X11-unix/X0 ]; then
        exec python3 /app/main.py
    fi
    echo "DISPLAY=$DISPLAY set but X11 unreachable — falling back to headless"
fi
exec python3 /app/usb_monitor.py
