#!/bin/bash
# Auto-detect: use GUI only if X11 is actually reachable.
# DISPLAY is inherited from the launching shell: set (e.g. :1) on the desktop
# -> try GUI; empty over SSH -> headless. The X11 cookie is mounted at
# /root/.Xauthority by docker-compose; fall back to the conventional path.
export XAUTHORITY="${XAUTHORITY:-/root/.Xauthority}"
if [ -n "$DISPLAY" ]; then
    if command -v xdpyinfo &>/dev/null; then
        if xdpyinfo -display "$DISPLAY" &>/dev/null; then
            echo "X11 reachable on $DISPLAY (XAUTHORITY=$XAUTHORITY) — starting GUI"
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
