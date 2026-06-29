#!/bin/bash
# Auto-detect: use GUI only if X11 is actually reachable
if [ -n "$DISPLAY" ]; then
    if command -v xdpyinfo &>/dev/null; then
        if xdpyinfo -display "$DISPLAY" &>/dev/null 2>&1; then
            exec python3 /app/main.py
        fi
    else
        # xdpyinfo absent — check the socket that matches the actual DISPLAY
        _dispnum="${DISPLAY%%.*}"; _dispnum="${_dispnum#:}"
        if [ -S "/tmp/.X11-unix/X${_dispnum}" ]; then
            exec python3 /app/main.py
        fi
    fi
    echo "DISPLAY=$DISPLAY set but X11 unreachable — falling back to headless"
fi
exec python3 /app/usb_monitor.py
