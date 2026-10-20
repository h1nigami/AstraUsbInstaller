"""Регрессии установщиков на изолированных путях с подставными системными командами."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(sys.argv[1]).resolve()
sys.argv[1:] = []


class InstallReviewFixes(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="astra-install-review-")
        self.root = Path(self.temp.name)
        self.app = self.root / "opt/astra-usb-avalonia"
        self.commands = self.root / "commands"
        self.commands.touch()
        for directory in (self.root / "bin", self.root / "payload", self.app / "data",
                          self.root / "etc/systemd/system", self.root / "etc/udev/rules.d",
                          self.root / "run/systemd/system"):
            directory.mkdir(parents=True, exist_ok=True)
        self.env = dict(os.environ, PATH=str(self.root / "bin") + ":" + os.environ["PATH"],
                        REVIEW_ROOT=str(self.root), ASTRA_ROOT=str(self.root),
                        PYTHON_ACTIVE="0", PYTHON_ENABLED="0", SHA_MODE="valid")
        self.command("id", "#!/bin/sh\necho 0\n")
        self.command("ldconfig", "#!/bin/sh\necho 'libfontconfig.so.1 libX11.so.6 libSM.so.6 libICE.so.6 libicuuc.so'\n")
        self.command("apt-cache", "#!/bin/sh\necho 'libicu76 - runtime'\n")
        self.command("udevadm", "#!/bin/sh\nexit 0\n")
        self.command("apt-get", '#!/bin/sh\necho "apt $*" >> "$REVIEW_ROOT/commands"\n')
        self.command("systemctl", """#!/bin/bash
unit="${@: -1}"
case "$1" in
 list-unit-files) echo 'astra-usb-monitor.service disabled';;
 is-active) case "$unit" in astra-usb-monitor*|astra-usb-update.timer|astra-usb-update.service) [ "$PYTHON_ACTIVE" = 1 ] && [ "${PYTHON_ACTIVE_UNIT:-$unit}" = "$unit" ];; *) exit 3;; esac;;
 is-enabled) case "$unit" in astra-usb-monitor*|astra-usb-update.timer) [ "$PYTHON_ENABLED" = 1 ];; astra-usb-update.service) [ "${PYTHON_STATIC_UPDATER:-0}" = 1 ];; *) exit 1;; esac;;
 show) case "$*" in *ActiveState*) echo "${PYTHON_ACTIVE_STATE:-inactive}";; *) echo loaded;; esac;;
 *) echo "systemctl $*" >> "$REVIEW_ROOT/commands";;
esac
""")
        self.command("curl", """#!/usr/bin/env python3
import hashlib, json, os, pathlib, sys
args=sys.argv[1:]; output=args[args.index('-o')+1] if '-o' in args else None
url=args[-1]; root=pathlib.Path(os.environ['REVIEW_ROOT'])
with (root/'requests').open('a') as log: log.write(url+'\\n')
if output is None:
    data=json.loads((root/'release.json').read_text())
    if url.endswith('/latest'): data=data['latest']
    elif '/tags/' in url: data=data['station']
    else: data=data['list']
    print(json.dumps(data)); sys.exit(0)
package=b'inert package\\n'
if url.endswith('.sha256'):
    mode=os.environ['SHA_MODE']
    if mode=='missing': sys.exit(22)
    text='' if mode=='empty' else ('bad' if mode=='malformed' else '0'*64 if mode=='wrong' else hashlib.sha256(package).hexdigest())+'  '+pathlib.PurePosixPath(url).name.removesuffix('.sha256')+'\\n'
    pathlib.Path(output).write_text(text)
else: pathlib.Path(output).write_bytes(package)
""")
        assets = [{"name": name, "browser_download_url": "https://example.test/" + name} for name in
                  ("bestcam-station_2.7.0_amd64.deb", "bestcam-station_2.7.0_amd64.deb.sha256")]
        station = dict(tag_name="v2.7.0", draft=False, prerelease=True,
                       published_at="2026-10-20T12:00:00Z", assets=assets)
        python = dict(tag_name="v1.25", draft=False, prerelease=False,
                      published_at="2026-10-20T13:00:00Z", assets=[])
        (self.root / "release.json").write_text(json.dumps(dict(station=station, latest=python, list=[python, station])))
        for name in ("AstraUsb", "start_native.sh"):
            file = self.root / "payload" / name
            file.write_text("#!/bin/sh\nexit 0\n")
            file.chmod(0o755)
        (self.root / "payload/99-astra-usb-avalonia-udisks.rules").write_text("rule\n")
        for name in ("AstraUsb", "99-astra-usb-avalonia-udisks.rules"):
            shutil.copy(self.root / "payload" / name, self.app / name)
        self.native = self.copy_script(SOURCE / "avalonia/install_native.sh", self.root / "payload/install_native.sh")
        self.online = self.copy_script(SOURCE / "avalonia/install.sh", self.root / "install.sh")

    def tearDown(self):
        self.temp.cleanup()

    def command(self, name, content):
        path = self.root / "bin" / name
        path.write_text(content)
        path.chmod(0o755)

    def copy_script(self, source, target):
        text = source.read_text().replace("/opt/astra-usb-avalonia", str(self.app))
        text = text.replace("/etc/", str(self.root / "etc") + "/")
        text = text.replace("/run/systemd/system", str(self.root / "run/systemd/system"))
        target.write_text(text)
        return target

    def run_script(self, script, *args):
        return subprocess.run(["bash", str(script), *args], env=self.env, stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=20)

    def test_disabled_python_unit_allows_noninteractive_native_update(self):
        result = self.run_script(self.native)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("systemctl restart astra-usb-avalonia.service", self.commands.read_text())

    def test_units_only_does_not_start_csharp_alongside_active_python(self):
        self.env["PYTHON_ACTIVE"] = "1"
        result = self.run_script(self.native, "--units-only")
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertNotIn("restart astra-usb-avalonia", self.commands.read_text())

    def test_package_preinst_refuses_active_python_before_stopping_csharp(self):
        preinst = self.build_preinst()
        self.env["PYTHON_ACTIVE"] = "1"
        result = self.run_script(preinst, "install")
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertEqual("", self.commands.read_text())

    def build_preinst(self):
        subprocess.run(["bash", str(SOURCE / "avalonia/packaging/build_deb.sh"), str(self.root / "payload"),
                        "v2.7.0", "amd64", str(self.root / "out")], check=True, stdout=subprocess.PIPE)
        subprocess.run(["dpkg-deb", "-e", str(self.root / "out/bestcam-station_2.7.0_amd64.deb"),
                        str(self.root / "control")], check=True)
        return self.copy_script(self.root / "control/preinst", self.root / "preinst")

    def test_inactive_static_python_updater_allows_package_preinst(self):
        self.env["PYTHON_STATIC_UPDATER"] = "1"
        result = self.run_script(self.build_preinst(), "install")
        self.assertEqual(0, result.returncode, result.stdout)

    def test_enabled_python_blocks_unattended_native_update(self):
        self.env["PYTHON_ENABLED"] = "1"
        result = self.run_script(self.native)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertEqual("", self.commands.read_text())

    def test_inactive_static_python_updater_allows_native_and_online_install(self):
        self.env["PYTHON_STATIC_UPDATER"] = "1"
        for script, args in ((self.native, ()), (self.online, ("v2.7.0",))):
            with self.subTest(script=script.name):
                result = self.run_script(script, *args)
                self.assertEqual(0, result.returncode, result.stdout)

    def test_running_python_updater_blocks_native_and_online_install_before_changes(self):
        self.env.update(PYTHON_ACTIVE="1", PYTHON_ACTIVE_UNIT="astra-usb-update.service")
        for script, args in ((self.native, ()), (self.online, ("v2.7.0",))):
            with self.subTest(script=script.name):
                self.commands.write_text("")
                result = self.run_script(script, *args)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertEqual("", self.commands.read_text())

    def test_activating_or_deactivating_python_updater_blocks_all_installers(self):
        preinst = self.build_preinst()
        for state in ("activating", "deactivating"):
            self.env["PYTHON_ACTIVE_STATE"] = state
            for script, args in ((self.native, ()), (self.online, ("v2.7.0",)), (preinst, ("install",))):
                with self.subTest(state=state, script=script.name):
                    self.commands.write_text("")
                    result = self.run_script(script, *args)
                    self.assertNotEqual(0, result.returncode, result.stdout)
                    self.assertEqual("", self.commands.read_text())

    def test_online_without_tag_selects_station_instead_of_python_latest(self):
        result = self.run_script(self.online)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("bestcam-station_2.7.0_amd64.deb", self.commands.read_text())

    def test_online_missing_empty_or_wrong_checksum_never_stops_services_or_installs(self):
        for mode in ("missing", "empty", "malformed", "wrong"):
            with self.subTest(mode=mode):
                self.commands.write_text("")
                self.env["SHA_MODE"] = mode
                result = self.run_script(self.online, "v2.7.0")
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertEqual("", self.commands.read_text())

    def test_online_ignores_draft_incomplete_foreign_and_wrong_architecture_releases(self):
        path = self.root / "release.json"
        data = json.loads(path.read_text())
        station = data["station"]
        draft = dict(station, draft=True, published_at="2026-11-30T00:00:00Z")
        incomplete = dict(station, assets=station["assets"][:1], published_at="2026-11-29T00:00:00Z")
        foreign = dict(station, tag_name="v1.99", published_at="2026-11-28T00:00:00Z")
        wrong_arch = dict(station, assets=[dict(item, name=item["name"].replace("amd64", "arm64"))
                                          for item in station["assets"]], published_at="2026-11-27T00:00:00Z")
        data["list"] = [station, wrong_arch, foreign, incomplete, draft]
        path.write_text(json.dumps(data))
        result = self.run_script(self.online)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("apt install", self.commands.read_text())

    def test_explicit_foreign_tag_or_missing_asset_checksum_never_changes_station(self):
        result = self.run_script(self.online, "v1.25")
        self.assertNotEqual(0, result.returncode, result.stdout)
        path = self.root / "release.json"
        data = json.loads(path.read_text())
        data["station"]["assets"].pop()
        path.write_text(json.dumps(data))
        result = self.run_script(self.online, "v2.7.0")
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertEqual("", self.commands.read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)
