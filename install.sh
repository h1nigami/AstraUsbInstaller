#!/bin/bash
# Установка USB Backup Manager на Astra Linux (Docker + GUI)
set -e

cd "$(dirname "$0")"

echo "=== Установка USB Backup Manager ==="

echo "Установка зависимостей..."
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-v2 espeak-ng espeak-ng-data sox libsox-fmt-all
sudo usermod -aG docker $USER

echo "Включаем Docker при старте системы..."
sudo systemctl enable docker
sudo systemctl start docker

echo "Разрешаем X11 подключения из контейнера..."
xhost +local:

echo "Сборка и запуск контейнера..."
docker compose up -d --build

AUTOSTART_DIR="$HOME/.config/autostart"
mkdir -p "$AUTOSTART_DIR"

APP_DIR="$(pwd)"
cat > "$AUTOSTART_DIR/usb-backup-manager.desktop" << EOF
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=USB device backup with device tracking
Exec=bash -c "xhost +local: && cd ${APP_DIR} && docker compose up -d"
Terminal=false
X-GNOME-Autostart-enabled=true
EOF

# Systemd service for boot-level autostart (headless / server mode)
cat > /tmp/astra-usb-monitor.service << EOF
[Unit]
Description=Astra USB Monitor
After=docker.service network-online.target
Requires=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=${APP_DIR}
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
EOF
sudo mv /tmp/astra-usb-monitor.service /etc/systemd/system/astra-usb-monitor.service
sudo systemctl daemon-reload
sudo systemctl enable astra-usb-monitor.service
echo "Systemd-сервис astra-usb-monitor включён для автозапуска при старте системы"

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
