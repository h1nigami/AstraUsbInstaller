#!/bin/bash
# Установка USB Backup Manager на Astra Linux БЕЗ Docker.
#
# Что делает скрипт:
#   1. Ставит все зависимости через apt (python3, tkinter, утилиты монтирования).
#   2. Копирует приложение в /opt/astra-usb-monitor.
#   3. Настраивает sudoers-правило: приложению нужен root, т.к. оно само
#      вызывает mount/umount для флешек (в Docker это решалось privileged).
#   4. Автозапуск GUI при входе в графическую сессию (~/.config/autostart).
#   5. Systemd-сервис headless-мониторинга: работает до входа в сессию и
#      после выхода из GUI по паролю; при запуске GUI останавливается,
#      чтобы не было двух копий, монтирующих одни и те же флешки.
#
# Запуск без Docker означает, что приложение видит те же точки монтирования,
# что и файловый менеджер — никакие volume пробрасывать не нужно.
#
# Запускать от обычного пользователя киоска (sudo спросится сам)
# или через sudo — тогда автозапуск настроится для пользователя,
# вызвавшего sudo.
set -e

cd "$(dirname "$0")"
SRC_DIR="$(pwd)"
APP_DIR="/opt/astra-usb-monitor"
SERVICE_NAME="astra-usb-monitor"

# --- Определяем пользователя киоска и способ повышения прав -----------------
if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
    KIOSK_USER="${SUDO_USER:-root}"
else
    SUDO="sudo"
    KIOSK_USER="$USER"
fi
if [ "$KIOSK_USER" = "root" ]; then
    echo "ОШИБКА: не удалось определить пользователя киоска."
    echo "Запустите скрипт от обычного пользователя: ./install_native.sh"
    exit 1
fi
KIOSK_HOME="$(getent passwd "$KIOSK_USER" | cut -d: -f6)"
echo "=== Установка USB Backup Manager (без Docker) ==="
echo "Пользователь киоска: $KIOSK_USER ($KIOSK_HOME)"

# --- 1. Зависимости ----------------------------------------------------------
echo ""
echo "--- Установка системных пакетов..."
export DEBIAN_FRONTEND=noninteractive
$SUDO apt-get update

# Критичные пакеты — без них приложение не работает.
$SUDO apt-get install -y python3 python3-tk util-linux mount udev

# Необязательные пакеты: поддержка NTFS/exFAT-флешек, x11-utils для
# диагностики, pip для rich. Имена различаются между версиями Astra,
# поэтому ставим по одному и не падаем, если пакета нет в репозитории.
for pkg in ntfs-3g exfat-fuse exfatprogs exfat-utils x11-utils python3-pip python3-rich; do
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

# --- 2. Копируем приложение в /opt ------------------------------------------
echo ""
echo "--- Установка приложения в $APP_DIR..."
$SUDO mkdir -p "$APP_DIR"
$SUDO cp "$SRC_DIR/main.py" "$SRC_DIR/gui.py" "$SRC_DIR/usb_monitor.py" "$APP_DIR/"
# data/ (база и конфиг) при переустановке не трогаем.
$SUDO mkdir -p "$APP_DIR/data" "$APP_DIR/USB_Backups"

# --- 3. Скрипт запуска GUI (выполняется от root) -----------------------------
$SUDO tee "$APP_DIR/start_gui.sh" > /dev/null << 'EOF'
#!/bin/bash
# Запускается от root через sudo из автозапуска сессии.
# Повторяет логику докерного start.sh:
#   - на время работы GUI останавливаем headless-сервис (иначе две копии
#     будут одновременно монтировать и копировать одни и те же флешки);
#   - выход с кодом 0 — это парольный "Выход" из киоска: GUI не
#     перезапускаем, возвращаем headless-мониторинг;
#   - ненулевой код — падение (например, умерла X-сессия): перезапускаем.
cd /opt/astra-usb-monitor
systemctl stop astra-usb-monitor.service 2>/dev/null || true
while true; do
    python3 main.py
    code=$?
    if [ "$code" -eq 0 ]; then
        break
    fi
    echo "GUI завершился с кодом $code, перезапуск через 5 секунд..."
    sleep 5
done
systemctl start astra-usb-monitor.service 2>/dev/null || true
EOF
$SUDO chmod 755 "$APP_DIR/start_gui.sh"

# --- 4. Обёртка автозапуска (выполняется от пользователя сессии) -------------
$SUDO tee "$APP_DIR/autostart_gui.sh" > /dev/null << 'EOF'
#!/bin/bash
# Вызывается из автозапуска графической сессии от имени пользователя.
# Разрешаем root'у подключаться к X-серверу сессии и передаём ему
# DISPLAY/XAUTHORITY, т.к. sudo очищает окружение.
export XAUTHORITY="${XAUTHORITY:-$HOME/.Xauthority}"
xhost +SI:localuser:root >/dev/null 2>&1 || true
exec sudo -n DISPLAY="$DISPLAY" XAUTHORITY="$XAUTHORITY" /opt/astra-usb-monitor/start_gui.sh
EOF
$SUDO chmod 755 "$APP_DIR/autostart_gui.sh"

# --- 5. Sudoers: пользователю киоска можно запускать GUI от root без пароля --
echo ""
echo "--- Настройка sudoers..."
SUDOERS_FILE="/etc/sudoers.d/astra-usb-monitor"
echo "$KIOSK_USER ALL=(root) NOPASSWD:SETENV: $APP_DIR/start_gui.sh" | $SUDO tee "$SUDOERS_FILE" > /dev/null
$SUDO chmod 440 "$SUDOERS_FILE"
if ! $SUDO visudo -c -f "$SUDOERS_FILE" > /dev/null; then
    echo "ОШИБКА: некорректное sudoers-правило, откатываю."
    $SUDO rm -f "$SUDOERS_FILE"
    exit 1
fi

# --- 6. Systemd-сервис headless-мониторинга ----------------------------------
echo "--- Настройка systemd-сервиса..."
$SUDO tee "/etc/systemd/system/$SERVICE_NAME.service" > /dev/null << EOF
[Unit]
Description=Astra USB Monitor (headless, до входа в графическую сессию)
After=multi-user.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=/usr/bin/python3 $APP_DIR/usb_monitor.py
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
$SUDO systemctl daemon-reload
$SUDO systemctl enable "$SERVICE_NAME.service"
$SUDO systemctl restart "$SERVICE_NAME.service"

# --- 7. Автозапуск GUI при входе в сессию ------------------------------------
echo "--- Настройка автозапуска GUI..."
AUTOSTART_DIR="$KIOSK_HOME/.config/autostart"
$SUDO mkdir -p "$AUTOSTART_DIR"
$SUDO tee "$AUTOSTART_DIR/usb-backup-manager.desktop" > /dev/null << EOF
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=Резервное копирование USB-накопителей
Exec=$APP_DIR/autostart_gui.sh
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
$SUDO chown -R "$KIOSK_USER:" "$KIOSK_HOME/.config"

echo ""
echo "=== Готово ==="
echo "Headless-мониторинг уже работает: systemctl status $SERVICE_NAME"
echo "GUI запустится автоматически при следующем входе в сессию."
echo ""
echo "Запустить GUI прямо сейчас (из графической сессии пользователя $KIOSK_USER):"
echo "  $APP_DIR/autostart_gui.sh"
echo ""
echo "База и настройки: $APP_DIR/data"
echo "Папку назначения выбирайте в GUI на вкладке «Настройки» —"
echo "диски видны как в файловом менеджере, пробрасывать ничего не нужно."
echo ""
echo "Удаление автозапуска:"
echo "  systemctl disable --now $SERVICE_NAME"
echo "  rm $AUTOSTART_DIR/usb-backup-manager.desktop $SUDOERS_FILE"
