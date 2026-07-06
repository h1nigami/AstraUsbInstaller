#!/bin/bash
# Нативный запуск БЕЗ Docker: GUI, если доступен X11, иначе headless.
# Запускается systemd-сервисом от root (root нужен приложению для mount/umount
# флешек). Адаптация докерного start.sh: та же логика ожидания X-сервера,
# поиска cookie сессии через /proc и честной проверки подключения xdpyinfo,
# только без /app и pid:host — нативно /proc и так хостовый.
#
# Сервис стартует раньше, чем поднимется графическая сессия пользователя,
# поэтому USB-монитор запускается в фоне сразу, а появления X11 ждём в цикле
# и поднимаем GUI, как только сможем.
#
# Авторизация X11 из-под root — главная сложность и причина «сервис есть,
# а GUI нет»:
#   * путь к cookie рабочей сессии (XAUTHORITY) динамический и неизвестен
#     заранее (у fly-dm свой путь, у lightdm свой);
#   * при НЕВЕРНОМ cookie клиенты X печатают "unable to open display" — ровно
#     то же, что и при ОТСУТСТВУЮЩЕМ сервере, поэтому по тексту ошибки «сервера
#     ещё нет» от «cookie не тот» не отличить. Проверяем подключение честно,
#     через xdpyinfo.

APP_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$APP_DIR"
LOCAL_XAUTH="/tmp/.astra_usb_xauth"

log() { echo "[start_native.sh] $*"; }

_dispnum() { local d="${DISPLAY%%.*}"; printf '%s' "${d#:}"; }

# Убеждается, что $DISPLAY указывает на живой X-сокет; если DISPLAY не задан
# (обычное дело под systemd) — берёт первый живой сокет из /tmp/.X11-unix.
_detect_display() {
    if [ -n "$DISPLAY" ] && [ -S "/tmp/.X11-unix/X$(_dispnum)" ]; then
        return 0
    fi
    local s
    for s in /tmp/.X11-unix/X*; do
        [ -S "$s" ] || continue
        export DISPLAY=":${s##*/X}"
        return 0
    done
    return 1
}

# Реально ли открывается дисплей с текущим $XAUTHORITY?
_can_connect() {
    command -v xdpyinfo >/dev/null 2>&1 || return 0   # нет инструмента — оптимистично
    xdpyinfo -display "$DISPLAY" >/dev/null 2>&1
}

# Печатает пути к cookie-файлам реальной сессии, найденные через /proc.
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

    # 2) root запускал X сам или доступ открыт через `xhost +` — cookie не нужен
    unset XAUTHORITY
    _can_connect && { log "X11 доступен без cookie"; return 0; }

    return 1
}

# Явный headless-режим по флагу (у systemd DISPLAY и так пуст, поэтому
# «пустой DISPLAY = headless» здесь не работает — дисплей мы ищем сами).
if [ "${APP_FORCE_HEADLESS:-0}" = "1" ]; then
    log "APP_FORCE_HEADLESS=1 — headless"
    exec python3 "$APP_DIR/usb_monitor.py"
fi

log "USB-монитор в фоне, ждём доступности X11 для GUI"
python3 "$APP_DIR/usb_monitor.py" &
MONITOR_PID=$!

while true; do
    if _detect_display && _setup_display_auth; then
        log "X11 доступен (DISPLAY=$DISPLAY) — останавливаем фоновый монитор, запускаем GUI"
        kill "$MONITOR_PID" 2>/dev/null
        wait "$MONITOR_PID" 2>/dev/null

        # Запускаем GUI напрямую (минуя fallback в main.py), чтобы ошибка
        # подключения к X дала ненулевой код, а не тихий headless.
        python3 -c "from gui import launch; launch()"
        rc=$?
        log "GUI завершился (код $rc)"
        if [ "$rc" -eq 0 ]; then
            # Штатный выход по паролю — НЕ перезапускаем GUI, иначе защита
            # выхода в kiosk-режиме бесполезна. Остаёмся в headless-мониторинге.
            log "Штатный выход из GUI — переходим в headless-мониторинг"
            exec python3 "$APP_DIR/usb_monitor.py"
        fi
        # Ненулевой код = аварийное завершение (например, пропала X-сессия):
        # поднимаем фоновый монитор и пробуем поднять GUI снова.
        log "Аварийное завершение GUI — возвращаемся к фоновому монитору"
        python3 "$APP_DIR/usb_monitor.py" &
        MONITOR_PID=$!
    fi
    sleep 5
done
