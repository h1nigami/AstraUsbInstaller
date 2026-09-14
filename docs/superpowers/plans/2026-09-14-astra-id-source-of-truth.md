# Astra ID Source of Truth Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать `.astra_id` единым идентификатором в Python и Avalonia, показать его на основном экране и вернуть пользователю доступ к архивным папкам.

**Architecture:** Обе версии читают положительный числовой Astra ID с носителя и выдают новый номер только через локальный `devices.id AUTOINCREMENT`. Avalonia использует `.bestcam_id` только как одноразовый мост к существующей записи. Архив остаётся в `Device{astra_id}`, а Linux-службы передают каталоги `Device*` владельцу корня архива.

**Tech Stack:** Python 3 stdlib, SQLite, .NET 8, Avalonia 11.2.3, Microsoft.Data.Sqlite 8.0.11, xUnit, Bash, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-14-astra-id-source-of-truth-design.md`

## Global Constraints

- `.astra_id` содержит положительное целое число и является единственным постоянным идентификатором.
- USB-серийник, номер из имени записи и `.bestcam_id` не могут заменить существующий Astra ID.
- Новый номер выдаёт только локальный SQLite `devices.id INTEGER PRIMARY KEY AUTOINCREMENT`.
- Если новый Astra ID нельзя записать и перечитать, копирование и автоудаление не начинаются.
- Одновременный дубликат `.astra_id` даёт ошибку и не перенумеровывается.
- Архивная папка остаётся `Device{astra_id}`.
- На основном экране показывается `Astra ID {id}`, пользовательское имя остаётся дополнительной подписью.
- На Linux владелец и группа папок `Device*` наследуются от корня архива. Другие каталоги не затрагиваются.
- Новые зависимости запрещены.
- Человекочитаемый текст пишется по-русски, без длинных тире.

---

### Task 1: Python использует только Astra ID

**Files:**
- Modify: `usb_monitor.py:478-607,986-1104,monitor_usb`
- Modify: `gui.py:14,785-795`
- Modify: `tests/test_device_registry.py:36-141`
- Modify: `tests/test_shared_camera_identity.py`
- Test: `tests/test_dest_and_cleanup.py`

**Interfaces:**
- Consumes: таблицу `devices` и файл `DEVICE_ID_FILE = ".astra_id"`.
- Produces: `_resolve_device_id(...) -> int`, `_friendly_device_label(id, name) -> str`, `_repair_archive_ownership(root, device_dir=None) -> None`.

- [ ] **Step 1: Написать падающие тесты идентификации**

Изменить тесты так, чтобы они требовали следующие результаты:

```python
def test_existing_marker_is_the_only_identity(self):
    um._write_device_id_to_usb(mp, 999)
    self.assertEqual(
        um._resolve_device_id(self.conn, mp, "CONFLICTING_SERIAL", "CAM", "sdb1"),
        999,
    )

def test_same_serial_without_marker_gets_next_local_ids(self):
    self.assertEqual((first_id, second_id), (1, 2))

def test_corrupt_existing_marker_is_rejected(self):
    with open(os.path.join(mp, um.DEVICE_ID_FILE), "w", encoding="utf-8") as stream:
        stream.write("broken")
    with self.assertRaisesRegex(OSError, "Некорректный Astra ID"):
        um._resolve_device_id(self.conn, mp, "SER", "CAM", "sdb1")

def test_duplicate_connected_marker_is_rejected_without_rewrite(self):
    with self.assertRaisesRegex(OSError, "Дубликат Astra ID 123456"):
        self.resolve(second, "sdc1")
    self.assertEqual(um._read_device_id_from_usb(second), 123456)
```

- [ ] **Step 2: Проверить падение тестов**

Run: `python -m unittest tests.test_device_registry tests.test_shared_camera_identity -v`

Expected: тест повреждённого маркера и тест дубликата падают, старые ожидания автоматического перенумерования больше не выполняются.

- [ ] **Step 3: Сделать чтение и выдачу Astra ID строгими**

Минимальная логика:

```python
def _read_device_id_from_usb(mountpoint):
    if not mountpoint:
        return None
    path = os.path.join(mountpoint, DEVICE_ID_FILE)
    try:
        with open(path, encoding="utf-8") as stream:
            value = stream.read().strip()
    except FileNotFoundError:
        return None
    except OSError as error:
        raise OSError(f"Не удалось прочитать Astra ID: {error}") from error
    if not value.isdigit() or int(value) <= 0:
        raise OSError(f"Некорректный Astra ID в {DEVICE_ID_FILE}")
    return int(value)
```

В `_resolve_device_id` оставить выдачу нового номера через `_create_device`, но заменить ветку дубликата:

```python
if duplicate:
    raise OSError(f"Дубликат Astra ID {id_from_usb}: {devname}")
device_id = _create_device(conn, serial, label, devname) if id_from_usb is None else id_from_usb
```

Записывать маркер только когда его не было. После записи обязательно перечитать и сравнить значение. USB-серийник остаётся только в колонке `serial`.

- [ ] **Step 4: Сделать подпись однозначной**

```python
def _friendly_device_label(device_id, name):
    astra_id = f"Astra ID {device_id}"
    return f"{astra_id} · {name}" if name else astra_id
```

В `gui.py` использовать тот же формат для списков, поиска, назначения и основной доски. Путь продолжает собираться как `f"Device{device_id}"`.

- [ ] **Step 5: Добавить восстановление владельца архива**

В `usb_monitor.py` добавить функцию без shell-строки:

```python
def _repair_archive_ownership(root, device_dir=None):
    if platform.system() == "Windows" or not root:
        return
    candidates = [device_dir] if device_dir else [
        entry.path for entry in os.scandir(root)
        if entry.is_dir(follow_symlinks=False)
        and entry.name.startswith("Device")
        and entry.name[6:].isdigit()
    ]
    root = os.path.realpath(root)
    for path in candidates:
        path = os.path.realpath(path)
        if os.path.dirname(path) != root or not os.path.basename(path)[6:].isdigit():
            continue
        subprocess.run(
            ["chown", "-R", f"--reference={root}", "--", path],
            check=False, capture_output=True,
        )
```

Вызвать её один раз при запуске мониторинга для существующих папок и после `_copy_files` для папки текущего устройства. Добавить тест с подменой `platform.system`, `os.scandir` и `subprocess.run`, который подтверждает, что команда получает только прямые папки `Device` с числовым суффиксом.

- [ ] **Step 6: Проверить Python**

Run: `python -m py_compile gui.py usb_monitor.py main.py updater.py`

Run: `python -m unittest discover -s tests -v`

Expected: обе команды завершаются с кодом 0.

- [ ] **Step 7: Зафиксировать задачу**

```bash
git add usb_monitor.py gui.py tests/test_device_registry.py tests/test_shared_camera_identity.py tests/test_dest_and_cleanup.py
git commit -m "Сделать Astra ID источником истины в Python"
```

---

### Task 2: Avalonia переходит на Astra ID

**Files:**
- Modify: `avalonia/AstraUsb/Services/DeviceRegistry.cs`
- Modify: `avalonia/AstraUsb/Services/ArchiveGuard.cs`
- Modify: `avalonia/AstraUsb/Services/BackupService.cs`
- Modify: `avalonia/AstraUsb/ViewModels/MainWindowViewModel.cs`
- Modify: `avalonia/AstraUsb/ViewModels/PortViewModel.cs`
- Modify: `avalonia/AstraUsb/Views/MainWindow.axaml`
- Modify: `avalonia/AstraUsb.Tests/DeviceRegistryTests.cs`
- Modify: `avalonia/AstraUsb.Tests/CardIsTheOnlyKeyTests.cs`
- Modify: `avalonia/AstraUsb.Tests/MainWindowViewModelTests.cs`
- Modify: `avalonia/AstraUsb.Tests/MainWindowTests.cs`
- Modify: `avalonia/AstraUsb.Tests/ArchiveGuardTests.cs`

**Interfaces:**
- Consumes: `.astra_id`, локальный `devices.id`, совместимый `.bestcam_id` и `DeviceRegistry.FriendlyLabel`.
- Produces: `ResolveDeviceId(...) -> long`, совместимый `ResolveByCard(...) -> long`, `ArchiveGuard.RepairOwnership(root, deviceDir = null)` и `PortViewModel.CameraLine`.

- [ ] **Step 1: Написать падающие тесты реестра**

Проверить такие сценарии:

```csharp
[Fact]
public void Same_serial_without_markers_gets_next_local_ids()
{
    var first = registry.ResolveDeviceId(Mount("one"), "SHARED", "L", "sda1");
    var second = registry.ResolveDeviceId(Mount("two"), "SHARED", "L", "sdb1");
    Assert.Equal((1L, 2L), (first, second));
}

[Fact]
public void Astra_marker_wins_over_legacy_bestcam_id()
{
    var card = Mount("card", astraId: 999);
    CardIdentity.Write(card, "BCU-01-0001");
    Assert.Equal(999, registry.ResolveByCard(card, 1, "CAM", "sdb1"));
}

[Fact]
public void Known_legacy_marker_is_migrated_to_astra_id()
{
    var old = Card("old", "BCU-01-0001");
    var existing = SeedLegacyDevice("BCU-01-0001", "64001");
    Assert.Equal(existing, registry.ResolveByCard(old, 1, "CAM", "sdb1"));
    Assert.Equal(existing, DeviceRegistry.ReadDeviceIdFromUsb(old));
}

[Fact]
public void Corrupt_astra_marker_is_rejected()
{
    Assert.Throws<InvalidDataException>(() =>
        registry.ResolveDeviceId(card, "SER", "CAM", "sdb1"));
}
```

Для подготовки старой записи использовать локальную базу теста, без нового
производственного API:

```csharp
private long SeedLegacyDevice(string firmwareId, string name)
{
    using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "devices.db")}");
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        INSERT INTO devices (serial, label, name, first_seen, last_seen, firmware_id)
        VALUES ($serial, 'CAM', $name, '2026-09-14T10:00:00',
                '2026-09-14T10:00:00', $firmware);
        SELECT last_insert_rowid();
        """;
    cmd.Parameters.AddWithValue("$serial", $"CARD_{firmwareId}");
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$firmware", firmwareId);
    return (long)(cmd.ExecuteScalar() ?? 0L);
}
```

- [ ] **Step 2: Проверить падение тестов реестра**

Run: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj --filter "DeviceRegistryTests|CardIsTheOnlyKeyTests"`

Expected: тест одинакового серийника и тесты перехода на `.astra_id` падают.

- [ ] **Step 3: Свести разрешение ID к одному пути**

Изменить `ResolveDeviceId` так, чтобы существующий корректный `.astra_id` всегда побеждал, а носитель без маркера всегда получал новую строку через `CreateDevice`. Удалить поиск по серийнику из этого пути. После создания проверить запись маркера:

```csharp
private static void RequireAstraId(string? mountPoint, long id)
{
    WriteDeviceIdToUsb(mountPoint, id);
    if (ReadDeviceIdFromUsb(mountPoint) != id)
        throw new IOException($"Не удалось сохранить {DeviceIdFile}");
}
```

`ResolveByCard` сначала вызывает Astra-путь. Только при отсутствующем `.astra_id` он может найти строку по старому `firmware_id`, записать её локальный `devices.id` в `.astra_id` и затем продолжить через общий Astra-путь. Неизвестный `.bestcam_id` не создаёт идентичность и не выдаёт номер.

- [ ] **Step 4: Показать Astra ID и остановить одновременный дубликат**

Единая подпись:

```csharp
public static string FriendlyLabel(long deviceId, string? name)
{
    var astraId = $"Astra ID {deviceId}";
    return string.IsNullOrEmpty(name) ? astraId : $"{astraId} · {name}";
}
```

В `ReadCard` формировать `CardInfo.CameraId` через `FriendlyLabel(id, name)`. В `Apply` использовать локальный `HashSet<long>` для Astra ID подключённых носителей. Первый носитель запускает выгрузку, второй получает `PortState.Failed` и текст `Дубликат Astra ID {id}` без вызова `StartBackup`.

В `MainWindowViewModelTests` заранее заполнить приватный словарь `_identified`
двумя экземплярами `CardInfo` с одинаковым `DeviceId`, вызвать `Apply` для двух
точек монтирования и проверить:

```csharp
Assert.NotEqual(PortState.Failed, model.Ports[0].State);
Assert.Equal(PortState.Failed, model.Ports[1].State);
Assert.Contains("Дубликат Astra ID 7", model.Ports[1].Detail);
```

Добавить `CameraLine` в три шаблона `MainWindow.axaml`:

```xml
<TextBlock Text="{Binding CameraLine}" FontSize="11"
           FontWeight="SemiBold" TextTrimming="CharacterEllipsis"
           Foreground="{Binding InkMuted}" />
```

Сохранить `x:DataType="vm:PortViewModel"` на каждом `DataTemplate` и не добавлять конвертеры.

В `MainWindowTests` для значений `grid`, `list` и `rack` переключить
`SetLayoutCommand`, присвоить первому порту `CameraId = "Astra ID 7 · 64001"`
и проверить, что видимое дерево содержит эту строку. Так тест проверяет все три
шаблона, а сборка проверяет compiled binding.

- [ ] **Step 5: Восстановить владельца папок Avalonia**

В `ArchiveGuard` добавить проверку имени и запуск `chown` через `ProcessStartInfo.ArgumentList`:

```csharp
public static bool IsDeviceFolderName(string? name) =>
    name is { Length: > 6 }
    && name.StartsWith(DeviceRegistry.DeviceDirPrefix, StringComparison.Ordinal)
    && long.TryParse(name[6..], out var id)
    && id > 0;
```

`RepairOwnership` работает только на Linux, принимает только прямую дочернюю папку корня с именем `Device` и положительным числовым суффиксом, затем выполняет `chown -R --reference=<root> -- <deviceDir>` без shell. Вызвать функцию в конструкторе `BackupService` для старых папок и после `FileCopier.Copy` для текущей папки.

Тесты должны подтвердить фильтрацию `Device1`, `Device999`, `Device0`, `DeviceX`, `lost+found` и пути за пределами корня.

- [ ] **Step 6: Проверить Avalonia**

Run: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj`

Run: `dotnet build avalonia/AstraUsb/AstraUsb.csproj -c Release`

Expected: обе команды завершаются с кодом 0, compiled bindings для `CameraLine` проходят сборку.

- [ ] **Step 7: Зафиксировать задачу**

```bash
git add avalonia/AstraUsb avalonia/AstraUsb.Tests
git commit -m "Перевести Avalonia на Astra ID"
```

---

### Task 3: Документация и общая проверка

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: итоговое поведение Task 1 и Task 2.
- Produces: актуальное описание идентификации, папок, миграции и прав доступа.

- [ ] **Step 1: Обновить описание поведения**

В документации явно записать:

```text
.astra_id является единственным идентификатором устройства.
Носитель без маркера получает следующий локальный devices.id.
Одинаковый USB-серийник не объединяет устройства.
Avalonia читает .bestcam_id только при одноразовом переносе существующей записи.
Папка архива называется Device{astra_id}, а основной экран показывает тот же Astra ID.
Linux-служба передаёт папки Device* владельцу корня архива.
```

Удалить утверждения о выборе устройства по USB-серийнику и об автоматическом перенумеровании дубликата `.astra_id`.

- [ ] **Step 2: Выполнить полную локальную проверку**

Run: `python -m py_compile gui.py usb_monitor.py main.py updater.py`

Run: `python -m unittest discover -s tests -v`

Run: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj`

Run: `dotnet build avalonia/AstraUsb/AstraUsb.csproj -c Release`

Run: `bash -n install_native.sh start_native.sh avalonia/install_native.sh avalonia/start_native.sh`

Expected: все команды завершаются с кодом 0.

- [ ] **Step 3: Выполнить Ponytail-review**

Проверить итоговый diff на дублирование, лишние зависимости, новые абстракции с одним потребителем и изменение путей `Device{astra_id}`. Удалять можно только код, добавленный этим планом.

- [ ] **Step 4: Зафиксировать документацию**

```bash
git add README.md AGENTS.md CLAUDE.md
git commit -m "Описать единые правила Astra ID"
```

---

## Выпуск

После чистого ревью ветки:

1. Создать PR по шаблону предыдущих PR и слить его в `master`.
2. Убедиться, что `tests.yml` прошёл на merge commit.
3. Выпустить Python `v1.9` как stable. Проверить ровно два файла `astra-usb-monitor-v1.9.tar.gz` и `.sha256`, а также что C# jobs пропущены.
4. Выпустить Avalonia `v2.0.4` как prerelease. Проверить ровно 16 C#-файлов с SHA256, отсутствие Python-архива и сохранение `v1.9` в `releases/latest`.
5. Проверить `VERSION` внутри обоих архивов, SHA256 и точные SHA тегов.
6. Удалить из старого stable-релиза `v1.5` только ошибочно прикреплённые C# assets, оставив два Python-файла.
7. Не перезапускать старые station workflow для `v1.5` и `v1.6`.
