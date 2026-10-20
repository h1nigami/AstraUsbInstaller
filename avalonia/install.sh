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

for tool in curl dpkg apt-get python3 sha256sum; do
    command -v "$tool" >/dev/null 2>&1 || { echo "Нужен $tool"; exit 1; }
done

API="https://api.github.com/repos/$REPO/releases"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
if [ -n "$TAG" ]; then
    case "$TAG" in v2.*) ;; *) echo "Нужен тег C# версии v2.*"; exit 1;; esac
    curl -fsSL "$API/tags/$TAG" > "$WORK/releases.json"
else
    curl -fsSL "$API?per_page=100" > "$WORK/releases.json"
fi

# Python нужен только установщику для разбора JSON, сама станция работает без него.
python3 - "$WORK/releases.json" "$ARCH" "$TAG" > "$WORK/selected" <<'PY'
import json, re, sys
with open(sys.argv[1]) as stream:
    releases = json.load(stream)
releases = releases if isinstance(releases, list) else [releases]
for release in sorted(releases, key=lambda item: item.get('published_at') or '', reverse=True):
    tag = release.get('tag_name', '')
    if release.get('draft') or not re.fullmatch(r'v2\.[0-9A-Za-z.-]+', tag):
        continue
    if sys.argv[3] and tag != sys.argv[3]:
        continue
    name = f'bestcam-station_{tag[1:]}_{sys.argv[2]}.deb'
    assets = {item['name']: item.get('browser_download_url', '') for item in release.get('assets', [])}
    urls = [assets.get(name, ''), assets.get(name + '.sha256', '')]
    if all(url.startswith('https://') and not any(c.isspace() for c in url) for url in urls):
        print(name, *urls, sep='\n')
        break
else:
    sys.exit('Нет готового C# релиза v2.* с пакетом этой архитектуры и SHA256.')
PY
mapfile -t SELECTED < "$WORK/selected"
NAME="${SELECTED[0]}"
URL="${SELECTED[1]}"
SHA_URL="${SELECTED[2]}"

echo "--- Скачиваем $NAME..."
curl -fsSL -o "$WORK/$NAME" "$URL"
curl -fsSL -o "$WORK/$NAME.sha256" "$SHA_URL"

echo "--- Сверяем контрольную сумму..."
EXPECTED="$(awk 'NR == 1 { print $1 }' "$WORK/$NAME.sha256")"
ACTUAL="$(sha256sum "$WORK/$NAME")"
if [[ ! "$EXPECTED" =~ ^[0-9a-fA-F]{64}$ ]] || \
    [ "${EXPECTED,,}" != "${ACTUAL%% *}" ]; then
    echo "ОШИБКА: отсутствующая или неверная SHA256, установка отменена"
    exit 1
fi

echo "--- Ставим пакет..."
if [ -d /run/systemd/system ]; then
    for unit in astra-usb-monitor.service astra-usb-update.timer astra-usb-update.service; do
        if systemctl is-active --quiet "$unit" || systemctl is-enabled --quiet "$unit"; then
            echo "Установка прервана: Python-версия активна или включена. Сначала выполните явную миграцию."
            exit 1
        fi
    done
fi
# Проверяем занятость до apt: старый prerm мог ещё не знать о блокировке.
STATION_DATA="/opt/astra-usb-avalonia/data"
mkdir -p "$STATION_DATA"
exec 9>>"$STATION_DATA/devices.db.operations.lock"
if ! flock -n -x 9; then
    echo "ОШИБКА: станция занята, установка отложена"
    exit 1
fi
if [ -f "$STATION_DATA/.copying" ] && \
    [ "$(( $(date +%s) - $(stat -c %Y "$STATION_DATA/.copying") ))" -lt 60 ]; then
    echo "ОШИБКА: станция собирает записи, установка отложена"
    exit 1
fi
if [ -d /run/systemd/system ]; then
    for unit in astra-usb-avalonia-update.timer astra-usb-avalonia-update.service astra-usb-avalonia.service; do
        state="$(systemctl show -p LoadState --value "$unit")"
        [ "$state" = not-found ] && continue
        systemctl stop "$unit"
        if systemctl is-active --quiet "$unit"; then
            echo "ОШИБКА: служба $unit не остановилась"
            exit 1
        fi
    done
fi
# Между скриптами dpkg новые задания не запускаются: киоск и апдейтер остановлены.
exec 9>&-
apt-get update -qq || true
apt-get install -y "$WORK/$NAME"

echo
echo "Готово. Состояние станции: systemctl status astra-usb-avalonia"
echo "Учётная запись admin, пароль 888888, смените его в настройках."
