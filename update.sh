#!/bin/bash
# Обновление приложения одной командой:  ./update.sh
#
# Скачивает свежую версию из git (ветка master), и если есть изменения —
# переустанавливает приложение через install_native.sh (это перезапустит GUI).
# Если изменений нет, ничего не трогает, чтобы зря не перезапускать
# приложение — вдруг прямо сейчас идёт копирование с флешки.
#
# Принудительная переустановка текущей версии:  ./update.sh --force
#
# ВНИМАНИЕ: локальные правки файлов в этой папке будут перезаписаны версией
# из git (git reset --hard). База, настройки и бэкапы приложения живут в
# /opt/astra-usb-monitor и НЕ затрагиваются.
set -e

cd "$(dirname "$0")"
APP_DIR="/opt/astra-usb-monitor"

if [ ! -d .git ]; then
    echo "ОШИБКА: это не git-клон репозитория — обновляться неоткуда."
    echo "Склонируйте репозиторий заново и запустите install_native.sh."
    exit 1
fi

echo "=== Обновление USB Backup Manager ==="

# Скачиваем свежую master с повторами при сетевых сбоях (2/4/8/16 секунд).
fetched=0
for delay in 2 4 8 16; do
    if git fetch origin master; then
        fetched=1
        break
    fi
    echo "git fetch не удался, повтор через ${delay}с..."
    sleep "$delay"
done
if [ "$fetched" -ne 1 ]; then
    echo "ОШИБКА: не удалось скачать обновление (нет сети?). Приложение не тронуто."
    exit 1
fi

LOCAL=$(git rev-parse HEAD)
REMOTE=$(git rev-parse origin/master)
if [ "$LOCAL" = "$REMOTE" ] && [ -f "$APP_DIR/start_native.sh" ] && [ "${1:-}" != "--force" ]; then
    echo "Обновлений нет — установлена актуальная версия:"
    git log -1 --format='  %h %s'
    echo "Принудительная переустановка: ./update.sh --force"
    exit 0
fi

echo "Переключаемся на свежую версию..."
git checkout -B master origin/master 2>/dev/null || git checkout master
git reset --hard origin/master
git log -1 --format='Версия: %h %s'

echo ""
echo "Переустановка (GUI будет перезапущен)..."
./install_native.sh

echo ""
echo "=== Обновление завершено ==="
