# План: нативный сервисный USB регистратора в Avalonia-станции

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Шаги — чекбоксы `- [ ]`.

**Goal:** Станция на Astra Linux читает и (по паритету с viewer) пишет настройки CMS-сервера A11 в сервисном режиме через `libusb-1.0`, без Windows-SDK.

**Architecture:** Чистый кодек кадра и (де)сериализация 256-байтной структуры — отдельный файл под юнит-тестами (эталон — снятые живьём ответы). Транспорт libusb — за интерфейсом, внешняя граница. Авто-настройка при подключении — перенос логики `VendorUsb.Configure` из viewer.

**Tech Stack:** C# / .NET 8, Avalonia, xUnit; `libusb-1.0.so.0` через P/Invoke.

**Spec:** `docs/superpowers/specs/2026-09-24-service-mode-usb-native.md`.

## Global Constraints

- Протокол — только подтверждённые факты (спека §«Протокол»). Транспорт: OUT `0x40/0x5A/0xAA`, IN `0xC0/0x59/0xAA`, пауза ~200 мс, IN по 1024 до `0D 0A`.
- Кадр: `7E | len16 LE (=payload+3) | cmd16 LE | 00 | payload | xor(7E..payload) | 0D 0A`. В запросе payload = префикс `"000000"` + данные; в ответе payload = статус(1 байт, 0=ок) + данные.
- Команды: вход `0x01` (без данных), чтение сервера `0x0D`, запись сервера `0x0C`.
- Структура сервера (данные `0x0D`-ответа после статуса и данные `0x0C`-запроса): `тип u8 @0 (2=CMSV6) · включение u8 @1 · домен1 12б @2 · домен2 12б @14 · IP ASCII 32б @26 (NUL-паддинг) · порт u16 LE @58 · 2 нулевых байта @60` = 62 байта.
- Эталонные векторы (снято с A11 24.09.2026): LOGIN-ответ `7e 04 00 01 00 00 00 7b 0d 0a`; SERVER-ответ `7e 42 00 0d 00 00 00 02 01 75 6b 6e 6f 77 6e 00 00 00 00 00 00 75 6b 6e 6f 77 6e 00 00 00 00 00 00 31 39 32 2e 31 36 38 2e 30 2e 39` + нули до IP@26+32 + `d0 19 00 00` + `d9 0d 0a`. Разбор SERVER: тип=2, вкл=1, домены `uknown`, IP `192.168.0.9`, порт 6608.
- Защита записи обязательна (спека §«Защита записи»): один A11 или отказ; read-after-write; guard длины; не писать при совпадении; домены/прочее сохраняются.
- Тесты — `dotnet test` из `avalonia/`, стиль xUnit как в `AstraUsb.Tests`. Сборка — `dotnet publish -r linux-x64 --self-contained`.
- Коммиты на русском, без строк атрибуции ИИ. Комментарии по-русски, только где причина неочевидна.

---

### Task 1: Кодек кадра и структуры сервера (`ServiceFrame`)

**Files:**
- Create: `avalonia/AstraUsb/Services/ServiceFrame.cs`
- Create: `avalonia/AstraUsb.Tests/ServiceFrameTests.cs`

**Interfaces (Produces):**
- `static byte[] ServiceFrame.Build(ushort cmd, ReadOnlySpan<byte> payload)` — кадр по правилу выше (payload уже с префиксом).
- `static byte[] ServiceFrame.Request(ushort cmd, ReadOnlySpan<byte> data)` — `Build(cmd, "000000"+data)`.
- `static byte[] ServiceFrame.ParseReply(ushort cmd, ReadOnlySpan<byte> raw)` — проверяет `7E`, длину, `cmd`, XOR, `0D 0A`; бросает `InvalidDataException` на несоответствии; возвращает данные ответа БЕЗ статуса; бросает, если статус != 0.
- `record CmsServer(byte Type, bool Enabled, string Domain1, string Domain2, string Ip, ushort Port)`.
- `static CmsServer ServiceFrame.ReadServer(ReadOnlySpan<byte> replyData)` — разбор 62-байтных данных.
- `static byte[] ServiceFrame.WriteServer(CmsServer current, string ip, ushort port)` — 62 байта: тип=2, вкл=1, IP и порт заменены, домены и остальное из `current`; IP длиннее 31 байта — `ArgumentException`.

- [ ] Step 1: Падающие тесты. Круг Build→разбор для login и server на эталонных векторах (побайтно); `ReadServer` даёт тип=2/вкл=1/IP `192.168.0.9`/порт 6608; `WriteServer(current,"10.0.0.5",6608)` меняет только IP+порт+тип+вкл, домены сохранены, длина 62; битый XOR и статус!=0 бросают; IP 32+ символа бросает.
- [ ] Step 2: Прогнать — красный.
- [ ] Step 3: Реализация (сверять раскладку с DLL: read `0x1004b630`, save `0x1004ece0`; дамп в разборе владельца).
- [ ] Step 4: `dotnet test --filter ServiceFrame`.
- [ ] Step 5: Commit — `feat(service-usb): кодек кадра и структуры сервера регистратора`

---

### Task 2: Транспорт libusb (`ServiceUsb` / `IServiceUsb`)

**Files:**
- Create: `avalonia/AstraUsb/Services/ServiceUsb.cs`
- Create: `avalonia/AstraUsb.Tests/ServiceUsbTests.cs`

**Interfaces:**
- `interface IServiceUsb { int Count(); CmsServer? ReadServer(); void WriteServer(string ip, ushort port); }` — `Count()` число подключённых `4255:0001`; чтение/запись включают login.
- `class ServiceUsb : IServiceUsb` — P/Invoke `libusb-1.0.so.0` (`libusb_init`, `libusb_open_device_with_vid_pid`, `libusb_set_auto_detach_kernel_driver`, `libusb_claim_interface`, `libusb_control_transfer`, release/close/exit). Одна команда = OUT кадра (сверить число записанных байт), пауза 200 мс, IN до `0D 0A`. `WriteServer`: `Count()!=1` → бросить; login; read; если IP+порт уже целевые — выход без записи; иначе `ServiceFrame.WriteServer`, отправить `0x0C`, снова read и сверить (иначе бросить).

- [ ] Step 1: Тест на разборной части без железа: подменяемый делегат обмена (`Func<byte[],byte[]>` вместо libusb) — проверить, что `WriteServer` при совпадении не шлёт `0x0C`, при расхождении шлёт и делает read-after-write, при `Count()!=1` бросает до любой команды. (Вынести обмен в `internal` для подмены; libusb-обёртка — тонкая, без логики.)
- [ ] Step 2: Красный. Step 3: Реализация. Step 4: `dotnet test`.
- [ ] Step 5: Commit — `feat(service-usb): транспорт libusb и запись с проверкой чтением`

---

### Task 3: Авто-настройка при подключении + адрес в Settings

**Files:**
- Modify: `avalonia/AstraUsb/Services/Settings.cs` (поле `CmsServerHost`, `CmsServerPort=6608`), `avalonia/AstraUsb/Services/ActionLog.cs` (вид `ServiceUsb`), `avalonia/AstraUsb/ViewModels/MainWindowViewModel.cs` (или сервис мониторинга), `avalonia/AstraUsb/ViewModels/SettingsViewModel.cs` + `Views/MainWindow.axaml` (поле адреса на вкладке настроек)
- Create: `avalonia/AstraUsb.Tests/ServiceProvisionTests.cs`

**Поведение (перенос `VendorUsb.Configure` из viewer):** фоновый цикл (рядом с опросом носителей) при `IServiceUsb.Count()==1` и заданном `CmsServerHost`: прочитать; если адрес не целевой — записать и перечитать; результат и ошибки — в `ActionLog`, статус на сервисной вкладке. При `Count()>1` — сообщение «подключите один A11». Пусто `CmsServerHost` — ничего не делать. Логика решения (что писать, когда пропустить, обработка нескольких устройств, разовость попытки на устройство) — чистая функция, тестируется с фейковым `IServiceUsb`; авто-запись не автозапускается в тестах вьюмодели без явного фейка.

- [ ] Step 1: Тест чистой функции решения (совпало → skip; не совпало → write; несколько → отказ; пусто host → skip) — красный.
- [ ] Step 2: Красный. Step 3: Реализация + поле настроек. Step 4: `dotnet test`; `dotnet publish -r linux-x64 --self-contained`.
- [ ] Step 5: Commit — `feat(service-usb): авто-настройка CMS-сервера регистратора на станции`

---

### Task 4: Прогон на живом A11 и уборка станции

- [ ] Собрать `linux-x64`, скопировать на станцию 192.168.0.41, поставить `avalonia/install_native.sh` (глушит Python-сервис).
- [ ] Проверить: чтение совпадает с эталоном; запись тестового адреса и read-after-write; отказ при двух A11 (по возможности); перезагрузка регистратора и повторное чтение — адрес сохранился.
- [ ] Вернуть Python-версию (из `/opt/astra-usb-monitor.before-*` или `install_native.sh` Python-репозитория), убрать Avalonia-службу. Найденное в прогоне чинить отдельными коммитами.
- [ ] Отчёт: что прочитано/записано, сохранность после перезагрузки.
