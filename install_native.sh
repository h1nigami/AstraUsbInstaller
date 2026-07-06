#!/bin/bash
# Установка USB Backup Manager на Astra Linux БЕЗ Docker.
#
# Что делает скрипт:
#   1. Ставит все зависимости через apt (python3, tkinter, утилиты монтирования).
#   2. Копирует приложение в /opt/astra-usb-monitor.
#   3. Ставит один systemd-сервис (start_native.sh), который ждёт X-сервер,
#      находит cookie сессии через /proc и поднимает GUI, как только
#      графическая сессия готова. Мониторинг USB работает внутри GUI
#      (gui.py сам запускает monitor_usb фоновым потоком). Парольный
#      «Выход» из GUI останавливает сервис до следующей перезагрузки.
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
# На киоске без интернета/репозиториев apt может не работать — это не повод
# прерывать установку: ниже мы проверяем фактическое наличие python3/tkinter,
# и падаем только если их реально нет.
$SUDO apt-get update || echo "ВНИМАНИЕ: apt-get update не сработал (нет сети/репозиториев?), пробуем продолжить"

# Критичные пакеты — без них приложение не работает.
$SUDO apt-get install -y python3 python3-tk util-linux mount udev \
    || echo "ВНИМАНИЕ: apt не смог поставить пакеты, проверяем что уже есть в системе"

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

# Проверяем, что всё критичное реально есть в системе (независимо от того,
# поставил его apt сейчас или оно уже было).
if ! command -v python3 >/dev/null 2>&1; then
    echo "ОШИБКА: нет python3 и apt не смог его установить."
    exit 1
fi
if ! python3 -c "import tkinter" 2>/dev/null; then
    echo "ОШИБКА: нет python3-tk (tkinter), GUI работать не будет."
    exit 1
fi
if ! command -v lsblk >/dev/null 2>&1; then
    echo "ОШИБКА: нет lsblk (пакет util-linux) — без него флешки не обнаруживаются."
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
Description=Astra USB Monitor (GUI, мониторинг USB работает внутри GUI)
After=multi-user.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/start_native.sh
# on-failure: парольный «Выход» из GUI завершает сервис с кодом 0, и systemd
# НЕ перезапускает его — иначе kiosk-выход был бы бессмысленным.
Restart=on-failure
RestartSec=5
Environment=PYTHONUNBUFFERED=1

[Install]
WantedBy=multi-user.target
EOF
$SUDO systemctl daemon-reload
$SUDO systemctl enable "$SERVICE_NAME.service"
$SUDO systemctl restart "$SERVICE_NAME.service"

# Не рапортуем «Готово», не убедившись, что сервис действительно жив.
sleep 3
if ! $SUDO systemctl is-active --quiet "$SERVICE_NAME.service"; then
    echo ""
    echo "ОШИБКА: сервис не запустился. Последние строки лога:"
    $SUDO journalctl -u "$SERVICE_NAME" -n 20 --no-pager 2>/dev/null || true
    echo ""
    echo "Соберите полную диагностику: sudo bash $SRC_DIR/diagnose.sh"
    exit 1
fi

echo ""
echo "=== Готово ==="
echo "Сервис запущен. GUI появится сам, как только будет доступна графическая"
echo "сессия — т.е. после входа пользователя в систему (и после перезагрузки"
echo "тоже). Мониторинг USB работает внутри GUI."
echo ""
echo "Статус:  systemctl status $SERVICE_NAME"
echo "Логи:    journalctl -u $SERVICE_NAME -f"
echo ""
echo "База и настройки: $APP_DIR/data"
echo "Папку назначения выбирайте в GUI на вкладке «Настройки» —"
echo "диски видны как в файловом менеджере, пробрасывать ничего не нужно."
echo ""
echo "Удаление: systemctl disable --now $SERVICE_NAME"
