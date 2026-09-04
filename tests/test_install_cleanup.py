"""Проверка блока install_native.sh, который выключает станцию BestCam.

Функция вытаскивается из установщика и запускается в песочнице: systemctl,
dpkg и apt-get подменены заглушками, а все пути уводятся под временный
каталог переменной ASTRA_ROOT, поэтому тест не трогает настоящую систему.
"""

import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INSTALLER = os.path.join(REPO, "install_native.sh")
FUNC = "remove_bestcam_station"

SYSTEMCTL = """#!/bin/sh
echo "$@" >> "$LOG"
[ "$1" = "show" ] && echo "$ASTRA_ROOT/opt/astra-usb-avalonia"
exit 0
"""

# Пакета в системе нет: так ставились v2.0 и v2.0.1, из архива.
DPKG_MISSING = """#!/bin/sh
exit 1
"""

DPKG_PRESENT = """#!/bin/sh
exit 0
"""

# Ни systemd, ни dpkg: так выглядит установка в контейнере.
SYSTEMCTL_MISSING = """#!/bin/sh
exit 127
"""

APT_GET = """#!/bin/sh
echo "$@" >> "$LOG"
exit 0
"""


def working_bash():
    """Путь к работающему bash или None: на Windows `which` находит обёртку
    WSL, которая без установленного дистрибутива не запускается вовсе."""
    path = shutil.which("bash")
    if not path:
        return None
    try:
        probe = subprocess.run([path, "-c", "echo ok"], capture_output=True,
                               text=True, timeout=30)
    except OSError:
        return None
    return path if probe.stdout.strip() == "ok" else None


BASH = working_bash()


def extract_function():
    """Тело функции из установщика — без остального скрипта: он ставит
    систему целиком и в тесте выполняться не должен."""
    text = pathlib.Path(INSTALLER).read_text(encoding="utf-8")
    match = re.search(r"^%s\(\) \{$.*?^\}$" % FUNC, text, re.M | re.S)
    return match.group(0) if match else ""


@unittest.skipUnless(BASH, "нужен работающий bash")
class RemoveStationTest(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.log = os.path.join(self.root, "calls.log")

        self.app = os.path.join(self.root, "opt", "astra-usb-avalonia")
        os.makedirs(os.path.join(self.app, "data"))
        pathlib.Path(self.app, "data", "station.db").touch()

        self.units = os.path.join(self.root, "etc", "systemd", "system")
        os.makedirs(self.units)
        for unit in ("astra-usb-avalonia.service",
                     "astra-usb-avalonia-update.service",
                     "astra-usb-avalonia-update.timer"):
            pathlib.Path(self.units, unit).touch()

        self.rule = os.path.join(self.root, "etc", "udev", "rules.d",
                                 "99-astra-usb-avalonia-udisks.rules")
        os.makedirs(os.path.dirname(self.rule))
        pathlib.Path(self.rule).touch()

    def run_cleanup(self, dpkg, systemctl=None):
        binaries = os.path.join(self.root, "bin")
        os.makedirs(binaries, exist_ok=True)
        for name, body in (("systemctl", systemctl or SYSTEMCTL), ("dpkg", dpkg),
                           ("apt-get", APT_GET)):
            path = os.path.join(binaries, name)
            with open(path, "w", newline="\n", encoding="utf-8") as f:
                f.write(body)
            os.chmod(path, 0o755)

        # Скрипт кладётся файлом: bash из Git for Windows переразбирает
        # командную строку по-своему и кавычки внутри `-c` до него не доходят.
        script = os.path.join(self.root, "cleanup.sh")
        with open(script, "w", newline="\n", encoding="utf-8") as f:
            # set -e как в самом установщике: без него оборванная команда
            # внутри функции осталась бы в тесте незамеченной.
            f.write('set -e\nPATH="$PWD/bin:$PATH"\n'
                    + extract_function() + f"\n{FUNC}\n")

        result = subprocess.run(
            [BASH, "cleanup.sh"], cwd=self.root, capture_output=True,
            text=True, env={**os.environ, "ASTRA_ROOT": self.root,
                            "LOG": self.log, "SUDO": ""})
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        log = pathlib.Path(self.log)
        return log.read_text(encoding="utf-8") if log.exists() else ""

    def test_manual_install_is_disabled_and_kept(self):
        self.run_cleanup(DPKG_MISSING)

        leftovers = [n for n in os.listdir(os.path.dirname(self.app))
                     if n.startswith("astra-usb-avalonia.removed.")]
        self.assertEqual(len(leftovers), 1, "каталог станции должен остаться под .removed")
        kept = os.path.join(os.path.dirname(self.app), leftovers[0], "data", "station.db")
        self.assertTrue(os.path.exists(kept), "база станции не должна пропадать")
        self.assertFalse(os.path.exists(self.app))
        self.assertEqual(os.listdir(self.units), [])
        self.assertFalse(os.path.exists(self.rule))
        self.assertIn("disable --now astra-usb-avalonia.service", self.calls())

    def test_package_is_removed_through_apt(self):
        self.run_cleanup(DPKG_PRESENT)

        self.assertIn("remove -y bestcam-station", self.calls())

    def test_survives_system_without_systemctl(self):
        self.run_cleanup(DPKG_MISSING, systemctl=SYSTEMCTL_MISSING)

        leftovers = [n for n in os.listdir(os.path.dirname(self.app))
                     if n.startswith("astra-usb-avalonia.removed.")]
        self.assertEqual(len(leftovers), 1, "без systemd каталог всё равно убирается")

    def test_station_absent_changes_nothing(self):
        shutil.rmtree(self.app)
        for unit in os.listdir(self.units):
            os.remove(os.path.join(self.units, unit))

        self.run_cleanup(DPKG_MISSING)

        self.assertTrue(os.path.exists(self.rule), "чужого правила udev тут нет — не трогаем")
        self.assertNotIn("disable", self.calls())


if __name__ == "__main__":
    sys.exit(unittest.main())
