#!/bin/bash
# Auto-detect: use GUI only if X11 is actually reachable.
# On manual launch $DISPLAY is inherited from the shell.
# On autostart (systemd / restart:always) $DISPLAY defaults to :0 (set in
# docker-compose.yml); we wait up to 90 s for the X server to come up before
# falling back to headless so the GUI starts after the desktop logs in.
export XAUTHORITY="${XAUTHORITY:-/root/.Xauthority}"

_x11_reachable() {
    if command -v xdpyinfo &>/dev/null; then
        xdpyinfo -display "$DISPLAY" &>/dev/null
    else
        local dispnum="${DISPLAY%%.*}"; dispnum="${dispnum#:}"
        [ -S "/tmp/.X11-unix/X${dispnum}" ]
    fi
}

if [ -n "$DISPLAY" ]; then
    if _x11_reachable; then
        echo "X11 reachable on $DISPLAY (XAUTHORITY=$XAUTHORITY) — starting GUI"
        exec python3 /app/main.py
    fi

    # Wait for X server to become available (e.g. autostart before desktop login)
    echo "DISPLAY=$DISPLAY set but X11 not yet reachable — waiting up to 90 s..."
    for i in $(seq 1 45); do
        sleep 2
        if _x11_reachable; then
            echo "X11 reachable after $((i * 2)) s — starting GUI"
            exec python3 /app/main.py
        fi
        echo "  still waiting ($((i * 2)) / 90 s)..."
    done

    echo "X11 unreachable after 90 s — falling back to headless"
fi

exec python3 /app/usb_monitor.py
