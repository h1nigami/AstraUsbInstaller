"""Публикация стабильной Avalonia; Python и Viewer имеют другие каналы."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import time
import urllib.error
import urllib.parse
import urllib.request


BASE = "https://download.neuroii.ru"
PRODUCT = "bestcam-station"
API = f"/api/v1/products/{PRODUCT}"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Запрос с токеном не должен уходить на адрес из ответа сервера.
        return None


def validated_files(release, directory):
    tag = release["tag_name"]
    if (not re.fullmatch(r"v2\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag)
            or release.get("draft") is not False or release.get("prerelease") is not False):
        raise ValueError("Нужен стабильный релиз Avalonia с тегом v2.X.Y")
    version = tag[1:]
    packages = {
        f"bestcam-station-{tag}-linux-x64.tar.gz",
        f"bestcam-station-{tag}-linux-arm64.tar.gz",
        f"bestcam-station-{tag}-win-x64.zip",
        f"bestcam-station_{version}_amd64.deb",
        f"bestcam-station_{version}_arm64.deb",
        f"BestCamStationSetup-{tag}.exe",
    }
    expected = packages | {name + ".sha256" for name in packages}
    assets = release["assets"]
    if len(assets) != len(expected) or {a["name"] for a in assets} != expected:
        raise ValueError("Состав GitHub-релиза должен содержать только 12 вложений Station")
    if {p.name for p in directory.iterdir()} != expected:
        raise ValueError("Скачан неполный набор или посторонние файлы")
    files = {}
    for asset in assets:
        path = directory / asset["name"]
        if path.is_symlink() or not path.is_file() or path.stat().st_size != asset["size"]:
            raise ValueError(f"Неверный файл или размер: {path.name}")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if asset.get("digest") and asset["digest"] != "sha256:" + digest:
            raise ValueError(f"Не совпала сумма GitHub: {path.name}")
        files[path.name] = (path, digest, path.stat().st_size)
    for name in packages:
        checksum = (directory / (name + ".sha256")).read_text(encoding="ascii").strip()
        if checksum != files[name][1] + "  " + name:
            raise ValueError(f"Неверная контрольная сумма или имя в {name}.sha256")
    return version, files


def current_version(base):
    # Каталог кэшируется на 60 секунд; издателю нужен свежий указатель stable.
    request = urllib.request.Request(
        base + f"/api/v1/catalog?publication={time.time_ns()}",
        headers={"Cache-Control": "no-cache"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        products = json.load(response)["products"]
    return next((p["version"] for p in products if p["slug"] == PRODUCT), None)


def version_key(version):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Не удалось сравнить версию stable: " + version)
    return tuple(map(int, version.split(".")))


def verify_public(base, version, files):
    for name, (_, digest, size) in sorted(files.items()):
        with urllib.request.urlopen(f"{base}/{PRODUCT}/{version}/{name}", timeout=120) as response:
            actual = hashlib.sha256()
            total = 0
            for block in iter(lambda: response.read(1024 * 1024), b""):
                actual.update(block)
                total += len(block)
        if total != size or actual.hexdigest() != digest:
            raise ValueError(f"Опубликованный файл отличается: {name}")
        print(f"SHA-256 OK: {name}", flush=True)


def mutate(base, token, method, path, *, payload=None, file=None, duplicate_ok=False):
    headers = {"Authorization": "Bearer " + token}
    if file:
        body = file.open("rb")
        headers["Content-Length"] = str(file.stat().st_size)
    else:
        body = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(base + API + path, data=body, method=method, headers=headers)
    try:
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=600) as response:
            response.read()
    except urllib.error.HTTPError as exc:
        exc.close()
        if not (duplicate_ok and exc.code == 409):
            raise RuntimeError(f"API Station: HTTP {exc.code}, {method} {path}") from None
    finally:
        if file:
            body.close()


def publish(release, directory, base=BASE, token="", *, check_only=False):
    version, files = validated_files(release, directory)
    current = current_version(base)
    if current and version_key(current) > version_key(version):
        return "superseded"
    if current == version:
        verify_public(base, version, files)
        return "unchanged"
    if check_only:
        return "checked"
    if not token:
        raise ValueError("Не задан DOWNLOAD_STATION_TOKEN")
    mutate(base, token, "POST", "/releases", payload={"version": version}, duplicate_ok=True)
    for name, (path, digest, size) in sorted(files.items()):
        query = urllib.parse.urlencode({"sha256": digest, "size": size})
        mutate(base, token, "PUT", f"/releases/{version}/files/{name}?{query}",
               file=path, duplicate_ok=True)
        print(f"Загружен или уже существует: {name}", flush=True)
    mutate(base, token, "PUT", "/channels/pilot", payload={"version": version})
    verify_public(base, version, files)
    current = current_version(base)
    if current and version_key(current) > version_key(version):
        return "superseded"
    mutate(base, token, "PUT", "/channels/stable", payload={"version": version})
    return "published"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release", type=Path)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--check", action="store_true", help="проверить без записи и продвижения")
    args = parser.parse_args()
    release = json.loads(args.release.read_text(encoding="utf-8-sig"))
    result = publish(release, args.directory, token=os.environ.get("DOWNLOAD_STATION_TOKEN", ""),
                     check_only=args.check)
    print(f"Station {release['tag_name']}: {result}", flush=True)


if __name__ == "__main__":
    main()
