#!/bin/bash
# Нативный запуск версии на Avalonia: дожидается графической сессии и
# запускает приложение. Пока пользователь не вошёл в сессию, оно просто ждёт.
#
# Скрипт повторяет проверенный запуск Python-версии и отличается от него одной
# строкой: чем запускать приложение. Логика поиска X-сессии оставлена как есть
# намеренно, она выстрадана на живых станциях, и упрощать её здесь означало бы
# заново собрать те же грабли.
#
# Запускается systemd-сервисом от root (root нужен приложению для mount/umount
# флешек). Логика ожидания X взята из проверенного докерного start.sh:
#
# Авторизация X11 из-под root — главная сложность и причина «сервис есть,
# а GUI нет»:
#   * путь к cookie рабочей сессии (XAUTHORITY) динамический и неизвестен
#     заранее (у fly-dm свой путь, у lightdm свой);
#   * при НЕВЕРНОМ cookie клиенты X печатают "unable to open display" — ровно
#     то же, что и при ОТСУТСТВУЮЩЕМ сервере, поэтому по тексту ошибки «сервера
#     ещё нет» от «cookie не тот» не отличить. Проверяем подключение честно,
#     через xdpyinfo.
# Cookie реальной сессии находим через /proc/<pid>/environ процессов рабочего
# стола и аргумент -auth X-сервера.

APP_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$APP_DIR"
LOCAL_XAUTH="/tmp/.astra_usb_xauth"

log() { echo "[start_native.sh avalonia] $*"; }

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
    command -v xdpyinfo >/dev/null 2>&1 || return 0   # нет инструмента, значит верим на слово
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

log "Ждём доступности X11 для запуска (проверка каждые 5 секунд)"
while true; do
    if _detect_display && _setup_display_auth; then
        log "X11 доступен (DISPLAY=$DISPLAY), запускаем приложение"
        "$APP_DIR/AstraUsb"
        rc=$?
        log "Приложение завершилось (код $rc)"
        if [ "$rc" -eq 0 ]; then
            # Штатный выход по паролю — останавливаем сервис (unit имеет
            # Restart=on-failure, поэтому systemd НЕ перезапустит его и
            # защита выхода в kiosk-режиме сохраняет смысл).
            log "Штатный выход по паролю — сервис останавливается"
            exit 0
        fi
        # Ненулевой код = аварийное завершение (например, пропала X-сессия):
        # ждём и пробуем поднять GUI снова.
        log "Аварийное завершение — повторный запуск через 5 секунд"
    fi
    sleep 5
done
