#!/bin/bash
# Установка версии на Avalonia на станцию с Astra Linux.
#
# Что делает:
#   1. Проверяет системные библиотеки, на которые опирается рисование.
#   2. Проверяет, не занята ли станция Python-версией: две программы,
#      одновременно монтирующие и вычищающие одни и те же карты, мешают друг
#      другу, поэтому установщик останавливается и ждёт решения человека.
#   3. Кладёт приложение в /opt/astra-usb-avalonia. Сборка самодостаточная,
#      ставить .NET на станцию не нужно.
#   4. Ставит udev-правило: рабочий стол больше не монтирует карты, это делает
#      станция.
#   5. Ставит systemd-сервис, который ждёт графическую сессию и запускает
#      приложение. Автозапуск через сессию (XDG autostart) в Astra срабатывает
#      не во всех конфигурациях, systemd надёжнее.
#
# Запускать из каталога, собранного publish.sh.
set -e

APP_DIR="/opt/astra-usb-avalonia"
SERVICE_NAME="astra-usb-avalonia"
PYTHON_SERVICE="astra-usb-monitor"
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
    SUDO="sudo"
else
    SUDO=""
fi

echo "--- Проверка сборки..."
if [ ! -x "$SRC_DIR/AstraUsb" ]; then
    echo "В каталоге нет исполняемого файла AstraUsb."
    echo "Соберите версию для станции: ./publish.sh linux-x64"
    exit 1
fi

# --- 1. Системные библиотеки --------------------------------------------------
# Сборка самодостаточна по .NET, но рисование опирается на системные
# библиотеки: без libfontconfig приложение падает ещё до первого окна, и
# сообщение об этом видно только в журнале сервиса.
echo "--- Проверка системных библиотек..."
NEEDED_LIBS="libfontconfig.so.1 libX11.so.6 libSM.so.6 libICE.so.6"
PACKAGES="libfontconfig1 libx11-6 libsm6 libice6"

# Не обязательны, но без них станция теряет часть работы: alsa-utils играет
# тревогу, speech-dispatcher произносит подсказки, ffmpeg переводит записи
# в другой формат и достаёт кадры для просмотра по временной шкале (кадры
# берёт ffprobe с ffmpeg, оба идут одним пакетом).
# Ставим, если репозиторий доступен.
OPTIONAL_PACKAGES="alsa-utils speech-dispatcher ffmpeg"

missing_libs() {
    local lib found=""
    for lib in $NEEDED_LIBS; do
        if ! ldconfig -p 2>/dev/null | grep -q "$lib"; then
            found="$found $lib"
        fi
    done
    printf '%s' "$found"
}

MISSING="$(missing_libs)"
if [ -n "$MISSING" ]; then
    echo "Не хватает:$MISSING"
    if command -v apt-get >/dev/null 2>&1; then
        echo "Ставим из репозитория: $PACKAGES"
        $SUDO apt-get update || true
        $SUDO apt-get install -y $PACKAGES || true
    fi

    MISSING="$(missing_libs)"
    if [ -n "$MISSING" ]; then
        echo
        echo "Библиотеки так и не появились:$MISSING"
        echo "Поставьте их вручную, иначе приложение не откроет окно:"
        echo "    sudo apt-get install $PACKAGES"
        exit 1
    fi
fi

echo "--- Необязательные пакеты: звук, речь, преобразование форматов..."
if command -v apt-get >/dev/null 2>&1; then
    $SUDO apt-get install -y $OPTIONAL_PACKAGES ||         echo "Не поставились: без них молчат тревога и подсказки, а форматы не переводятся"
fi

# --- 2. Python-версия на той же станции --------------------------------------
if systemctl list-unit-files 2>/dev/null | grep -q "^$PYTHON_SERVICE.service"; then
    echo
    echo "На станции установлена Python-версия ($PYTHON_SERVICE)."
    echo "Две программы будут монтировать и чистить одни и те же карты."
    echo
    echo "Остановить и отключить её сейчас? Записи и база останутся на месте."
    read -r -p "Отключить Python-версию? [y/N] " answer
    case "$answer" in
        [yY]*)
            $SUDO systemctl disable --now "$PYTHON_SERVICE.service" || true
            $SUDO systemctl disable --now astra-usb-update.timer 2>/dev/null || true
            echo "Python-версия отключена. Её каталог не тронут."
            ;;
        *)
            echo "Установка прервана: сначала решите, какая версия работает на станции."
            exit 1
            ;;
    esac
fi

# --- 3. Приложение ------------------------------------------------------------
echo "--- Установка в $APP_DIR..."
$SUDO mkdir -p "$APP_DIR"

# data и USB_Backups не трогаем: там база станции и собранные записи.
$SUDO find "$APP_DIR" -mindepth 1 -maxdepth 1 \
    ! -name data ! -name USB_Backups -exec rm -rf {} + 2>/dev/null || true

$SUDO cp -r "$SRC_DIR/." "$APP_DIR/"
$SUDO chmod +x "$APP_DIR/AstraUsb" "$APP_DIR/start_native.sh"
$SUDO mkdir -p "$APP_DIR/data" "$APP_DIR/USB_Backups"

# --- 4. Правило udev ----------------------------------------------------------
echo "--- Настройка udev-правила против автомонтирования рабочим столом..."
$SUDO cp "$SRC_DIR/99-astra-usb-avalonia-udisks.rules" \
    "/etc/udev/rules.d/99-astra-usb-avalonia-udisks.rules"

if command -v udevadm >/dev/null 2>&1; then
    $SUDO udevadm control --reload-rules || true
    $SUDO udevadm trigger --subsystem-match=block || true
fi

# --- 5. Сервис ----------------------------------------------------------------
echo "--- Настройка systemd-сервиса..."
$SUDO tee "/etc/systemd/system/$SERVICE_NAME.service" > /dev/null << EOF
[Unit]
Description=BestCam USB Backup Manager (Avalonia)
After=multi-user.target

[Service]
Type=simple
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/start_native.sh
# on-failure: выход по паролю завершает приложение с кодом 0, и systemd его
# НЕ перезапускает, иначе защита выхода из киоска не имела бы смысла.
Restart=on-failure
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

[Install]
WantedBy=multi-user.target
EOF

$SUDO systemctl daemon-reload
$SUDO systemctl enable "$SERVICE_NAME.service"
$SUDO systemctl restart "$SERVICE_NAME.service"

echo
echo "Установлено в $APP_DIR"
echo "Состояние:  systemctl status $SERVICE_NAME"
echo "Журнал:     journalctl -u $SERVICE_NAME -f"
echo
echo "Учётная запись по умолчанию: admin, пароль 888888."
echo "Смените их в разделе «Настройки», подраздел «Доступ»."
echo
echo "Веб-панель выключена. Включается в разделе «Настройки», подраздел"
echo "«Веб-панель»; после включения нужен перезапуск программы, а порт"
echo "придётся открыть в брандмауэре станции вручную."
