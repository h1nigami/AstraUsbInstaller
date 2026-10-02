#!/bin/bash
# Настоящие install.sh и скрипты собранного .deb работают с подставными внешними командами.
set -eu
[ "${UPDATER_TEST_CONTAINER:-}" = 1 ] && [ -f /.dockerenv ] || exit 2
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
export PACKAGE_TEST_DIR="$work"
mkdir -p "$work/bin" "$work/payload" /opt/astra-usb-avalonia/data /run/systemd/system
export PATH="$work/bin:$PATH"
cat > "$work/bin/curl" <<'MOCK'
#!/bin/sh
output=""
while [ "$#" -gt 0 ]; do
    if [ "$1" = -o ]; then output="$2"; shift; fi
    last="$1"
    shift
done
if [ -z "$output" ]; then
    echo '{"url":"https://example/bestcam-station_2.1_amd64.deb"}'
elif [ "${last##*.}" = sha256 ]; then
    exit 1
else
    echo package > "$output"
fi
MOCK
cat > "$work/bin/apt-get" <<'MOCK'
#!/bin/sh
echo "apt $*" >> "$PACKAGE_TEST_DIR/commands"
MOCK
cat > "$work/bin/systemctl" <<'MOCK'
#!/bin/sh
case "$1" in
    show) echo loaded ;;
    is-active) [ "$(cat "$PACKAGE_TEST_DIR/${3}.state")" = active ] ;;
    stop)
        echo "stop $2" >> "$PACKAGE_TEST_DIR/commands"
        [ "${PACKAGE_TEST_REFUSE_STOP:-}" = 1 ] || echo inactive > "$PACKAGE_TEST_DIR/${2}.state"
        ;;
    *) echo "$*" >> "$PACKAGE_TEST_DIR/commands" ;;
esac
MOCK
chmod +x "$work/bin/"*
for name in AstraUsb start_native.sh; do
    printf '#!/bin/sh\nexit 0\n' > "$work/payload/$name"
    chmod +x "$work/payload/$name"
done
cp /src/avalonia/install_native.sh "$work/payload/"
printf 'rule\n' > "$work/payload/99-astra-usb-avalonia-udisks.rules"
bash /src/avalonia/packaging/build_deb.sh "$work/payload" v2.1 amd64 "$work/out"
dpkg-deb -e "$work/out/bestcam-station_2.1_amd64.deb" "$work/control"

reset_state() {
    : > "$work/commands"
    for unit in astra-usb-avalonia-update.timer astra-usb-avalonia-update.service astra-usb-avalonia.service; do
        echo active > "$work/$unit.state"
    done
}
busy_refuses() {
    reset_state
    if "$@" > "$work/output" 2>&1; then
        echo "FAIL: busy принят: $*"
        exit 1
    fi
    [ ! -s "$work/commands" ] || { cat "$work/commands"; exit 1; }
}
exec 8>>/opt/astra-usb-avalonia/data/devices.db.operations.lock
flock -n -s 8
busy_refuses bash /src/avalonia/install.sh v2.1
busy_refuses sh "$work/control/prerm" upgrade 2.1
[ -f "$work/control/preinst" ]
busy_refuses sh "$work/control/preinst" install
exec 8>&-
touch /opt/astra-usb-avalonia/data/.copying
busy_refuses bash /src/avalonia/install.sh v2.1
busy_refuses sh "$work/control/prerm" upgrade 2.1
busy_refuses sh "$work/control/preinst" install
rm /opt/astra-usb-avalonia/data/.copying

for script in "$work/control/prerm" "$work/control/preinst"; do
    reset_state
    sh "$script" upgrade 2.1
    for unit in astra-usb-avalonia-update.timer astra-usb-avalonia-update.service astra-usb-avalonia.service; do
        grep -qx "stop $unit" "$work/commands"
        [ "$(cat "$work/$unit.state")" = inactive ]
    done
done
reset_state
bash /src/avalonia/install.sh v2.1 > "$work/output"
[ "$(grep -c '^stop ' "$work/commands")" = 3 ]
grep -q '^apt install ' "$work/commands"
reset_state
export PACKAGE_TEST_REFUSE_STOP=1
if sh "$work/control/preinst" upgrade 2.1 > "$work/output" 2>&1; then
    echo 'FAIL: принята служба, которая не остановилась'
    exit 1
fi
unset PACKAGE_TEST_REFUSE_STOP
dpkg-deb -x "$work/out/bestcam-station_2.1_amd64.deb" /
reset_state
sh "$work/control/postinst" configure > "$work/output"
grep -qx 'enable --now astra-usb-avalonia-update.timer' "$work/commands"
reset_state
sh "$work/control/postinst" abort-upgrade > "$work/output"
if grep -q '^restart ' "$work/commands"; then
    echo 'FAIL: abort-upgrade перезапустил работающий киоск'
    exit 1
fi
echo 'PASS package-guard'
