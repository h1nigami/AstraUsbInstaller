#!/usr/bin/env python3
"""Автообновление киоска с релизов GitHub.

Запускается systemd-таймером, а не из GUI: install_native.sh в конце
перезапускает сервис приложения, и обновление, идущее внутри этого сервиса,
убило бы само себя посреди подмены файлов.
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

REPO = "h1nigami/AstraUsbInstaller"
LATEST_URL = f"https://api.github.com/repos/{REPO}/releases/latest"
APP_DIR = os.path.dirname(os.path.abspath(__file__))
PREV_DIR = APP_DIR + ".prev"
SERVICE = "astra-usb-monitor"


def pick_asset(release):
    """Извлечь (tarball_url, sha256_url) из ответа релиза, или None.

    Оба файла обязательны: без контрольной суммы устанавливать ничего не будем.
    """
    tarball = checksum = None
    for asset in release.get("assets", []):
        name = asset.get("name", "")
        url = asset.get("browser_download_url")
        if name.endswith(".sha256"):
            checksum = url
        elif name.endswith(".tar.gz"):
            tarball = url
    if tarball and checksum:
        return tarball, checksum
    return None


def needs_update(current, latest):
    """Сравнить теги на неравенство, не на порядок: цель — перейти
    на то, что GitHub отметил как latest, поэтому отозванный релиз
    исправляется публикацией предыдущего вместо трудных переговоров с хостом."""
    if not latest:
        return False
    return current != latest
