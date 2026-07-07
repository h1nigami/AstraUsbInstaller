#!/bin/bash
# Установка USB Backup Manager на Astra Linux БЕЗ Docker.
#
# Что делает скрипт:
#   1. Ставит все зависимости через apt (python3, tkinter, утилиты монтирования).
#   2. Копирует приложение в /opt/astra-usb-monitor.
#   3. Добавляет sudoers-правило для запуска GUI от root (нужен mount/umount
#      флешек) без пароля.
#   4. Устанавливает XDG autostart (.desktop в ~/.config/autostart/) — GUI
#      запускается после входа пользователя в графическую сессию, а не при
#      загрузке системы. start_native.sh ждёт X-сервер, находит cookie сессии
#      через /proc и поднимает GUI. Мониторинг USB работает внутри GUI
#      (gui.py сам запускает monitor_usb фоновым потоком). Парольный «Выход»
#      из GUI останавливает процесс до следующего входа.
#
# Приложение работает от root: само вызывает mount/umount для флешек
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
# python3-pil + python3-pil.imagetk дают превью фото (JPEG и т.п.) во вкладке
# «Поиск» — без них tkinter умеет только GIF/PNG и окно просмотра остаётся
# пустым. mpv/vlc/eog — плеер и просмотрщик для открытия видео/фото по
# двойному клику (xdg-open в киоске часто без обработчика и молча ничего не
# открывает). Остальное — поддержка NTFS/exFAT-флешек и pip для rich. Имена
# различаются между версиями Astra, поэтому ставим по одному и не падаем,
# если пакета нет.
for pkg in x11-utils ntfs-3g exfat-fuse exfatprogs exfat-utils python3-pip \
           python3-rich python3-pil python3-pil.imagetk mpv vlc eog; do
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

# Pillow — превью фото во вкладке «Поиск». Если apt-пакета нет (имя зависит от
# версии Astra), пробуем через pip. GUI и без него работает — просто вместо
# превью откроется внешний просмотрщик.
if ! python3 -c "import PIL.ImageTk" 2>/dev/null; then
    if command -v pip3 >/dev/null 2>&1; then
        $SUDO pip3 install Pillow 2>/dev/null \
            || $SUDO pip3 install --break-system-packages Pillow 2>/dev/null \
            || echo "  Pillow не установился — превью фото будет открываться внешним просмотрщиком"
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
# data/ (база и конфиг) при переустановке не трогаем, но логотип — часть
# приложения, его кладём/обновляем всегда (GUI ищет data/LOGO-1.png рядом
# с gui.py).
$SUDO mkdir -p "$APP_DIR/data" "$APP_DIR/USB_Backups"
if [ -f "$SRC_DIR/data/LOGO-1.png" ]; then
    $SUDO cp "$SRC_DIR/data/LOGO-1.png" "$APP_DIR/data/LOGO-1.png"
else
    echo "ВНИМАНИЕ: $SRC_DIR/data/LOGO-1.png не найден — GUI покажет заглушку вместо логотипа"
fi

# --- 4. Sudoers для запуска GUI от root без пароля -------------------------
echo "--- Настройка sudoers..."
$SUDO tee /etc/sudoers.d/astra-usb-monitor > /dev/null << 'EOF'
Defaults!/opt/astra-usb-monitor/start_native.sh env_keep += "DISPLAY XAUTHORITY"
ALL ALL=(root) NOPASSWD: /opt/astra-usb-monitor/start_native.sh
EOF
$SUDO chmod 440 /etc/sudoers.d/astra-usb-monitor

# --- 5. XDG autostart (после входа в сессию, а не при загрузке) ------------
echo "--- Настройка автозапуска после входа в систему..."
if [ -n "$SUDO_USER" ]; then
    REAL_USER="$SUDO_USER"
else
    REAL_USER="${USER:-root}"
fi
REAL_HOME="$(getent passwd "$REAL_USER" | cut -d: -f6)"
AUTOSTART_DIR="$REAL_HOME/.config/autostart"
$SUDO mkdir -p "$AUTOSTART_DIR"
$SUDO tee "$AUTOSTART_DIR/usb-backup-manager.desktop" > /dev/null << 'EOF'
[Desktop Entry]
Type=Application
Name=USB Backup Manager
Comment=USB device backup with device tracking
Exec=/usr/bin/sudo /opt/astra-usb-monitor/start_native.sh
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
$SUDO chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR/usb-backup-manager.desktop" 2>/dev/null || true

# --- 6. Запуск GUI сейчас (после установки) --------------------------------
if [ -n "$SUDO_USER" ]; then
    echo "--- Запуск GUI для $SUDO_USER..."
    su - "$SUDO_USER" -c "/usr/bin/sudo /opt/astra-usb-monitor/start_native.sh" &
    sleep 2
fi

echo ""
echo "=== Готово ==="
echo "GUI запущен. При следующем входе в систему он запустится автоматически"
echo "(автозапуск через XDG: ~/.config/autostart/usb-backup-manager.desktop)."
echo "Мониторинг USB работает внутри GUI."
echo ""
echo "База и настройки: $APP_DIR/data"
echo "Папку назначения выбирайте в GUI на вкладке «Настройки» —"
echo "диски видны как в файловом менеджере, пробрасывать ничего не нужно."
echo ""
echo "Удаление: sudo rm -f /etc/sudoers.d/astra-usb-monitor"
echo "          sudo rm -f $AUTOSTART_DIR/usb-backup-manager.desktop"
echo "          sudo rm -rf $APP_DIR"
