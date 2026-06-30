#!/bin/bash
# Auto-detect: use GUI only if X11 is actually reachable.
# On manual launch $DISPLAY is inherited from the shell.
# On autostart (systemd / restart:always) $DISPLAY defaults to :0 (set in
# docker-compose.yml); we wait up to 90 s for the X server to come up before
# falling back to headless so the GUI starts after the desktop logs in.
export XAUTHORITY="${XAUTHORITY:-/root/.Xauthority}"

_x11_reachable() {
    # Check socket existence first — xdpyinfo can fail due to XAUTHORITY
    # mismatch even when the server is running, causing false negatives.
    local dispnum="${DISPLAY%%.*}"; dispnum="${dispnum#:}"
    [ -S "/tmp/.X11-unix/X${dispnum}" ] || return 1
    # If xdpyinfo is available, use it as a secondary auth check but don't
    # fail on auth errors (exit code 1 from auth vs. exit code from no server).
    if command -v xdpyinfo &>/dev/null; then
        local out
        out=$(xdpyinfo -display "$DISPLAY" 2>&1) && return 0
        # "unable to open display" means no server; auth errors still mean server is up
        echo "$out" | grep -qi "unable to open display" && return 1
    fi
    return 0
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
