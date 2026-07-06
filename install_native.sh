#!/bin/bash
# Установка USB Backup Manager на Astra Linux БЕЗ Docker.
#
# Что делает скрипт:
#   1. Ставит все зависимости через apt (python3, tkinter, утилиты монтирования).
#   2. Копирует приложение в /opt/astra-usb-monitor.
#   3. Ставит один systemd-сервис (start_native.sh), который сам:
#        - сразу запускает headless-мониторинг USB (бэкапы идут ещё до входа
#          пользователя в сессию);
#        - ждёт X-сервер, находит cookie сессии через /proc и поднимает GUI,
#          как только графическая сессия готова;
#        - после парольного «Выхода» из GUI возвращается в headless-режим.
#      Автозапуск рабочего стола (fly/.config/autostart) НЕ используется —
#      он в Astra срабатывает не во всех конфигурациях; systemd надёжнее,
#      и все логи видны через journalctl.
#
# Сервис работает от root: приложение само вызывает mount/umount для флешек
# (в Docker это решалось privileged). Запуск без Docker означает, что
# приложение видит те же точки монтирования, что и файловый менеджер —
# никакие volume пробрасывать не нужно.
#
# Запускать: ./install_native.sh (от обычного пользователя, sudo спросится
# сам) или sudo ./install_native.sh.
set -e

cd "$(dirname "$0")"
SRC_DIR="$(pwd)"
APP_DIR="/opt/astra-usb-monitor"
SERVICE_NAME="astra-usb-monitor"

if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
else
    SUDO="sudo"
fi

echo "=== Установка USB Backup Manager (без Docker) ==="

# --- 1. Зависимости ----------------------------------------------------------
echo ""
echo "--- Установка системных пакетов..."
export DEBIAN_FRONTEND=noninteractive
$SUDO apt-get update

# Критичные пакеты — без них приложение не работает.
$SUDO apt-get install -y python3 python3-tk util-linux mount udev

# Необязательные пакеты. x11-utils даёт xdpyinfo — им честно проверяется
# доступность X-сервера (без него запуск GUI будет пробоваться вслепую).
# Остальное — поддержка NTFS/exFAT-флешек и pip для rich. Имена различаются
# между версиями Astra, поэтому ставим по одному и не падаем, если пакета нет.
for pkg in x11-utils ntfs-3g exfat-fuse exfatprogs exfat-utils python3-pip python3-rich; do
    if $SUDO apt-get install -y "$pkg" 2>/dev/null; then
        echo "  установлен: $pkg"
    else
        echo "  пропущен (нет в репозитории): $pkg"
    fi
done

# rich — только красивый прогресс в консоли, GUI без него работает.
if ! python3 -c "import rich" 2>/dev/null; then
    if command -v pip3 >/dev/null 2>&1; then
        $SUDO pip3 install rich 2>/dev/null \
            || $SUDO pip3 install --break-system-packages rich 2>/dev/null \
            || echo "  rich не установился — не страшно, приложение работает без него"
    fi
fi

# Проверяем, что tkinter реально импортируется.
if ! python3 -c "import tkinter" 2>/dev/null; then
    echo "ОШИБКА: python3-tk не установился, GUI работать не будет."
    exit 1
fi

# --- 2. Останавливаем docker-версию и артефакты старых установок -------------
echo ""
echo "--- Очистка предыдущих вариантов установки..."
# Docker-версия (install.sh): контейнер и наша прошлая нативная версия не
# должны монтировать флешки одновременно с новым сервисом.
if command -v docker >/dev/null 2>&1; then
    $SUDO docker rm -f astra-usb-monitor 2>/dev/null \
        && echo "  остановлен docker-контейнер astra-usb-monitor" || true
fi
# Артефакты прошлой версии этого скрипта (автозапуск через рабочий стол + sudo).
$SUDO rm -f /etc/sudoers.d/astra-usb-monitor \
    "$APP_DIR/start_gui.sh" "$APP_DIR/autostart_gui.sh"
for d in /home/*/.config/autostart /root/.config/autostart; do
    if [ -f "$d/usb-backup-manager.desktop" ]; then
        $SUDO rm -f "$d/usb-backup-manager.desktop"
        echo "  удалён автозапуск рабочего стола: $d/usb-backup-manager.desktop"
    fi
done

# --- 3. Копируем приложение в /opt -------------------------------------------
echo "--- Установка приложения в $APP_DIR..."
$SUDO mkdir -p "$APP_DIR"
$SUDO cp "$SRC_DIR/main.py" "$SRC_DIR/gui.py" "$SRC_DIR/usb_monitor.py" \
         "$SRC_DIR/start_native.sh" "$APP_DIR/"
$SUDO chmod 755 "$APP_DIR/start_native.sh"
# data/ (база и конфиг) при переустановке не трогаем.
$SUDO mkdir -p "$APP_DIR/data" "$APP_DIR/USB_Backups"

# --- 4. Systemd-сервис --------------------------------------------------------
echo "--- Настройка systemd-сервиса..."
$SUDO tee "/etc/systemd/system/$SERVICE_NAME.service" > /dev/null << EOF
[Unit]
Description=Astra USB Monitor (headless + GUI при появлении X-сессии)
After=multi-user.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/start_native.sh
Restart=always
RestartSec=5
Environment=PYTHONUNBUFFERED=1
# Раскомментируйте, чтобы принудительно работать без GUI:
#Environment=APP_FORCE_HEADLESS=1

[Install]
WantedBy=multi-user.target
EOF
$SUDO systemctl daemon-reload
$SUDO systemctl enable "$SERVICE_NAME.service"
$SUDO systemctl restart "$SERVICE_NAME.service"

echo ""
echo "=== Готово ==="
echo "Сервис запущен. Headless-мониторинг уже работает; GUI появится сам,"
echo "как только будет доступна графическая сессия (и после перезагрузки тоже)."
echo ""
echo "Статус:  systemctl status $SERVICE_NAME"
echo "Логи:    journalctl -u $SERVICE_NAME -f"
echo ""
echo "База и настройки: $APP_DIR/data"
echo "Папку назначения выбирайте в GUI на вкладке «Настройки» —"
echo "диски видны как в файловом менеджере, пробрасывать ничего не нужно."
echo ""
echo "Удаление: systemctl disable --now $SERVICE_NAME"
