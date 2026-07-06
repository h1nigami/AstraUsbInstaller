#!/bin/bash
# Собирает диагностику установки USB Backup Manager (без Docker) одной
# командой. Запуск:  sudo bash diagnose.sh
# Вывод целиком отправьте разработчику — по нему видно, на каком шаге затык.

SERVICE_NAME="astra-usb-monitor"
APP_DIR="/opt/astra-usb-monitor"

echo "================ ДИАГНОСТИКА USB Backup Manager ================"
echo "Дата: $(date)"
echo "Пользователь: $(id)"

echo ""
echo "=== 1. Systemd-сервис ==="
echo "включён в автозагрузку: $(systemctl is-enabled $SERVICE_NAME 2>&1)"
echo "текущее состояние:      $(systemctl is-active $SERVICE_NAME 2>&1)"
systemctl status "$SERVICE_NAME" --no-pager -l 2>&1 | head -15

echo ""
echo "=== 2. Логи сервиса (последние 60 строк) ==="
journalctl -u "$SERVICE_NAME" -n 60 --no-pager 2>&1

echo ""
echo "=== 3. Файлы приложения ==="
ls -la "$APP_DIR" 2>&1

echo ""
echo "=== 4. Графическая сессия ==="
echo "X-сокеты:"
ls -la /tmp/.X11-unix 2>&1
echo "кто вошёл в систему:"
who 2>&1
echo "DISPLAY текущего терминала: '${DISPLAY:-<пусто>}'"
if command -v xdpyinfo >/dev/null 2>&1; then
    echo "xdpyinfo: установлен"
else
    echo "xdpyinfo: НЕТ (пакет x11-utils) — доступность X проверяется вслепую"
fi

echo ""
echo "=== 5. Python и зависимости ==="
python3 --version 2>&1 || echo "НЕТ python3"
python3 -c "import tkinter; print('tkinter: OK')" 2>&1
python3 -c "import rich; print('rich: OK')" 2>&1 || echo "rich: нет (не критично)"
command -v lsblk >/dev/null 2>&1 && echo "lsblk: OK" || echo "НЕТ lsblk (util-linux)"
command -v mount >/dev/null 2>&1 && echo "mount: OK" || echo "НЕТ mount"

echo ""
echo "=== 6. Диски и точки монтирования ==="
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT 2>&1

echo ""
echo "================ КОНЕЦ ДИАГНОСТИКИ ================"
