#!/bin/bash
# Установка USB Backup Manager на Astra Linux (рабочая станция с GUI)

set -e

APP_DIR="$HOME/astra-usb-installer"
AUTOSTART_DIR="$HOME/.config/autostart"
PYTHON=$(command -v python3 || command -v python)

echo "=== Установка USB Backup Manager ==="

if [ ! -d "$APP_DIR" ]; then
    mkdir -p "$APP_DIR"
    cp -r "$(dirname "$0")"/* "$APP_DIR/"
fi

cd "$APP_DIR"

echo "Установка зависимостей..."
sudo apt-get update
sudo apt-get install -y python3 python3-pip python3-tk

$PYTHON -m pip install --user -r requirements.txt

mkdir -p "$AUTOSTART_DIR"

cat > "$AUTOSTART_DIR/usb-backup-manager.desktop" << EOF
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=USB device backup with device tracking
Exec=$PYTHON $APP_DIR/main.py
Terminal=false
X-GNOME-Autostart-enabled=true
EOF

echo ""
echo "=== Готово ==="
echo ""
echo "Запуск:  $PYTHON $APP_DIR/main.py"
echo "Автозапуск добавлен в $AUTOSTART_DIR"
echo "После перезагрузки GUI запустится автоматически."
echo ""
echo "Запустить сейчас:  $PYTHON $APP_DIR/main.py &"
