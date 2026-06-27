#!/bin/bash
# Установка USB Backup Manager на Astra Linux (Docker + GUI)
set -e

cd "$(dirname "$0")"

echo "=== Установка USB Backup Manager ==="

echo "Установка зависимостей..."
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-v2 espeak-ng espeak-ng-data sox libsox-fmt-all
sudo usermod -aG docker $USER

echo "Разрешаем X11 подключения из контейнера..."
xhost +local:

echo "Сборка и запуск контейнера..."
docker compose up -d --build

AUTOSTART_DIR="$HOME/.config/autostart"
mkdir -p "$AUTOSTART_DIR"

cat > "$AUTOSTART_DIR/usb-backup-manager.desktop" << EOF
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=USB device backup with device tracking
Exec=xhost +local: && cd $(pwd) && docker compose up -d --build
Terminal=false
X-GNOME-Autostart-enabled=true
EOF

echo ""
echo "=== Готово ==="
echo "Контейнер запущен."
echo ""
echo "Если GUI не появился:"
echo "  1. Проверьте DISPLAY: echo \$DISPLAY (должно быть :0)"
echo "  2. Запустите: xhost +local:"
echo "  3. Проверьте логи: docker compose logs -f"
echo ""
echo "Стоп:  docker compose down"
echo "Логи:  docker compose logs -f"
