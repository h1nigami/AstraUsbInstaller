#!/bin/bash
# Установка USB Backup Manager на Astra Linux (Docker + GUI)
set -e

APP_DIR="$HOME/astra-usb-installer"
AUTOSTART_DIR="$HOME/.config/autostart"

echo "=== Установка USB Backup Manager ==="

if [ ! -d "$APP_DIR" ]; then
    git clone https://github.com/your-repo/astra-usb-installer.git "$APP_DIR"
fi

cd "$APP_DIR"

echo "Установка Docker..."
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-v2
sudo usermod -aG docker $USER

echo "Сборка и запуск контейнера..."
docker compose up -d --build

mkdir -p "$AUTOSTART_DIR"

cat > "$AUTOSTART_DIR/usb-backup-manager.desktop" << EOF
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=USB device backup with device tracking
Exec=docker compose -f $APP_DIR/docker-compose.yml up -d --build
Terminal=false
X-GNOME-Autostart-enabled=true
EOF

echo ""
echo "=== Готово ==="
echo "Контейнер запущен. При перезагрузке GUI появится автоматически."
echo ""
echo "Логи:  docker compose logs -f"
echo "Стоп:  docker compose down"
