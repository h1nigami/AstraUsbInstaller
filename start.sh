#!/bin/bash
# Auto-detect GUI vs headless.
# On manual launch $DISPLAY is inherited from the shell.
# On autostart (systemd / restart:always) $DISPLAY defaults to :0 (docker-compose.yml).
#
# Strategy: start USB monitor in the background immediately so devices are
# handled even before the desktop appears, then keep polling for X11.
# When the display becomes reachable, stop the background monitor and hand
# off to the full GUI (which runs its own monitor thread).
export XAUTHORITY="${XAUTHORITY:-/root/.Xauthority}"

_x11_reachable() {
    # Socket existence is the primary signal — xdpyinfo can fail on auth
    # mismatch even when the server is running, producing false negatives.
    local dispnum="${DISPLAY%%.*}"; dispnum="${dispnum#:}"
    [ -S "/tmp/.X11-unix/X${dispnum}" ] || return 1
    if command -v xdpyinfo &>/dev/null; then
        local out
        out=$(xdpyinfo -display "$DISPLAY" 2>&1) && return 0
        # "unable to open display" = no server; any other error = server up, auth issue
        echo "$out" | grep -qi "unable to open display" && return 1
    fi
    return 0
}

if [ -z "$DISPLAY" ]; then
    echo "DISPLAY not set — headless mode"
    exec python3 /app/usb_monitor.py
fi

# Start USB monitor in the background immediately so backups work even
# before the desktop is ready.
echo "Starting headless USB monitor in background (DISPLAY=$DISPLAY)..."
python3 /app/usb_monitor.py &
MONITOR_PID=$!

# Poll for X11 indefinitely. When it appears, switch to full GUI.
echo "Waiting for X11 on $DISPLAY..."
while true; do
    if _x11_reachable; then
        echo "X11 reachable — stopping background monitor and launching GUI"
        kill "$MONITOR_PID" 2>/dev/null
        wait "$MONITOR_PID" 2>/dev/null
        exec python3 /app/main.py
    fi
    sleep 5
done
