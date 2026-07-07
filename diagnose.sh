#!/bin/bash
# Собирает диагностику установки USB Backup Manager (без Docker) одной
# командой. Запуск:  sudo bash diagnose.sh
# Вывод целиком отправьте разработчику — по нему видно, на каком шаге затык.

APP_DIR="/opt/astra-usb-monitor"

echo "================ ДИАГНОСТИКА USB Backup Manager ================"
echo "Дата: $(date)"
echo "Пользователь: $(id)"

echo ""
echo "=== 1. XDG autostart ==="
for f in /home/*/.config/autostart/usb-backup-manager.desktop \
         /root/.config/autostart/usb-backup-manager.desktop; do
    if [ -f "$f" ]; then
        echo "  найден: $f"
        head -5 "$f"
    fi
done
echo "  (если файла нет — автозапуск после входа не сработает)"

echo ""
echo "=== 2. Sudoers ==="
if [ -f /etc/sudoers.d/astra-usb-monitor ]; then
    echo "  sudoers-правило: /etc/sudoers.d/astra-usb-monitor"
    cat /etc/sudoers.d/astra-usb-monitor
else
    echo "  НЕТ /etc/sudoers.d/astra-usb-monitor — GUI не сможет mount/umount"
fi

echo ""
echo "=== 3. Процессы ==="
if pgrep -f "start_native.sh" >/dev/null 2>&1; then
    echo "  start_native.sh: ЗАПУЩЕН"
else
    echo "  start_native.sh: НЕ ЗАПУЩЕН (ждёт логина пользователя)"
fi
if pgrep -f "python3.*(gui|main)" >/dev/null 2>&1; then
    echo "  GUI (python3): ЗАПУЩЕН"
else
    echo "  GUI (python3): НЕ ЗАПУЩЕН"
fi

echo ""
echo "=== 4. Файлы приложения ==="
ls -la "$APP_DIR" 2>&1

echo ""
echo "=== 5. Графическая сессия ==="
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
echo "=== 6. Python и зависимости ==="
python3 --version 2>&1 || echo "НЕТ python3"
python3 -c "import tkinter; print('tkinter: OK')" 2>&1
python3 -c "import rich; print('rich: OK')" 2>&1 || echo "rich: нет (не критично)"
command -v lsblk >/dev/null 2>&1 && echo "lsblk: OK" || echo "НЕТ lsblk (util-linux)"
command -v mount >/dev/null 2>&1 && echo "mount: OK" || echo "НЕТ mount"

echo ""
echo "=== 7. Диски и точки монтирования ==="
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT 2>&1

echo ""
echo "================ КОНЕЦ ДИАГНОСТИКИ ================"
