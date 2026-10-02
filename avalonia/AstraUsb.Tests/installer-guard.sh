#!/bin/bash
# Проверяется настоящий префикс установщика до изменения пакетов и файлов.
set -eu
[ "${UPDATER_TEST_CONTAINER:-}" = 1 ] && [ -f /.dockerenv ] || exit 2
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export USB_DB_PATH="$work/devices.db"
sed '/^# --- 1\./,$d' /src/avalonia/install_native.sh > "$work/install_native.sh"
printf '\nprintf started > "$USB_DB_PATH.started"\n' >> "$work/install_native.sh"
printf '#!/bin/sh\nexit 0\n' > "$work/AstraUsb"
chmod +x "$work/AstraUsb"
exec 8>>"$USB_DB_PATH.operations.lock"
flock -n -s 8
if bash "$work/install_native.sh" > "$work/output" 2>&1; then
    echo 'FAIL: установка прошла при совместной операции'
    exit 1
fi
[ ! -e "$USB_DB_PATH.started" ]
flock -n -x 8
ASTRA_OPERATIONS_LOCK_FD=8 bash "$work/install_native.sh"
[ -f "$USB_DB_PATH.started" ]
rm "$USB_DB_PATH.started"
exec 7>>"$work/other.lock"
if ASTRA_OPERATIONS_LOCK_FD=7 bash "$work/install_native.sh" > "$work/output" 2>&1; then
    echo 'FAIL: принят дескриптор другой базы'
    exit 1
fi
[ ! -e "$USB_DB_PATH.started" ]
echo 'PASS installer-guard'
