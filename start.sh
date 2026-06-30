#!/bin/bash
# Запуск: GUI если доступен X11, иначе headless.
#
# Контейнер при автозапуске (systemd / restart:always) часто стартует раньше,
# чем поднимется графическая сессия пользователя, поэтому USB-монитор запускаем
# в фоне сразу, а появления X11 ждём в цикле и поднимаем GUI, как только сможем.
#
# Авторизация X11 из контейнера — главная сложность и причина «контейнер есть,
# а GUI нет»:
#   * путь к cookie рабочей сессии (XAUTHORITY) динамический и неизвестен на
#     момент создания контейнера, поэтому статически смонтированный
#     /root/.Xauthority часто не подходит;
#   * при НЕВЕРНОМ cookie клиенты X печатают "unable to open display" — ровно то
#     же, что и при ОТСУТСТВУЮЩЕМ сервере. По тексту ошибки отличить «сервера
#     ещё нет» от «cookie не тот» нельзя, поэтому старая версия считала живой
#     сервер недоступным и никогда не запускала GUI.
#
# Контейнер работает с pid:host, поэтому реальный cookie сессии находим через
# /proc/<pid>/{environ,cmdline,root}, копируем локально и ЧЕСТНО проверяем
# подключение через xdpyinfo, а не по тексту ошибки.

export DISPLAY="${DISPLAY:-:0}"
LOCAL_XAUTH="/tmp/.container_xauth"

log() { echo "[start.sh] $*"; }

_dispnum() { local d="${DISPLAY%%.*}"; printf '%s' "${d#:}"; }

_socket_ready() { [ -S "/tmp/.X11-unix/X$(_dispnum)" ]; }

# Реально ли открывается дисплей с текущим $XAUTHORITY?
_can_connect() {
    command -v xdpyinfo >/dev/null 2>&1 || return 0   # нет инструмента — оптимистично
    xdpyinfo -display "$DISPLAY" >/dev/null 2>&1
}

# Печатает пути к cookie-файлам реальной сессии, доступные через ФС хоста
# (/proc/<pid>/root работает благодаря pid:host + privileged).
_xauth_candidates() {
    local f pid root content val home args
    # Процессы рабочего стола: их XAUTHORITY и ~/.Xauthority
    for f in /proc/[0-9]*/environ; do
        content=$(tr '\0' '\n' < "$f" 2>/dev/null) || continue
        printf '%s\n' "$content" | grep -Eq "^DISPLAY=${DISPLAY}(\.[0-9]+)?$" || continue
        pid=${f#/proc/}; pid=${pid%/environ}
        root="/proc/${pid}/root"
        val=$(printf '%s\n' "$content" | sed -n 's/^XAUTHORITY=//p' | head -1)
        [ -n "$val" ] && [ -f "${root}${val}" ] && printf '%s\n' "${root}${val}"
        home=$(printf '%s\n' "$content" | sed -n 's/^HOME=//p' | head -1)
        [ -n "$home" ] && [ -f "${root}${home}/.Xauthority" ] && printf '%s\n' "${root}${home}/.Xauthority"
    done
    # X-сервер дисплей-менеджера: cookie передаётся аргументом -auth
    for f in /proc/[0-9]*/cmdline; do
        args=$(tr '\0' '\n' < "$f" 2>/dev/null) || continue
        printf '%s\n' "$args" | grep -q '^-auth$' || continue
        pid=${f#/proc/}; pid=${pid%/cmdline}
        root="/proc/${pid}/root"
        val=$(printf '%s\n' "$args" | grep -A1 '^-auth$' | tail -1)
        [ -n "$val" ] && [ -f "${root}${val}" ] && printf '%s\n' "${root}${val}"
    done
}

# Настраивает рабочий XAUTHORITY. Возвращает 0, если подключение к дисплею есть.
_setup_display_auth() {
    local cand
    # 1) cookie реальной сессии, найденный через /proc (самый надёжный путь)
    while IFS= read -r cand; do
        [ -z "$cand" ] && continue
        cp -f "$cand" "$LOCAL_XAUTH" 2>/dev/null || continue
        chmod 600 "$LOCAL_XAUTH" 2>/dev/null
        export XAUTHORITY="$LOCAL_XAUTH"
        if _can_connect; then
            log "X11 cookie: $cand"
            return 0
        fi
    done < <(_xauth_candidates)

    # 2) статически смонтированный cookie (если подошёл)
    if [ -f /root/.Xauthority ]; then
        export XAUTHORITY=/root/.Xauthority
        _can_connect && { log "X11 cookie: /root/.Xauthority"; return 0; }
    fi

    # 3) доступ открыт через `xhost +local:` — cookie вообще не нужен
    unset XAUTHORITY
    _can_connect && { log "X11 доступен без cookie (xhost)"; return 0; }

    return 1
}

# Явный headless-режим (DISPLAY намеренно пуст) — без цикла ожидания GUI.
if [ -z "${DISPLAY// /}" ]; then
    log "DISPLAY не задан — headless"
    exec python3 /app/usb_monitor.py
fi

log "DISPLAY=$DISPLAY — USB-монитор в фоне, ждём доступности X11 для GUI"
python3 /app/usb_monitor.py &
MONITOR_PID=$!

while true; do
    if _socket_ready && _setup_display_auth; then
        log "X11 доступен — останавливаем фоновый монитор, запускаем GUI"
        kill "$MONITOR_PID" 2>/dev/null
        wait "$MONITOR_PID" 2>/dev/null

        # Запускаем GUI напрямую (минуя fallback в main.py).
        python3 -c "from gui import launch; launch()"
        rc=$?
        log "GUI завершился (код $rc)"
        if [ "$rc" -eq 0 ]; then
            # Штатный выход по паролю (окно закрылось без ошибки) — НЕ
            # перезапускаем GUI, иначе защита выхода в kiosk-режиме бесполезна.
            # Остаёмся в headless-мониторинге, чтобы контейнер продолжал бэкапы.
            log "Штатный выход из GUI — переходим в headless-мониторинг"
            exec python3 /app/usb_monitor.py
        fi
        # Ненулевой код = аварийное завершение (например, пропала X-сессия):
        # поднимаем фоновый монитор и пробуем поднять GUI снова.
        log "Аварийное завершение GUI — возвращаемся к фоновому монитору"
        python3 /app/usb_monitor.py &
        MONITOR_PID=$!
    fi
    sleep 5
done
