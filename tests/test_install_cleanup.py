"""Проверка сохранности отдельной станции BestCam при установке Python.

Блок очистки вытаскивается из установщика и запускается в песочнице: systemctl,
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

SYSTEMCTL = """#!/bin/sh
echo "$@" >> "$LOG"
[ "$1" = "show" ] && echo "$ASTRA_ROOT/opt/astra-usb-avalonia"
exit 0
"""

DPKG_PRESENT = """#!/bin/sh
exit 0
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


def extract_cleanup():
    """Выполнить только очистку, исключив установку пакетов и служб."""
    text = pathlib.Path(INSTALLER).read_text(encoding="utf-8")
    return text.split("# --- 2.", 1)[1].split("# --- 3.", 1)[0].split("\n", 1)[1]


def extract_named_function(name):
    """Тело функции из установщика — без остального скрипта: он ставит
    систему целиком и в тесте выполняться не должен."""
    text = pathlib.Path(INSTALLER).read_text(encoding="utf-8")
    match = re.search(r"^%s\(\) \{$.*?^\}$" % name, text, re.M | re.S)
    return match.group(0) if match else ""


@unittest.skipUnless(BASH, "нужен работающий bash")
class PreserveStationTest(unittest.TestCase):
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
                           ("apt-get", APT_GET), ("rm", APT_GET), ("docker", APT_GET)):
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
                    + extract_cleanup() + "\n")

        result = subprocess.run(
            [BASH, "cleanup.sh"], cwd=self.root, capture_output=True,
            text=True, env={**os.environ, "ASTRA_ROOT": self.root,
                            "LOG": self.log, "SUDO": "", "APP_DIR": os.path.join(self.root, "python")})
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def calls(self):
        log = pathlib.Path(self.log)
        return log.read_text(encoding="utf-8") if log.exists() else ""

    def test_python_install_preserves_independent_station(self):
        self.run_cleanup(DPKG_PRESENT)
        self.assertTrue(os.path.exists(os.path.join(self.app, "data", "station.db")))
        self.assertTrue(os.path.exists(self.rule))
        self.assertEqual(len(os.listdir(self.units)), 3)
        self.assertNotIn("remove", self.calls())
        self.assertNotIn("disable", self.calls())
        self.assertNotIn("astra-usb-avalonia", self.calls())


def _stub_trust_tools(root):
    """Заглушки chown/runuser/gio: пишут вызовы в лог вместо системы."""
    bin_dir = os.path.join(root, "bin")
    os.makedirs(bin_dir, exist_ok=True)
    log = os.path.join(root, "calls.log")
    for name in ("chown", "runuser", "gio"):
        path = os.path.join(bin_dir, name)
        with open(path, "w", newline="\n") as f:
            f.write(f'#!/bin/sh\necho "{name} $*" >> "{log}"\n')
        os.chmod(path, 0o755)
    return log


@unittest.skipUnless(BASH, "нужен работающий bash")
class DesktopShortcutTest(unittest.TestCase):
    FUNC = "install_desktop_shortcut"

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def run_shortcut(self, extra_env=None):
        script = os.path.join(self.root, "shortcut.sh")
        with open(script, "w", newline="\n", encoding="utf-8") as f:
            f.write('set -e\nPATH="$PWD/bin:$PATH"\n'
                    + extract_named_function(self.FUNC) + f"\n{self.FUNC}\n")
        env = {**os.environ, "ASTRA_ROOT": self.root, "SUDO": "",
               "HOME": self.root, "SUDO_USER": ""}
        env.update(extra_env or {})
        result = subprocess.run(
            [BASH, "shortcut.sh"], cwd=self.root, capture_output=True,
            text=True, env=env)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def desktops(self):
        found = []
        for dirpath, _dirnames, filenames in os.walk(self.root):
            if "BestCam-USB.desktop" in filenames:
                found.append(os.path.join(dirpath, "BestCam-USB.desktop"))
        return found

    def test_creates_trusted_launcher_on_desktop(self):
        desktop = os.path.join(self.root, "Desktop")
        os.makedirs(desktop)
        self.run_shortcut({"ASTRA_DESKTOP_DIR": desktop})

        shortcuts = self.desktops()
        self.assertEqual(shortcuts, [os.path.join(desktop, "BestCam-USB.desktop")])
        text = pathlib.Path(shortcuts[0]).read_text(encoding="utf-8")
        self.assertIn("[Desktop Entry]", text)
        self.assertIn("pkexec", text)
        self.assertIn("astra-usb-monitor", text)
        self.assertIn("Icon=/opt/astra-usb-monitor/data/LOGO-1.png", text)
        self.assertTrue(os.access(shortcuts[0], os.X_OK),
                        "ярлык должен быть исполняемым")

    def test_uses_sudo_user_home(self):
        home = os.path.join(self.root, "home", "operator", "Desktop")
        os.makedirs(home)
        self.run_shortcut({"SUDO_USER": "operator"})

        self.assertEqual(self.desktops(), [os.path.join(home, "BestCam-USB.desktop")])

    def test_shortcut_is_given_to_user_and_trusted(self):
        home = os.path.join(self.root, "home", "operator", "Desktop")
        os.makedirs(home)
        log = _stub_trust_tools(self.root)
        self.run_shortcut({"SUDO_USER": "operator"})

        calls = pathlib.Path(log).read_text(encoding="utf-8")
        shortcut = os.path.join(home, "BestCam-USB.desktop")
        self.assertIn(f"chown operator: {shortcut}", calls)
        self.assertIn(f"runuser -u operator -- gio set {shortcut} metadata::trusted true", calls)

    def test_no_desktop_changes_nothing(self):
        self.run_shortcut()
        self.assertEqual(self.desktops(), [])


AVALONIA_INSTALLER = os.path.join(REPO, "avalonia", "install_native.sh")


@unittest.skipUnless(BASH, "нужен работающий bash")
class AvaloniaDesktopShortcutTest(unittest.TestCase):
    """Тот же ярлык в установщике C#: своя служба и свой файл ярлыка."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def run_shortcut(self, extra_env=None):
        text = pathlib.Path(AVALONIA_INSTALLER).read_text(encoding="utf-8")
        match = re.search(r"^install_desktop_shortcut\(\) \{$.*?^\}$", text, re.M | re.S)
        self.assertIsNotNone(match, "в установщике C# нет install_desktop_shortcut")
        script = os.path.join(self.root, "shortcut.sh")
        with open(script, "w", newline="\n", encoding="utf-8") as f:
            f.write('set -e\nPATH="$PWD/bin:$PATH"\n' + match.group(0)
                    + "\ninstall_desktop_shortcut\n")
        env = {**os.environ, "ASTRA_ROOT": self.root, "SUDO": "",
               "HOME": self.root, "SUDO_USER": ""}
        env.update(extra_env or {})
        result = subprocess.run(
            [BASH, "shortcut.sh"], cwd=self.root, capture_output=True,
            text=True, env=env)
        self.assertEqual(result.returncode, 0, result.stderr)

    def shortcuts(self):
        return [os.path.join(dirpath, "BestCam-Station.desktop")
                for dirpath, _dirnames, filenames in os.walk(self.root)
                if "BestCam-Station.desktop" in filenames]

    def test_creates_launcher_for_avalonia_service(self):
        desktop = os.path.join(self.root, "Desktop")
        os.makedirs(desktop)
        self.run_shortcut({"ASTRA_DESKTOP_DIR": desktop})

        found = self.shortcuts()
        self.assertEqual(found, [os.path.join(desktop, "BestCam-Station.desktop")])
        text = pathlib.Path(found[0]).read_text(encoding="utf-8")
        self.assertIn("Exec=pkexec systemctl start astra-usb-avalonia\n", text)
        self.assertIn("Icon=/opt/astra-usb-avalonia/logo.png", text)
        self.assertNotIn("astra-usb-monitor", text)
        self.assertTrue(os.access(found[0], os.X_OK))

    def test_uses_russian_desktop_of_sudo_user(self):
        desktop = os.path.join(self.root, "home", "operator", "Рабочий стол")
        os.makedirs(desktop)
        self.run_shortcut({"SUDO_USER": "operator"})
        self.assertEqual(self.shortcuts(), [os.path.join(desktop, "BestCam-Station.desktop")])

    def test_shortcut_is_given_to_user_and_trusted(self):
        desktop = os.path.join(self.root, "home", "operator", "Desktop")
        os.makedirs(desktop)
        log = _stub_trust_tools(self.root)
        self.run_shortcut({"SUDO_USER": "operator"})

        calls = pathlib.Path(log).read_text(encoding="utf-8")
        shortcut = os.path.join(desktop, "BestCam-Station.desktop")
        self.assertIn(f"chown operator: {shortcut}", calls)
        self.assertIn(f"runuser -u operator -- gio set {shortcut} metadata::trusted true", calls)

    def test_no_desktop_changes_nothing(self):
        self.run_shortcut()
        self.assertEqual(self.shortcuts(), [])


if __name__ == "__main__":
    sys.exit(unittest.main())
