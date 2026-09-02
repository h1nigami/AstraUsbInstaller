#!/bin/bash
# Собирает версию для станции: один каталог, который переносится целиком.
#
# Сборка самодостаточная (--self-contained), потому что на Astra Linux нет
# готового пакета .NET нужной версии, и требовать его установку на каждой
# станции значило бы отдельную возню на каждой точке. Каталог получается
# крупнее, зато переносится копированием и не зависит от того, что стоит в
# системе.
set -e

RUNTIME="${1:-linux-x64}"
OUT="${2:-publish/$RUNTIME}"

cd "$(dirname "$0")"

echo "--- Сборка для $RUNTIME в $OUT"
dotnet publish AstraUsb/AstraUsb.csproj \
    -c Release \
    -r "$RUNTIME" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "$OUT"

chmod +x "$OUT/AstraUsb" 2>/dev/null || true
cp start_native.sh install_native.sh 99-astra-usb-avalonia-udisks.rules "$OUT/" 2>/dev/null || true
chmod +x "$OUT/start_native.sh" "$OUT/install_native.sh" 2>/dev/null || true

if [ -f ../VERSION ]; then
    cp ../VERSION "$OUT/VERSION"
elif command -v git >/dev/null 2>&1; then
    TAG=$(git describe --tags --abbrev=0 2>/dev/null || true)
    if [ -n "$TAG" ]; then
        echo "$TAG $(date +%F)" > "$OUT/VERSION"
    fi
fi

echo
echo "Готово: $OUT"
echo "Перенесите каталог на станцию и запустите там ./install_native.sh"
