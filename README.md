# Astra USB Installer / USB Backup Manager

Автоматический мониторинг USB-устройств, создание резервных копий файлов и управление устройствами через GUI.

## Возможности

- Автоопределение новых USB-устройств (Linux / Windows)
- Монтирование, сканирование и копирование файлов
- Запись `.astra_id` на USB для идентификации устройства
- GUI на Tkinter (вкладки: поиск, устройства, workers)
- Headless-режим (Docker, сервер без экрана)
- Поддержка нескольких USB одновременно (ThreadPoolExecutor)
- SQLite-база: история устройств и бэкапов
- Поиск по дате, устройству, человеку

## Быстрый старт (Docker)

```bash
docker compose up -d --build
```

Для работы с GUI на хосте нужен X-сервер. Docker пробрасывает `/tmp/.X11-unix`.

## Использование без Docker

```bash
pip install -r requirements.txt
python main.py          # автоопределение: GUI или headless
python usb_monitor.py   # принудительно headless
```

## Переменные окружения

| Переменная | По умолчанию | Описание |
|---|---|---|
| `USB_BACKUP_DEST` | `./USB_Backups` | Куда сохранять бэкапы |
| `USB_DB_PATH` | `./data/devices.db` | Путь к SQLite БД |
| `USB_MAX_WORKERS` | `10` | Потоков для параллельной работы |
| `USB_DEBUG` | `0` | Включить отладку (1) |
| `DISPLAY` | `:0` | X11 display для GUI |

## Структура проекта

- `usb_monitor.py` — ядро: мониторинг, монтирование, копирование, SQLite
- `gui.py` — графический интерфейс (Tkinter)
- `main.py` — точка входа (GUI → headless fallback)
- `start.sh` — entrypoint для Docker
- `docker-compose.yml` — продакшн-развёртывание
- `Dockerfile` — сборка образа
- `requirements.txt` — зависимости Python
