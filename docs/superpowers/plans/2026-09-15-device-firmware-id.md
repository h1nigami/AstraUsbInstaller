# Device firmware ID Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Определять регистратор по его числовому ID, заданному оператором, без файлов-маркеров и сохранять выгрузку в `Device{id}`.

**Architecture:** Python и Avalonia независимо читают один и тот же ID из журнала `LOG` и имени свежей записи. Общая схема SQLite использует этот ID как `devices.id` и `id_source = 'device'`; прежние записи без отметки не переиспользуются. Главный экран получает числовую подпись из результата регистрации, остальные пользовательские имена остаются в справочниках.

**Tech Stack:** Python stdlib, Tkinter, SQLite; .NET, Avalonia, Microsoft.Data.Sqlite, xUnit. Новых зависимостей нет.

**Spec:** `docs/superpowers/specs/2026-09-15-device-firmware-id-design.md`

## Global Constraints

- ID: ASCII-десятичное число от 1 до `9223372036854775807`; `devices.id` и `{id}` в `Device{id}` равны этому числу.
- Два допустимых разных ID из `LOG` и имени свежей записи означают ошибку. Один допустимый ID используется, отсутствие обоих останавливает выгрузку.
- Не читать и не писать `.astra_id` и `.bestcam_id` ради идентификации. Не удалять уже существующие файлы на носителе; исключать их из сканирования и копирования.
- При ошибке ID не создавать архивную папку, не копировать и не удалять исходные видео. Старые строки базы и папки архива не мигрировать автоматически.
- Отметка новой строки: `devices.id_source TEXT DEFAULT ''`, значение `device`. Занятый старой строкой числовой ID означает отказ.
- На главной плитке после регистрации только число, до неё состояние «Определение ID» без имени. Поиск и вкладка «Устройства» могут показывать пользовательское имя.
- После каждого тестового цикла проверять затронутые старые тесты. Перед коммитом Python запускать `py_compile` и весь `unittest`; C# запускать `dotnet test`.

---

### Task 1: Чтение ID в Python

**Files:**
- Modify: `usb_monitor.py` (рядом с текущей `_read_device_id_from_usb`)
- Test: `tests/test_device_registry.py` (новый `DeviceIdentityReaderTest`)

**Interfaces:**
- Produces: `_read_device_id(mountpoint: str) -> int`; `OSError` при отсутствии, противоречии или ошибке чтения. Task 2 вызывает эту функцию до регистрации.

- [ ] **Step 1: Написать падающий тест.** Сначала проверить ID из журнала. После зелёного цикла отдельными красными тестами проверить ID из записи и противоречие.

```python
def test_reads_device_id_from_log(self):
    with tempfile.TemporaryDirectory() as mount:
        os.makedirs(os.path.join(mount, "LOG"))
        with open(os.path.join(mount, "LOG", "boot.txt"), "w") as out:
            out.write("#ID:1234567 #Включение системы\n")
        self.assertEqual(getattr(um, "_read_device_id", lambda _: None)(mount), 1234567)
```

- [ ] **Step 2: Проверить красный тест.** `python -m unittest tests.test_device_registry.DeviceIdentityReaderTest.test_reads_device_id_from_log -v`; ожидается `AssertionError`: без функции результат `None`.
- [ ] **Step 3: Реализовать минимальный читатель.** Читать только `.txt` из `LOG` и распознаваемые имена из `DCIM`; отбирать самый свежий журнал по имени, запись по времени в имени. Существующий `RecordingName` в C# служит образцом формата.

```python
def _read_device_id(mountpoint):
    log_id = _id_from_latest_log(mountpoint)
    file_id = _id_from_latest_recording(mountpoint)
    if log_id and file_id and log_id != file_id:
        raise OSError(f"Разные ID регистратора: {log_id} и {file_id}")
    device_id = log_id or file_id
    if not device_id:
        raise OSError("ID регистратора не найден")
    return device_id
```

- [ ] **Step 4: Дополнить тесты и проверить зелёный цикл.** `_id_from_latest_log` читает первые 50 строк самого свежего `.txt` в `LOG` и ищет `#ID:`; `_id_from_latest_recording` берёт ID из самого свежего имени `A11_{id}_{person}_{yyyyMMddHHmmss}_{seq}.mp4` в `DCIM`. Оба вызывают `_positive_id` ниже. Ошибка чтения существующего файла или перечисления каталога вызывает `OSError`. По одному красному и зелёному циклу для ID из записи, совпадения, противоречия, отсутствия, нуля, выхода за int64 и ошибки чтения. Запуск: `python -m unittest tests.test_device_registry.DeviceIdentityReaderTest -v`.

```python
def _positive_id(text):
    if not text or not text.isascii() or not text.isdigit():
        return None
    value = int(text)
    return value if 0 < value <= 9223372036854775807 else None
```
- [ ] **Step 5: Коммит.** `git add usb_monitor.py tests/test_device_registry.py` и `git commit -m "Читать ID регистратора из журнала и записей"` после полной проверки Python.

### Task 2: База и защита регистрации в Python

**Files:**
- Modify: `usb_monitor.py` (`_init_db`, `_resolve_device_id`, `copy_task`, `_scan_drive`, `_copy_files`)
- Test: `tests/test_device_registry.py`, `tests/test_shared_camera_identity.py`, `tests/test_camera_simulation.py`

**Interfaces:**
- Consumes: `_read_device_id(mountpoint: str) -> int` из Task 1.
- Produces: `_resolve_device_id(...) -> int` из ID регистратора; `copy_task` передаёт этот же int в БД и путь архива.

- [ ] **Step 1: Написать падающие тесты.** Тест на повторное подключение с одним ID, старый `.astra_id` с другим числом и занятый старой строкой `devices.id`.

```python
def test_device_id_wins_over_old_astra_marker(self):
    with tempfile.TemporaryDirectory() as mount:
        os.makedirs(os.path.join(mount, "DCIM"))
        with open(os.path.join(mount, "DCIM", "A11_1234567_222222_20260915120000_0001.mp4"), "wb"):
            pass
        with open(os.path.join(mount, ".astra_id"), "w") as out:
            out.write("999\n")
        self.assertEqual(um._resolve_device_id(self.conn, mount, "SAME", "CAM", "sdb1"), 1234567)
        self.assertEqual(open(os.path.join(mount, ".astra_id")).read(), "999\n")
```

- [ ] **Step 2: Проверить красный тест.** `python -m unittest tests.test_device_registry.ResolveDeviceIdTest.test_device_id_wins_over_old_astra_marker -v`; прежний код вернёт `999`.
- [ ] **Step 3: Реализовать регистрацию.** `id_source` добавляется через `ALTER TABLE` с обработкой только ошибки уже существующей колонки. Под текущей блокировкой прочитанный ID резервируется за `devname`; новая строка вставляется с `id_source='device'`, существующая без отметки вызывает `OSError`. Старое чтение и запись `.astra_id` удаляются из пути выполнения, проверка пропажи носителя остаётся.

```python
device_id = _read_device_id(mountpoint)
row = conn.execute("SELECT id_source FROM devices WHERE id = ?", (device_id,)).fetchone()
if row and row[0] != "device":
    raise OSError(f"ID {device_id} занят прежней записью")
if not row:
    effective_serial = serial or f"DEVICE_ID_{device_id}"
    if conn.execute("SELECT 1 FROM devices WHERE serial = ?", (effective_serial,)).fetchone():
        effective_serial = f"{effective_serial}#{device_id}"
    conn.execute("INSERT INTO devices (id, serial, label, first_seen, last_seen, id_source) VALUES (?, ?, ?, ?, ?, 'device')",
                 (device_id, effective_serial, label or devname, now, now))
```

- [ ] **Step 4: Проверить безопасность выгрузки.** Для отсутствующего ID, конфликта источников и дубликата одновременно подключённых носителей проверить, что `_copy_files` и `_delete_source_videos` не вызваны, архивной папки нет. Служебные `.astra_id` и `.bestcam_id` не входят в `_scan_drive` и `_copy_files`. Старые marker-тесты заменить ожиданиями нового поведения, не оставлять тестов записи файла. Запуск: `python -m unittest discover -s tests -v` и `python -m py_compile gui.py usb_monitor.py main.py updater.py`.
- [ ] **Step 5: Коммит.** `git add usb_monitor.py tests` и `git commit -m "Привязать архив к ID регистратора"` после зелёной проверки.

### Task 3: Главный экран Python

**Files:**
- Modify: `usb_monitor.py` (`copy_task` и события очереди)
- Modify: `gui.py` (`_poll_queue`, `_refresh_workers`)
- Test: `tests/test_camera_simulation.py`

**Interfaces:**
- Consumes: числовой `device_id` из Task 2.
- Produces: `display_id=str(device_id)` в событиях главной плитки; путь архива по-прежнему `Device{device_id}`.

- [ ] **Step 1: Написать падающий тест на событие главного экрана.**

```python
self.assertEqual(done[0][1], "1234567")
self.assertEqual(gui.workers_data[1234567]["device"], "1234567")
self.assertTrue((destination / "Device1234567").is_dir())
```

- [ ] **Step 2: Проверить красный тест.** `python -m unittest tests.test_camera_simulation -v`; прежняя очередь передаёт `Astra ID ...` с именем.
- [ ] **Step 3: Передавать на главную плитку только ID.** Оставить `_friendly_device_label` для поиска; для `_emit` и итоговых событий использовать `str(device_id)`. До идентификации выводить «Определение ID», а ошибку выводить с пустой подписью, без `devname`.

```python
display_id = f"Device{device_id}"
main_label = str(device_id)
progress_queue.put_nowait((device_id, main_label, state, current, total, msg, devname))
```

- [ ] **Step 4: Проверить сквозную симуляцию и весь Python.** `python -m unittest discover -s tests -v`; `python -m py_compile gui.py usb_monitor.py main.py updater.py`.
- [ ] **Step 5: Коммит.** `git add usb_monitor.py gui.py tests/test_camera_simulation.py` и `git commit -m "Показывать на главном экране только ID"`.

### Task 4: Чтение ID в Avalonia

**Files:**
- Create: `avalonia/AstraUsb/Services/DeviceIdentifier.cs` (чтение `LOG`, согласование с `RecordingName`)
- Modify: `avalonia/AstraUsb/Services/RecordingName.cs` (не скрывать ошибку обхода существующего `DCIM`)
- Test: `avalonia/AstraUsb.Tests/DeviceIdentifierTests.cs`, `RecordingNameTests.cs`

**Interfaces:**
- Consumes: `RecordingName.FromCard(string? mountPoint) -> RecordingInfo?`.
- Produces: `DeviceIdentifier.Read(string mountPoint) -> long`; `InvalidDataException` при отсутствии или противоречии, `IOException` при ошибке чтения существующего источника.

- [ ] **Step 1: Написать падающий xUnit-тест.** Сначала проверить наличие читателя через reflection, чтобы сборка тестов до реализации проходила и давала обычный провал проверки.

```csharp
var reader = typeof(RecordingName).Assembly.GetType("AstraUsb.Services.DeviceIdentifier");
Assert.NotNull(reader);
```

- [ ] **Step 2: Проверить красный тест.** `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj --filter FullyQualifiedName~DeviceIdentifierTests`; ожидается `Assert.NotNull` на отсутствующем классе.
- [ ] **Step 3: Добавить читатель на базе старого `DeviceIdentifier` из `33c4a7a`.** Не возвращать путь через маркер. Для ID использовать `long.TryParse` с `CultureInfo.InvariantCulture` и проверкой ASCII-цифр; читать ограниченное начало свежего `.txt` в `LOG` и `RecordingName.FromCard`.

```csharp
public static long Read(string mountPoint)
{
    var logId = ReadLogId(mountPoint);
    var recordingId = Directory.Exists(Path.Combine(mountPoint, "DCIM"))
        ? ParseId(RecordingName.FromCard(mountPoint)?.DeviceNo) : null;
    if (logId is > 0 && recordingId is > 0 && logId != recordingId)
        throw new InvalidDataException($"Разные ID регистратора: {logId} и {recordingId}");
    return logId ?? recordingId ?? throw new InvalidDataException("ID регистратора не найден");
}
```

- [ ] **Step 4: Проверить остальные границы.** `ReadLogId` читает первые 50 строк свежего `.txt` в `LOG`; `ParseId` принимает только ASCII-цифры и `long > 0`. Вызов `RecordingName.FromCard` делать только при существующем `DCIM`, чтобы файлы вне него не стали источником ID. Для ошибки чтения существующего журнала или перечисления `DCIM` выбрасывается `IOException`. `RecordingName.FromCard` сейчас скрывает ошибки обхода, поэтому изменить его так, чтобы существующий, но нечитаемый `DCIM` завершался `IOException`. После зелёного первого теста добавить прямые тесты `DeviceIdentifier.Read` для ID из журнала, имени, совпадения, противоречия, отсутствия, нуля, выхода за int64 и неисправного источника; каждый проверить красным до очередной правки. `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj --filter FullyQualifiedName~DeviceIdentifierTests`.

```csharp
private static long? ParseId(string? text) =>
    text is { Length: > 0 }
    && text.All(c => c is >= '0' and <= '9')
    && long.TryParse(text, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var value)
    && value > 0 ? value : null;
```
- [ ] **Step 5: Коммит.** `git add avalonia/AstraUsb/Services/DeviceIdentifier.cs avalonia/AstraUsb/Services/RecordingName.cs avalonia/AstraUsb.Tests/DeviceIdentifierTests.cs avalonia/AstraUsb.Tests/RecordingNameTests.cs` и `git commit -m "Читать ID регистратора в Avalonia"` после `dotnet test` всего проекта.

### Task 5: Регистрация и плитка Avalonia

**Files:**
- Modify: `avalonia/AstraUsb/Services/DeviceRegistry.cs` (`InitSchema`, `ResolveByCard`; старые методы чтения и записи маркера удалить)
- Modify: `avalonia/AstraUsb/ViewModels/MainWindowViewModel.cs` (`ReadCard`, `Apply`)
- Modify: `avalonia/AstraUsb/Services/Markers.cs` (сохранять исключение старых файлов)
- Test: `avalonia/AstraUsb.Tests/DeviceRegistryTests.cs`, `CardIdentityTests.cs`, `CardIsTheOnlyKeyTests.cs`, `MainWindowViewModelTests.cs`, `ArchiveSearchTests.cs`, `DevicesViewModelTests.cs`, `OperatorFormsTests.cs`, `StaffDirectoryTests.cs`, `FileCopierTests.cs`

**Interfaces:**
- Consumes: `DeviceIdentifier.Read(mountPoint) -> long` из Task 4.
- Produces: `ResolveByCard(...) -> long` с прямым числовым ID; `CardInfo.CameraId` содержит только число.

- [ ] **Step 1: Написать падающий тест.** Для карты с ID `1234567` в имени записи и `.astra_id` со значением `999` проверить `devices.id == 1234567`, неизменность старого файла и `id_source='device'`.

```csharp
Assert.Equal(1234567, registry.ResolveByCard(card, 1, "CAM", "sdb1"));
Assert.Equal("999\n", File.ReadAllText(Path.Combine(card, ".astra_id")));
using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "devices.db")}");
db.Open();
using var cmd = db.CreateCommand();
cmd.CommandText = "SELECT id_source FROM devices WHERE id = 1234567";
Assert.Equal("device", cmd.ExecuteScalar());
```

- [ ] **Step 2: Проверить красный тест.** `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj --filter FullyQualifiedName~DeviceRegistryTests`; прежний код вернёт `999`.
- [ ] **Step 3: Поменять регистрацию и подпись.** В `InitSchema` добавить `id_source`; `IdSourceOf(long id)` читает эту колонку через `Scalar`. `ResolveByCard` берёт `DeviceIdentifier.Read`, отклоняет старую строку с тем же `id` и создаёт новую с `serial = $"DEVICE_ID_{id}"` (C# путь не получает USB-серийник). `MainWindowViewModel.ReadCard` возвращает `CardInfo(id, id.ToString(), $"Папка Device{id}", ...)`; в `Apply` до ID вместо `device.Name` ставить пустую подпись и «Определение ID», при ошибке также оставить подпись пустой. Дубликат показывать как «Дубликат ID устройства {id}». Константу старого файла в `Markers` оставить строкой `.astra_id`, без обращения к удалённому `DeviceRegistry.DeviceIdFile`.

```csharp
var id = DeviceIdentifier.Read(mountPoint!);
if (DeviceExists(id) && IdSourceOf(id) != "device")
    throw new InvalidDataException($"ID {id} занят прежней записью");
var info = new CardInfo(id, id.ToString(), $"Папка Device{id}", personnel, employee, department);

private string? IdSourceOf(long id) =>
    Scalar("SELECT id_source FROM devices WHERE id = $id", ("$id", id)) as string;
```

- [ ] **Step 4: Закрыть регрессии.** Тесты `CardIdentityTests`, `CardIsTheOnlyKeyTests` и marker-сценарии `DeviceRegistryTests` перевести на отказ без ID или игнорирование старого маркера. В `ArchiveSearchTests`, `DevicesViewModelTests`, `OperatorFormsTests` и `StaffDirectoryTests` заменить `ResolveByCard(null, ...)` на временную карту с именем записи `A11_1234567_222222_20260915120000_0001.mp4`, задавая разные ID для разных записей. `MainWindowViewModelTests` проверяет одну числовую подпись, ошибку без ID и одновременный дубликат. `FileCopierTests` использует литерал `.astra_id` как исключаемый файл. Проверить конфликт старой БД, отсутствие старых маркеров в копиях и неизменность источника. Запуск: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj` и `dotnet build avalonia/AstraUsb/AstraUsb.csproj`.
- [ ] **Step 5: Коммит.** `git add avalonia/AstraUsb avalonia/AstraUsb.Tests` и `git commit -m "Привязать Avalonia к ID регистратора"`.

### Task 6: Документация и общая проверка

**Files:**
- Modify: `README.md` (описание назначения ID человеком и отказа без него)
- Modify: `AGENTS.md`, `CLAUDE.md` (заменить устаревшие инварианты `.astra_id`)
- Test: обе полные тестовые команды ниже

**Interfaces:** Нет новых API.

- [ ] **Step 1: Исправить только разделы с прежним источником ID.** Описать прямой `Device{id}`, ручное назначение уникального ID, ошибку без ID, сохранение старых архивов и маркеров без автоматической миграции. Не менять правила релизов и установки.
- [ ] **Step 2: Проверить текст по humanizer, затем прогнать `git diff --check`.** Убедиться, что документация не обещает различать два устройства с одинаковым ID, подключённые по очереди.
- [ ] **Step 3: Выполнить полную проверку.** `python -m py_compile gui.py usb_monitor.py main.py updater.py`; `python -m unittest discover -s tests -v`; `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj`; `dotnet build avalonia/AstraUsb/AstraUsb.csproj`. Если ошибка теста обнаружит новый дефект, применить systematic-debugging и тестовый цикл до исправления.
- [ ] **Step 4: Ревью и коммит.** Проверить `git diff --check`, отсутствие новых файлов `.astra_id`/`.bestcam_id` в тестовых источниках, выполнить ponytail-review и code-review. Коммит: `git add README.md AGENTS.md CLAUDE.md` и `git commit -m "Обновить правила ID регистратора"`.
