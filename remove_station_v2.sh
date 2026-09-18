#!/bin/bash
# Выключает кроссплатформенную станцию BestCam (версия 2) на этой машине.
#
# На части точек рядом с Python-версией оказалась вторая станция: два киоска
# делят экран и одни и те же карты, а какая из программ заберёт флешку — как
# повезёт. Скрипт зовут установщик и кнопка диагностики в интерфейсе, поэтому
# он живёт отдельным файлом: логика удаления должна быть описана один раз.
#
# Безвозвратно не удаляется ничего: каталог с базой и собранными записями
# остаётся рядом под именем с суффиксом .removed и датой, забрать оттуда
# данные можно в любой момент.
#
# ASTRA_ROOT — префикс путей: в бою пустой, в тестах ведёт во временный
# каталог. SUDO — пусто под root, иначе sudo.
set -e

remove_bestcam_station() {
    local root="${ASTRA_ROOT:-}"
    local units="$root/etc/systemd/system"
    local dir unit

    # Каталог спрашиваем у самой службы: на станции он может быть не там,
    # где его оставил бы установщик по умолчанию.
    # `|| dir=` обязательно: под set -e неудача подстановки (в контейнере
    # systemctl нет вовсе) иначе обрывает установку целиком.
    dir="$(systemctl show -p WorkingDirectory --value astra-usb-avalonia.service 2>/dev/null)" || dir=""
    [ -n "$dir" ] || dir="$root/opt/astra-usb-avalonia"

    [ -d "$dir" ] || [ -f "$units/astra-usb-avalonia.service" ] || return 0

    echo "--- Найдена станция BestCam — выключаем, чтобы киоски не делили экран"
    # Пакет снимается через apt: иначе dpkg продолжает считать его
    # установленным и службы вернёт первое же обновление системы.
    if dpkg -s bestcam-station >/dev/null 2>&1; then
        $SUDO apt-get remove -y bestcam-station || true
    fi
    # Установки из архива (v2.0 и v2.0.1) пакета не оставляли — их юниты
    # убираются руками.
    for unit in astra-usb-avalonia.service astra-usb-avalonia-update.timer \
                astra-usb-avalonia-update.service; do
        $SUDO systemctl disable --now "$unit" 2>/dev/null || true
        $SUDO rm -f "$units/$unit"
    done
    $SUDO rm -f "$root/etc/udev/rules.d/99-astra-usb-avalonia-udisks.rules"
    $SUDO systemctl daemon-reload 2>/dev/null || true

    if [ -d "$dir" ]; then
        $SUDO mv "$dir" "$dir.removed.$(date +%Y%m%d-%H%M%S)"
        echo "  каталог станции сохранён рядом: $dir.removed.*"
    fi
}

# Запущен напрямую — выполняем. Подключён через source — только объявляем.
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    remove_bestcam_station
fi
