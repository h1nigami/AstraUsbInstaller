#!/bin/bash
# Собирает пакет .deb со станцией. Пакет удобнее архива тем, что зависимости
# ставит apt, а удаление идёт штатно, вместе со службами.
#
# Использование: ./build_deb.sh <каталог сборки> <тег> <архитектура> <куда положить>
set -e

PAYLOAD="$1"
TAG="$2"
ARCH="$3"
OUT="$4"

VERSION="${TAG#v}"
ROOT="$(mktemp -d)"
APP="$ROOT/opt/astra-usb-avalonia"

mkdir -p "$APP" "$ROOT/DEBIAN"
chmod 755 "$ROOT"
cp -r "$PAYLOAD/." "$APP/"
chmod +x "$APP/AstraUsb" "$APP/start_native.sh" "$APP/install_native.sh"

# Версии icu перечислены списком: имя пакета зависит от выпуска системы, а
# без этой библиотеки программа падает ещё до появления окна.
cat > "$ROOT/DEBIAN/control" << CONTROL
Package: bestcam-station
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Maintainer: Best Electronics <support@bestcam.local>
Depends: libfontconfig1, libx11-6, libsm6, libice6, libicu76 | libicu72 | libicu71 | libicu70 | libicu67 | libicu63
Recommends: ffmpeg, alsa-utils, speech-dispatcher
Description: BestCam Station, сбор записей с носимых регистраторов
 Программа станции BestCam BC-10: собирает записи с регистраторов при
 подключении, ведёт архив и журнал, показывает состояние гнёзд на экране
 станции и в веб-панели. Ставит службу киоска и обновление по расписанию.
CONTROL

cat > "$ROOT/DEBIAN/postinst" << 'POSTINST'
#!/bin/sh
set -e

# Файлы уже разложены, установщику остаётся правило udev и службы.
/opt/astra-usb-avalonia/install_native.sh --units-only
POSTINST

cat > "$ROOT/DEBIAN/prerm" << 'PRERM'
#!/bin/sh
set -e

systemctl disable --now astra-usb-avalonia.service 2>/dev/null || true
systemctl disable --now astra-usb-avalonia-update.timer 2>/dev/null || true
PRERM

cat > "$ROOT/DEBIAN/postrm" << 'POSTRM'
#!/bin/sh
set -e

# База станции и собранные записи остаются: их удаляет только человек.
if [ "$1" = "purge" ] || [ "$1" = "remove" ]; then
    rm -f /etc/systemd/system/astra-usb-avalonia.service
    rm -f /etc/systemd/system/astra-usb-avalonia-update.service
    rm -f /etc/systemd/system/astra-usb-avalonia-update.timer
    rm -f /etc/udev/rules.d/99-astra-usb-avalonia-udisks.rules
    systemctl daemon-reload 2>/dev/null || true
fi
POSTRM

chmod 755 "$ROOT/DEBIAN/postinst" "$ROOT/DEBIAN/prerm" "$ROOT/DEBIAN/postrm"

mkdir -p "$OUT"
NAME="bestcam-station_${VERSION}_${ARCH}.deb"
dpkg-deb --build --root-owner-group "$ROOT" "$OUT/$NAME" > /dev/null
rm -rf "$ROOT"

echo "$OUT/$NAME"
