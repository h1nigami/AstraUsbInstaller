#!/bin/bash
# Запуск: GUI если X11 доступен, иначе headless.
# При autostart (systemd / restart:always) X11 может подняться позже —
# запускаем USB-монитор в фоне немедленно, а GUI ждём бесконечно.
#
# Проблема при автостарте: XAUTHORITY-файл монтируется в docker-compose.yml
# по пути на момент создания контейнера, а не на момент входа пользователя.
# Решение: находим актуальный XAUTHORITY динамически через /proc (pid:host).
export XAUTHORITY="${XAUTHORITY:-/root/.Xauthority}"

_x11_reachable() {
    local dispnum="${DISPLAY%%.*}"; dispnum="${dispnum#:}"
    [ -S "/tmp/.X11-unix/X${dispnum}" ] || return 1
    if command -v xdpyinfo &>/dev/null; then
        local out
        out=$(xdpyinfo -display "$DISPLAY" 2>&1) && return 0
        # "unable to open display" = сервер не запущен; иначе — сервер есть, auth-ошибка
        echo "$out" | grep -qi "unable to open display" && return 1
    fi
    return 0
}

# Ищем XAUTHORITY из запущенных процессов рабочего стола.
# Работает потому что контейнер запущен с pid:host — /proc/<pid>/root
# даёт доступ к файловой системе хоста через пространство имён процесса.
_find_host_xauthority() {
    for env_file in /proc/[0-9]*/environ; do
        local pid content xauth_path host_path
        pid="${env_file%/environ}"; pid="${pid#/proc/}"
        content=$(tr '\0' '\n' < "$env_file" 2>/dev/null) || continue
        echo "$content" | grep -q "^DISPLAY=${DISPLAY}" || continue
        xauth_path=$(echo "$content" | grep "^XAUTHORITY=" | head -1)
        xauth_path="${xauth_path#XAUTHORITY=}"
        [ -z "$xauth_path" ] && continue
        host_path="/proc/${pid}/root${xauth_path}"
        [ -f "$host_path" ] && echo "$host_path" && return 0
    done
}

if [ -z "$DISPLAY" ]; then
    echo "DISPLAY не задан — headless"
    exec python3 /app/usb_monitor.py
fi

echo "DISPLAY=$DISPLAY — запускаем USB-монитор в фоне"
python3 /app/usb_monitor.py &
MONITOR_PID=$!

while true; do
    if _x11_reachable; then
        # Обновляем XAUTHORITY из реального сеанса рабочего стола
        host_xauth=$(_find_host_xauthority)
        if [ -n "$host_xauth" ]; then
            export XAUTHORITY="$host_xauth"
            echo "XAUTHORITY найден: $host_xauth"
        fi

        echo "X11 доступен — останавливаем фоновый монитор, запускаем GUI"
        kill "$MONITOR_PID" 2>/dev/null
        wait "$MONITOR_PID" 2>/dev/null

        # Запускаем GUI напрямую (минуя fallback в main.py).
        # Если Tkinter упадёт (ошибка auth и т.п.) — перезапустим монитор и повторим.
        python3 -c "from gui import launch; launch()"
        echo "GUI завершился (код $?) — перезапускаем монитор"
        python3 /app/usb_monitor.py &
        MONITOR_PID=$!
    fi
    sleep 5
done
