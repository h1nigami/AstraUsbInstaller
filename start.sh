#!/bin/bash
# Auto-detect: use GUI if X11 socket is available
if [ -z "$DISPLAY" ] && [ -S /tmp/.X11-unix/X0 ]; then
    export DISPLAY=:0
fi

if [ -n "$DISPLAY" ]; then
    exec python3 /app/main.py
else
    exec python /app/main.py
fi
