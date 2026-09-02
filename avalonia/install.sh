#!/bin/bash
# Установка станции одной командой:
#
#   curl -fsSL https://raw.githubusercontent.com/h1nigami/AstraUsbInstaller/master/avalonia/install.sh | sudo bash
#
# Скрипт сам определяет разрядность, забирает пакет нужного релиза, сверяет
# контрольную сумму и ставит его через apt, чтобы зависимости подтянулись.
# Нужен другой релиз: передайте тег, например
#
#   ... | sudo bash -s -- v2.0.1
set -e

REPO="h1nigami/AstraUsbInstaller"
TAG="$1"

if [ "$(id -u)" -ne 0 ]; then
    echo "Запускать от root: sudo bash install.sh"
    exit 1
fi

case "$(uname -m)" in
    x86_64)  ARCH="amd64" ;;
    aarch64) ARCH="arm64" ;;
    *)
        echo "Эта архитектура не поддерживается: $(uname -m)"
        exit 1
        ;;
esac

for tool in curl dpkg apt-get; do
    command -v "$tool" >/dev/null 2>&1 || { echo "Нужен $tool"; exit 1; }
done

API="https://api.github.com/repos/$REPO/releases"
if [ -n "$TAG" ]; then
    ANSWER="$(curl -fsSL "$API/tags/$TAG")"
else
    ANSWER="$(curl -fsSL "$API/latest")"
fi

# Ссылка на пакет своей архитектуры прямо из ответа GitHub: разбирать его
# целиком незачем, достаточно вытащить нужный адрес.
URL="$(printf '%s' "$ANSWER" \
    | grep -o "https://[^\"]*bestcam-station_[^\"]*_${ARCH}\.deb" | head -1)"

if [ -z "$URL" ]; then
    echo "В релизе нет пакета для $ARCH."
    echo "Посмотрите список: https://github.com/$REPO/releases"
    exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
NAME="$(basename "$URL")"

echo "--- Скачиваем $NAME..."
curl -fsSL -o "$WORK/$NAME" "$URL"
curl -fsSL -o "$WORK/$NAME.sha256" "$URL.sha256" 2>/dev/null || true

if [ -s "$WORK/$NAME.sha256" ]; then
    echo "--- Сверяем контрольную сумму..."
    (cd "$WORK" && sha256sum -c "$NAME.sha256")
else
    echo "Контрольной суммы в релизе нет, ставим без сверки."
fi

echo "--- Ставим пакет..."
apt-get update -qq || true
apt-get install -y "$WORK/$NAME"

echo
echo "Готово. Состояние станции: systemctl status astra-usb-avalonia"
echo "Учётная запись admin, пароль 888888, смените его в настройках."
