# Перенос изменений Python v1.25 в Avalonia

Эталон: `git diff 382a3ac..2524f00 -- usb_monitor.py gui.py updater.py`. База предыдущего переноса: `382a3ac`; актуальный Python: `2524f008c32e125dc2f3f904535110a582a4ec6f`. Сверены все 190 блоков diff и изменения 14 Python-файлов тестов. Из них выделены 83 смысловых контракта.

Рабочая ветка: `codex/python-parity-2026-10-20`. Стек сохранён: .NET 8, Avalonia 11.2.3, существующие SQLite и xUnit. Исходники Python, версии пакетов и каналы релизов не менялись.

## Результат

- Изменённая запись с временным суффиксом находится при следующем подключении. Совпадение требует обычного файла, точного имени, размера и разницы mtime меньше секунды. `Destinations` сохраняет путь реально найденной версии.
- Прогресс учитывает новые и уже сохранённые файлы; итог сеанса считает только новые копии. Постоянный журнал показывает ход новых копий, объём и оставшееся время. Для пропущенных файлов записывается итог, без сообщения на каждый файл.
- Экспорт сохраняет mtime и atime, создаёт файл с обычными правами `0666 & umask` и не выполняет chmod. Резервное копирование сохраняет права исходного файла.
- Отказ обоих источников USB-опроса сохраняет камеры в интерфейсе. Ожидаемая ошибка mount повторяется при следующем опросе; неожиданная ошибка остаётся на той же плитке до подтверждённого отключения.
- Диск архива исключается из источников до идентификации и удерживается при опросе, остановке и закрытии. Сам BackupService повторно запрещает источник, содержащий текущий архив. Разрешение пути выполняется в фоне. Уборка монтирований снимает только собственную точку отсутствующего `/dev`. Живые устройства, чужие точки и неизвестный источник не допускают lazy umount.
- Основной период поиска применяется к текущей дате файла. Дата загрузки остаётся отдельным столбцом. Дополнительный период съёмки сохраняется отдельно. Экспорт берёт отмеченные результаты либо весь результат поиска, когда отметок нет. Команды удаления сохраняют прежние ограничения выбора.
- Над плитками показаны свободное место, ёмкость и процент заполнения настоящего тома архива. Предупреждение включается строго при свободном месте меньше 10%. При отсутствии маркера измерение системного корня не подменяет архив.
- Единый журнал `astra-usb-monitor.log` размещается в архиве; при его недоступности используется `data/app.log`. Строки имеют дату и не перемешиваются. Запись и чтение архивного журнала удерживают защищённый каталог, проверяют маркер и идентичность назначения. Ошибка fallback имеет причину, подавление повторов сбрасывается после восстановления.
- Миграции проверяют колонки до ALTER и выполняются целиком в транзакции IMMEDIATE, включая одновременный старт нескольких выгрузок. Повреждение базы и запрет записи не считаются существующей колонкой. Ошибки настроек, исходных файлов, монтирования и фоновых операций сохраняют причину; флаги фоновой занятости освобождаются.
- Настройки с отрицательным таймаутом/сроком хранения, null обязательной строки или повреждённым UTF-8 дают `Unreadable`. Сохранение не затирает повреждённый документ без явного выбора восстановления.
- Обновление сохраняет раздельные семейства v1/v2, точный RID, проверку суммы, повторную проверку занятости и откат при любой ошибке установки.

## Проверки

Для изменяемого поведения сначала наблюдался ожидаемый RED, затем GREEN. Регрессии включают замену родителя junction/symlink, FIFO вместо файла, смену UUID, пропавший маркер, ошибку SQLite, отказ записи, повторную выгрузку, отказ mount и неожиданный сбой фонового задания. Отдельный тест проверяет порядок блокировок Settings и StationLog, чтобы повреждённая конфигурация не вызывала взаимную блокировку.

Полный итоговый прогон Windows Release: **692 PASS, 1 Linux-specific SKIP, 0 FAIL**, всего 693 проверки, exit 0. Команда: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj -c Release --no-restore --nologo --logger "console;verbosity=minimal"`. Пропущенный `A_nested_linux_archive_reports_its_own_volume_instead_of_the_system_disk` отдельно проверен в Linux на настоящем tmpfs. Release и XAML собираются успешно.

[Независимое ревью](2026-10-20-python-avalonia-review.md) не оставило незакрытых замечаний. Его находки закрыты отдельными RED/GREEN: порядок блокировок журнала и настроек, процессный fallback журнала, права экспорта, архив в наборе источников, фоновые ошибки и ошибки автоочистки. Дополнительно воспроизведены гонки миграции старой БД при 12 одновременных конструкторах; IMMEDIATE-транзакция охватывает всю схему, поздняя ошибка откатывает все изменения, затем повторный init проходит.

Linux-проверки выполнены в WSL `supermux`, в отдельных chroot с mount/pid/net namespaces. Физические диски в `/dev` не передавались. Использован временный .NET SDK 8.0.424/runtime 8.0.30, SHA512 проверен по официальному описанию выпуска. Тестовые подстановки разрешают временный архив в изоляции; штатный запрет архива на системном диске в продукте сохранён.

| Проверка | Результат |
| --- | --- |
| Native ArchiveLeaf: права backup, umask экспорта, времена, ссылки/FIFO и cleanup | 15/15 PASS |
| Native StationLog: identity, marker, shadow, ссылки/FIFO, несколько процессов, ротация | 10/10 PASS |
| StorageState на настоящем вложенном tmpfs 8 MiB | PASS; ёмкость 8 388 608 байт, при снятом маркере архив недоступен |
| Updater: busy, offline-busy, offline-lock, mismatch, restarts, success, handoff | Все 7 PASS на окончательном Updater |
| installer-guard и package-guard | Оба PASS |
| shell syntax | Все 14 shell-скриптов PASS, включая новый station-log harness |

В сценариях обновления используются тестовые systemctl и локальный HTTP-сервер. Настоящий минутный Settle сохранён. package-guard обрабатывает отсутствие сокета systemd внутри изоляции и завершается успешно. Это проверка установки и отката в изоляции, а не работа действующей службы станции.

## Сохранённые различия платформ

C# использует Dispatcher и прямую модель FoundFile вместо Tk Treeview/Queue; специальных Tcl/PIL исключений в этом стеке нет. Файловые и процессные ошибки проверяются средствами .NET. Стандартные fallback превью сохраняются. Уборка мёртвых монтирований запускается при старте в фоне; интерфейс не ждёт её перед первым опросом.

Python читает стабильный v1 latest, C# выбирает готовый v2 для своей платформы. Семейства продукта, имена архивов и контрольные суммы сохраняют независимость обновлений.

Существующая защита C# оставляет оригиналы при частичной ошибке копирования, подмене карты и отказе журнала сведений о копиях. Перенос не расширяет автоудаление и не ослабляет проверку открытых дескрипторов, блокировки или отмену.

## Проверка с настоящими устройствами и оставшиеся границы

На машине пользователя проверен настоящий регистратор `D:` (FAT32, ID `1`). Использованы реальные UsbWatcher, DeviceIdentifier, DeviceRegistry, BackupService, ArchiveSearch и FileExporter; база и архив размещены в отдельном тестовом каталоге рабочего дерева.

| Действие | Фактический результат |
| --- | --- |
| Чтение регистратора | ID 1; 8 файлов DCIM; всего 321 файл |
| Первая полная выгрузка | 321 файл, 464 МБ, Done |
| Повторная выгрузка | 0 файлов, 0 Б; список файлов архива не изменился |
| Проверка оригиналов и копий | SHA256 всех 8 записей DCIM совпали, оригиналы сохранены |
| Поиск по дате файла | Архивная фотография найдена по mtime вне даты загрузки |
| Экспорт на настоящий FAT32 | 1 фотография, 797 320 байт; SHA256 совпал, mtime в пределах точности FAT32 |
| Физическое переподключение | ID и UUID прежние; сохранены 2 новых/изменённых служебных файла (156 КБ), следующий проход 0 файлов/0 Б; SHA256 записей DCIM и экспорт повторно PASS |
| Уборка тестового экспорта | Удалена только созданная проверкой папка AstraParityTest; исходные записи сохранены |

Отдельный физический диск архива не подключён: тестовый архив находится на NTFS машины. Проверка Windows/FAT32 не подтверждает работу Linux-монтирования, exFAT и действующего systemd. На целевой Linux-станции остаются сценарии с отдельным диском архива:

1. Первой загрузки, повторного подключения без новых копий и изменения одного файла с единственной дополнительной версией.
2. Краткого пропадания устройства и полного отключения во время чтения, возврата на другой порт с тем же UUID.
3. Отключения и возврата диска архива: отсутствие записи в shadow-каталог, правильные место и журнал после возврата.
4. Экспорта на FAT/exFAT и обычную Linux-файловую систему с чтением результата от имени оператора.
5. Обновления при активной выгрузке и запуска действительной службы systemd.

Работающая станция не переустанавливалась, носители не форматировались, режим удаления исходных видео при аппаратной проверке был выключен.

## Все смысловые контракты

`S/` означает `avalonia/AstraUsb/Services/`, `VM/` означает `avalonia/AstraUsb/ViewModels/`, `V/` означает `avalonia/AstraUsb/Views/`. Строки Python относятся к эталонному коммиту. Imports, объекты Tk и stdout имеют соответствующий механизм C#, перечисленный в строке.

| Код | Python, функция и строки | Место C# | Контракт переноса |
| --- | --- | --- | --- |
| M01 | imports:8,14 | `S/FileCopier.cs`, `S/CrashLog.cs` | `re` нужен для точной маски версии, traceback для последнего рубежа |
| M02 | `_worker_guard`:126 | `VM/MainWindowViewModel.StartBackup` | Успешная блокировка сбрасывает подавление сообщения о занятости |
| M03 | `_find_card_by_uuid`:175 | `VM/MainWindowViewModel.Identify/StillConnected`, `S/UsbWatcher.List` | Отказ опроса при ожидании возврата означает продолжить ожидание |
| M04 | `read_version`:234 | `S/VersionInfo.Parse` | Отсутствующий/битый VERSION допускает fallback, программная ошибка не скрывается |
| M05 | `touch_copying_marker`:251 | `S/BusyMarker.Touch` | Причина отказа записи busy-маркера видна один раз; после восстановления повтор снова допустим |
| M06 | `_log_once`:269-284 | Новый общий журнал и вызывающие сервисы | Одинаковая ошибка по ключу подавляется, изменение пишется, восстановление очищает ключ |
| M07 | `_LogTee.__init__/write`:289-326 | `S/CrashLog.Write`, `S/ActionLog.Write`, новый журнал | Целые строки от каждого потока, единая синхронизация stdout/stderr, дата у каждой непустой строки, отступ убирается только в файле |
| M08 | `_LogTee._write_lines/flush/isatty`:328-353 | Новый журнал | Отказ записи сообщается один раз в исходный поток, закрытый прежний файл после переноса допустим; неожиданные ошибки не маскируются |
| M09 | `setup_file_logging`:358-385 | `S/CrashLog`, `S/ActionLog`, `S/Updater.Say` | Повторный тот же путь идемпотентен; новый путь переключает запись; ошибка открытия видна; старый файл закрывается; ротация сохраняется |
| M10 | `update_log_location`:389-403 | `Program.Main`, `VM/SettingsViewModel.SaveStorage`, `VM/MainWindowViewModel.Refresh` | На старте включить журнал; доступный архив получает файл в корне, иначе data/app.log; смена папки и возврат тома меняют путь |
| M11 | `restore_stdout`:406-416 | Изоляция тестов нового журнала | Тесты возвращают исходные потоки и сбрасывают путь |
| M12 | `collect_diagnostics`:434 | `S/Diagnostics.Export` | Ошибка SQLite допускает пакет без базы; причина не должна скрывать ошибку кода |
| M13 | `export_logs`:471 | `S/Diagnostics.Export` | В диагностике текущий фактический файл журнала, включая архивное размещение |
| M14 | `_load_config`:614-632 | `S/Settings.Load` | Неверный тип/значение оставляет режим Unreadable и объясняет путь и конкретное поле; повторы подавлены |
| M15 | `_iter_mounts`:686-689 | `S/MountManager.Current`, `S/ArchiveGuard.Mounts` | Отказ чтения таблицы монтирования имеет причину и подавление повторов; восстановление очищает ошибку |
| M16 | `_find_mount_for_path`:698,705; `_get_filesystem_uuid`:723 | `S/ArchiveGuard.ResolveDestination/Uuid`, `S/MountManager.FindExisting` | Узкие ожидаемые файловые/процессные ошибки при поиске пути и UUID |
| M17 | `_archive_path_allowed`:912-917; `_dest_identity_matches`:926; `ensure_dest_marker`:965 | `S/ArchiveGuard.Open/Available/Mark` | Отказ архива/проверки физического диска/создания маркера пишет путь и причину один раз |
| M18 | `_copy_archive_file`:931,942,946 | `S/ArchiveDirectory.CopyFile/CopyStream/WriteFile` | Backup сохраняет mode по умолчанию; экспорт без mode; mtime/atime сохраняются; неполный файл убран при любом исключении |
| M19 | `_delete_source_videos`:1007,1009 | `S/SourceCleaner.DeleteBackedUpVideos/EnumerateFiles` | Неудалённый исходный файл и причина записаны; успешное число удалений записано по-русски |
| M20 | `_format_size`:1047,1051 | `S/BackupService.Size`, `VM/SearchViewModel.Size`, `VM/MainWindowViewModel.Size` | Русские единицы Б/КБ/МБ/ГБ/ТБ/ПБ в доступных пользователю строках |
| M21 | `_log_progress`:1094-1097 | `S/BackupService` callback и новый журнал | Русские «файлов», «осталось»; у строки один timestamp от журнала |
| M22 | `_init_db`:1144-1147 | `S/DeviceRegistry.InitSchema`, `S/CollectionLog.EnsureColumn` | Сначала PRAGMA, ALTER только отсутствующих колонок, реальная ошибка миграции выходит наружу |
| M23 | `_init_db`:1162-1163 | `S/DeviceRegistry.InitSchema` восстановление из backups | Отказ восстановления потерянной строки устройства имеет причину |
| M24 | `_id_from_latest_log`:1210 | `S/DeviceIdentifier.ReadLogId/ParseId` | Первые 7 символов токена, ASCII цифры, положительное число; сверка LOG/DCIM сохраняется |
| M25 | `_resolve_device_id`:1335 | `VM/MainWindowViewModel._astraOwners`, `ReadCard`, `Apply` | Любая ошибка освобождает резерв ID и пробрасывается |
| M26 | `_friendly_device_label`:1368-1369 | `S/DeviceRegistry.FriendlyLabel`:176 | Подпись `ID N`, имя через ` · ` |
| M27 | `_get_device_serial_linux`:1419,1442,1449; Windows:1462; `_get_drive_label_linux`:1495; Windows:1521 | `S/UsbWatcher.ListWindows/CollectFromDisk/ListSysfs/RunProcess` | Служебные serial/label имеют ожидаемый fallback, программные ошибки не подавляются широким catch |
| M28 | `_scan_drive`:1469-1480 | `S/BackupService.Measure` | Нечитаемые файлы/каталоги учитываются в диагностике с причиной |
| M29 | `_get_linux_partitions`:1529-1532; `_get_lsblk_partitions`:1572-1577; `_get_sys_block_partitions`:1605,1620-1623 | `S/UsbWatcher.ListLinux/ListSysfs/RunProcess` | Неудача обоих источников отличается от успешного пустого опроса; пустой lsblk пробует sysfs; ошибки по каждому источнику подавляются до изменения/восстановления |
| M30 | `_find_existing_mount`:1650,1656 | `S/MountManager.FindExisting/Current` | Ожидаемые ошибки realpath/таблицы допускают fallback; ошибка кода видна |
| M31 | `_mount_device`:1706,1708 | `S/MountManager.MountOurselves/Run`, `S/UsbWatcher.RunProcess` | Причина неудачного mount по-русски; неожиданные ошибки достигают последнего рубежа |
| M32 | `_mount_source`, `_unmount`:1712-1754 | `S/MountManager.Release` | Уже снятая точка успех; обычный umount сначала; lazy только когда source известен как /dev и физически отсутствует; живое/неизвестное не отцеплять; ошибка снятия и rmdir видна |
| M33 | `_cleanup_dead_mounts`:1757-1764 | `S/MountManager`, `VM/MainWindowViewModel` запуск/ForgetCard | Чистить только собственные точки под MOUNT_BASE с отсутствующим /dev, при старте и подтверждённом удалении |
| M34 | `_same_timestamped_copy`:1767-1785; `_copy_files_open`:1843-1849 | `S/FileCopier.Copy`, `S/ArchiveDirectory.SameFile` | Ровно `stem_8цифр_6цифр.ext`, обычный файл, тот же размер и abs(mtime)<1с; не плодить копии; реальный путь версии сохранить в Destinations |
| M35 | `_copy_files_open`:1796-1799,1807-1820,1846-1849,1856-1857,1860-1861 | `S/FileCopier.Copy`, `S/BackupService` callback | Обработано = новое + уже сохранённое; callback доходит до общего числа байтов; CopyResult считает только новые; пропущенные не засоряют log |
| M36 | `_copy_files_open`:1862,1865,1880 | `S/FileCopier.Copy/EnumerateDirectories` | Ожидаемые файловые ошибки дают Failed и источник/назначение/причину; неожиданное не теряется |
| M37 | `_copy_task._label/_emit`:1942,1949 | `S/DeviceRegistry.ReadNames`, `VM/MainWindowViewModel.StartBackup` progress | Ошибка только чтения имени допускает номер; отправка события не проглатывает неизвестную ошибку |
| M38 | `_copy_task`:1975-2087,2121,2127,2134 | `S/BackupService`, `VM/MainWindowViewModel.BeginRecovery`, новый журнал | Ошибка/сканирование/пусто/объём/возврат/отключение/остановка/итог по-русски без встроенных старых timestamps |
| M39 | `_copy_task`:2098,2142 | `S/BackupService.RunAsync/RecordCollected` | Внутренние catch узкие; ожидаемая ошибка копии и SQLite содержит причину, последняя граница ловит остальное |
| M40 | `_copy_task_linux`:2194-2199 | `S/MountManager.Ensure`, `VM/MainWindowViewModel.MountPointFor` | Неудачный mount виден как ошибка и повторяется без переподключения |
| M41 | `_copy_task_linux`:2214-2218 | `S/ArchiveGuard.ResolveDestination`, `VM/MainWindowViewModel.Refresh` | Возврат диска архива не запускает backup, сохраняет mount, переносит журнал и сообщает событие |
| M42 | `_make_submit_fn`:2239-2241 | `VM/MainWindowViewModel.StartBackup` catch StationBusy | Повторять ожидаемую занятость; один log до освобождения; повтор не считается новым подключением |
| M43 | `_make_submit_fn`:2245-2252 | `S/BackupService.RunAsync`, `VM/MainWindowViewModel.StartBackup/Identify/MountPointFor/TrackMediaTask` | Последняя граница пишет stack trace, показывает ошибку существующего dev, неизвестная ошибка не повторяется каждые 2с |
| M44 | `monitor_usb`:2270 | `Program.Main` и Console | Только отсутствие reconfigure допустимо |
| M45 | `monitor_usb`:2284-2285,2291-2292,2300-2305,2319 | `VM/MainWindowViewModel` constructor/Refresh, `S/BackupService` constructor | Русские стартовые сведения и отсутствие архива; уборка мёртвых точек при запуске в фоне; начальная ошибка опроса не валит процесс |
| M46 | `monitor_usb._forget`:2339-2344 | `VM/MainWindowViewModel.ForgetCard/ReleaseGoneMedia` | Подтверждённое отключение пишется и запускает уборку; событие удаления не глотается |
| M47 | `monitor_usb`:2354,2357-2361 | `VM/MainWindowViewModel.Refresh/TrackMediaTask` | Worker уже сообщил fault, завершение не глотает его повторно; ошибка опроса не изменяет known/current |
| M48 | `monitor_usb`:2378,2385 | `VM/MainWindowViewModel.HoldBriefly/BeginRecovery` | Ошибки уведомления сбоя/возврата шины не подавлены |
| M49 | `monitor_usb`:2425-2427 | `VM/MainWindowViewModel.MountPointFor/HoldBriefly` | Отметку повтора снимает только подтверждённое отключение; один пропуск в опросе не теряет retry |
| M50 | `monitor_usb`:2438,2449-2450,2454 | `VM/MainWindowViewModel.SplitOfflineMedia/StartBackup`, Program | Русский log нового устройства/отложения/остановки; retry не log нового устройства |
| G01 | imports:11,16-17 | `S/CrashLog`, новый журнал, `S/ArchiveGuard` | Доступ к traceback, подавлению ошибок, доступности архива и смене журнала |
| G02 | `_archive_disk_usage`:101-108; `_build_workers_tab/_refresh_disk_space/_show_disk_space/_draw_disk_bar/_build_ports`:622-673 | `S/StorageState.Read`, `VM/MainWindowViewModel.UpdateStorage`, `V/MainWindow.axaml`:1193 | Измерить доступный архив; показать свободно, всего, %занятости и отсутствие диска; предупреждение `<10%`; обновление без блокировки UI |
| G03 | `_start_update_service`:145 | `S/Updater.StartService/Shell` | Ожидаемые ошибки запуска сообщаются, программные не маскируются |
| G04 | logo:268; overlay:374; date fields:564,572 | `App.axaml`, `VM/SearchViewModel.TryDate/Search`, `V/MainWindow` | Точечный Tk fallback и корректный ввод даты |
| G05 | `_build_search_tab`:539 | `VM/SearchViewModel.Refresh/FoundFile.Selected`, `V/MainWindow.axaml` CheckBox | Счётчик экспорта обновляется на изменение выбора |
| G06 | `_offline_roots`:901-904 | `VM/MainWindowViewModel.Refresh/WatchOfflineMedia` | Отказ опроса офлайн-носителей записать один раз, не трактовать как пустую шину, сбросить подавление при успехе |
| G07 | about:832; offline dialogs/status/mount:869,882,919,942,979 | `VM/SettingsViewModel.CheckUpdates`, `VM/MainWindowViewModel.WatchOfflineMedia/MountPointFor` | Ожидаемое закрытие UI не маскирует код; ожидаемый mount failure допускает следующий опрос |
| G08 | `_auto_cleanup_worker`:1077,1080 | `S/BackupService.PurgeExpired`, `S/StorageManager.DeleteExpired` | Отложенную из-за занятости и неудачную автоочистку видно в log с причиной |
| G09 | export diagnostics:1112 | `VM/LogViewModel.ExportDiagnostics`, `S/Diagnostics.Export` | Файловая ошибка показывается, неизвестная ошибка не теряется |
| G10 | factory reset:1140,1143-1144; restart:1165-1166 | `VM/SettingsViewModel.RunFactoryReset`, `S/AppRestart.Now` | Сброс пишет успех со статистикой/отказ с причиной; перезапуск пишет причину отказа |
| G11 | `_select_dest`:1192 | `VM/SettingsViewModel.SaveStorage` | Успешный выбор назначения сразу переносит активный журнал |
| G12 | exit:1304 | `VM/MainWindowViewModel` выход/`S/ActionLog` | Выход оператором виден; timestamp ставит журнал, не сообщение |
| G13 | `_search_worker`:1382-1390,1441-1447 | `S/ArchiveSearch.Find`, `S/ArchiveQuery`, `VM/SearchViewModel.Search` | Основной период сравнивается с mtime файла, независимо от started_at/CollectedAt |
| G14 | `_search_worker`:1463-1464 | `VM/SearchViewModel.Search`, `S/ArchiveSearch.Cameras/People/Resolve` | Ожидаемая ошибка поиска с причиной; незапланированная ошибка не проглатывается внутри |
| G15 | `_apply_search_results/_export_targets/_update_export_btn`:1477-1495 | `VM/SearchViewModel.ExportLabel/Chosen/Refresh`, `FoundFile` | Стабильная привязка строки к файлу; выделенные или все результаты; кнопка содержит реальное N, выключена при 0 |
| G16 | `_open_externally`:1505,1532; `_show_image`:1579,1584 | `VM/SearchViewModel.Play/ShowPhoto`, `S/VideoPreview/MediaTools` | Узкие ожидаемые ошибки файла/процесса/изображения; не терять причину кода |
| G17 | `_export_found_files`:1599-1600,1613,1622 | `VM/SearchViewModel.Export` | Подтвердить/экспортировать один снимок выбранных целей; отсутствие выборки означает весь текущий результат |
| G18 | `_export_worker/_export_done`:1634-1647 | `S/FileExporter.Export`, `S/ArchiveDirectory.CopyFile`, `VM/SearchViewModel.Export` | Без chmod, оба времени; ошибки с именами файлов и причинами пишутся, счётчик кнопки обновлён после |
| G19 | rename:1698,1706-1707; assign:1723,1727-1728 | `VM/DevicesViewModel.Rename/Assign`, `S/DeviceRegistry.Rename`, `S/StaffDirectory.AssignDevice` | Успех содержит ID и новое имя/сотрудника, отказ содержит ID и причину |
| G20 | `_delete_device_videos`:1776 | `VM/SearchViewModel.Delete`, `S/ArchiveSearch.Delete`, `S/StorageManager` | Ошибка удаления видео с ID/причиной должна быть видна |
| G21 | `_start_monitor`:1789,1791 | `VM/MainWindowViewModel.Refresh/TrackMediaTask`, `S/CrashLog.Install` | Любое падение monitor thread оставляет trace и статус UI |
| G22 | `_poll_queue`:1852-1859 | `VM/MainWindowViewModel.StartBackup` progress и обработчики fault | Поздняя ошибка после определения ID красит ту же плитку, удаляет busy, не создаёт вторую |
| G23 | `launch`:1991-1992 | `Program.Main`, `App.OnFrameworkInitializationCompleted` | Прямой старт GUI включает постоянный журнал до создания окна |
| P01 | import http.client:11 | `S/Updater.Ask/Download` | Отличать сетевые HTTP ошибки от программных |
| P02 | `_log`:105 | `S/Updater.Say` | Русский префикс «Обновление», единый журнал |
| P03 | `_service_healthy`:118-119 | `S/Updater.Healthy/Output` | Отказ запуска/systemctl и неверное число перезапусков объясняют провал проверки |
| P04 | failed-tag read:152; write:160-162; clear:168 | `S/Updater.AlreadyFailed/RememberFailed`, снятие failed-tag | Ожидаемая ошибка чтения допустима; ошибка записи failed-tag содержит тег/причину; удаление молчит только при ожидаемой файловой ошибке |
| P05 | `find_offline_archives`:192 | `S/Updater.FindOfflineArchives` | Ожидаемый отказ чтения каталога даёт пустой список, неизвестная ошибка не скрывается |
| P06 | `_verify_checksum_file`:210,216 | `S/Updater.VerifyChecksumFile` | Отсутствующая/битая сумма и нечитаемый архив имеют ожидаемую ошибку; программная не превращается в штатный пропуск |
| P07 | `stage_offline_package`:252 | `S/Updater.StageOffline` | Файловая ошибка подготовки удаляет неполный спул и показывает причину; неожиданная не маскируется |
| P08 | `_take_offline_spool`:261-274 | `S/Updater.TakeOfflineSpool` | Нет каталога спула: тихо; каталог есть без читаемого тега/с неверным тегом/без архива: log причины и пропуск |
| P09 | `_apply_locked`:358 | `S/Updater.InstallArchive` catch с RememberFailed/Rollback | Любое исключение установки, включая неожиданное, заканчивается запоминанием тега и откатом |
| P10 | `main` release fetch:414; download:443 | `S/Updater.Ask/Download`, `S/Release.Select` | Ожидаемые сетевые/форматные ошибки пишут причину и допускают следующую проверку; ошибки кода проходят наружу |

## Привязка всех 190 блоков diff

Координаты получены для `git diff --unified=0` указанного диапазона. Каждый блок привязан к одному или нескольким контрактам выше.

| Блок | Файл | Координаты diff | Контракты |
| --- | --- | --- | --- |
| 001 | `gui.py` | `-10,0 +11` | G01 |
| 002 | `gui.py` | `-15,2 +16,2` | G01 |
| 003 | `gui.py` | `-99,0 +101,8` | G02 |
| 004 | `gui.py` | `-136 +145` | G03 |
| 005 | `gui.py` | `-259 +268` | G04 |
| 006 | `gui.py` | `-365 +374` | G04 |
| 007 | `gui.py` | `-529,0 +539` | G05 |
| 008 | `gui.py` | `-554 +564` | G04 |
| 009 | `gui.py` | `-562 +572` | G04 |
| 010 | `gui.py` | `-611,0 +622` | G02 |
| 011 | `gui.py` | `-612,0 +624,28` | G02 |
| 012 | `gui.py` | `-618,0 +658,16` | G02 |
| 013 | `gui.py` | `-777 +832` | G07 |
| 014 | `gui.py` | `-814 +869` | G07 |
| 015 | `gui.py` | `-827 +882` | G07 |
| 016 | `gui.py` | `-846 +901,2` | G06 |
| 017 | `gui.py` | `-847,0 +904` | G06 |
| 018 | `gui.py` | `-862 +919` | G07 |
| 019 | `gui.py` | `-885 +942` | G07 |
| 020 | `gui.py` | `-922 +979` | G07 |
| 021 | `gui.py` | `-1019,0 +1077` | G08 |
| 022 | `gui.py` | `-1021,0 +1080` | G08 |
| 023 | `gui.py` | `-1053 +1112` | G09 |
| 024 | `gui.py` | `-1080,0 +1140` | G10 |
| 025 | `gui.py` | `-1082,0 +1143,2` | G10 |
| 026 | `gui.py` | `-1103 +1165,2` | G10 |
| 027 | `gui.py` | `-1128,0 +1192` | G11 |
| 028 | `gui.py` | `-1240 +1304` | G12 |
| 029 | `gui.py` | `-1317,0 +1382,2` | G13 |
| 030 | `gui.py` | `-1325,6 +1390,0` | G13 |
| 031 | `gui.py` | `-1381 +1441,7` | G13 |
| 032 | `gui.py` | `-1397,2 +1463,2` | G14 |
| 033 | `gui.py` | `-1411,2 +1477,2` | G15 |
| 034 | `gui.py` | `-1418,5 +1484,12` | G15 |
| 035 | `gui.py` | `-1432 +1505` | G16 |
| 036 | `gui.py` | `-1459 +1532` | G16 |
| 037 | `gui.py` | `-1506 +1579` | G16 |
| 038 | `gui.py` | `-1511 +1584` | G16 |
| 039 | `gui.py` | `-1526 +1599,2` | G17 |
| 040 | `gui.py` | `-1539 +1613` | G17 |
| 041 | `gui.py` | `-1548 +1622` | G17 |
| 042 | `gui.py` | `-1560 +1634` | G18 |
| 043 | `gui.py` | `-1562,2 +1636,5` | G18 |
| 044 | `gui.py` | `-1570,3 +1647` | G18 |
| 045 | `gui.py` | `-1622,0 +1698` | G19 |
| 046 | `gui.py` | `-1630 +1706,2` | G19 |
| 047 | `gui.py` | `-1645,0 +1723` | G19 |
| 048 | `gui.py` | `-1649 +1727,2` | G19 |
| 049 | `gui.py` | `-1696,0 +1776` | G20 |
| 050 | `gui.py` | `-1708,0 +1789` | G21 |
| 051 | `gui.py` | `-1709,0 +1791` | G21 |
| 052 | `gui.py` | `-1769,0 +1852,8` | G22 |
| 053 | `gui.py` | `-1900,0 +1991,2` | G23 |
| 054 | `updater.py` | `-10,0 +11` | P01 |
| 055 | `updater.py` | `-104 +105` | P02 |
| 056 | `updater.py` | `-117 +118,2` | P03 |
| 057 | `updater.py` | `-150 +152` | P04 |
| 058 | `updater.py` | `-158,2 +160,3` | P04 |
| 059 | `updater.py` | `-165 +168` | P04 |
| 060 | `updater.py` | `-189 +192` | P05 |
| 061 | `updater.py` | `-207 +210` | P06 |
| 062 | `updater.py` | `-213 +216` | P06 |
| 063 | `updater.py` | `-249 +252` | P07 |
| 064 | `updater.py` | `-257,0 +261,2` | P08 |
| 065 | `updater.py` | `-261 +266,2` | P08 |
| 066 | `updater.py` | `-263,0 +270` | P08 |
| 067 | `updater.py` | `-266,0 +274` | P08 |
| 068 | `updater.py` | `-349,0 +358` | P09 |
| 069 | `updater.py` | `-405 +414` | P10 |
| 070 | `updater.py` | `-434 +443` | P10 |
| 071 | `usb_monitor.py` | `-7,0 +8` | M01 |
| 072 | `usb_monitor.py` | `-12,0 +14` | M01 |
| 073 | `usb_monitor.py` | `-123,0 +126` | M02 |
| 074 | `usb_monitor.py` | `-172 +175,5` | M03 |
| 075 | `usb_monitor.py` | `-227 +234` | M04 |
| 076 | `usb_monitor.py` | `-244,2 +251,5` | M05 |
| 077 | `usb_monitor.py` | `-258,0 +269,16` | M06 |
| 078 | `usb_monitor.py` | `-262,0 +289,5` | M07 |
| 079 | `usb_monitor.py` | `-270,0 +302,2` | M07 |
| 080 | `usb_monitor.py` | `-273 +306,19` | M07 |
| 081 | `usb_monitor.py` | `-275 +326` | M07 |
| 082 | `usb_monitor.py` | `-277,2 +328,10` | M08 |
| 083 | `usb_monitor.py` | `-288 +347` | M08 |
| 084 | `usb_monitor.py` | `-294 +353` | M08 |
| 085 | `usb_monitor.py` | `-299,4 +358,2` | M09 |
| 086 | `usb_monitor.py` | `-303,0 +361,2` | M09 |
| 087 | `usb_monitor.py` | `-312,2 +371,3` | M09 |
| 088 | `usb_monitor.py` | `-315,3 +375,11` | M09 |
| 089 | `usb_monitor.py` | `-320,0 +389,15` | M10 |
| 090 | `usb_monitor.py` | `-323 +406` | M11 |
| 091 | `usb_monitor.py` | `-332,0 +416` | M11 |
| 092 | `usb_monitor.py` | `-350 +434` | M12 |
| 093 | `usb_monitor.py` | `-387 +471` | M13 |
| 094 | `usb_monitor.py` | `-530 +614` | M14 |
| 095 | `usb_monitor.py` | `-532 +616` | M14 |
| 096 | `usb_monitor.py` | `-535 +619` | M14 |
| 097 | `usb_monitor.py` | `-537 +621` | M14 |
| 098 | `usb_monitor.py` | `-540,2 +624` | M14 |
| 099 | `usb_monitor.py` | `-544 +627,3` | M14 |
| 100 | `usb_monitor.py` | `-545,0 +631,2` | M14 |
| 101 | `usb_monitor.py` | `-599 +686,2` | M15 |
| 102 | `usb_monitor.py` | `-600,0 +689` | M15 |
| 103 | `usb_monitor.py` | `-609 +698` | M16 |
| 104 | `usb_monitor.py` | `-616 +705` | M16 |
| 105 | `usb_monitor.py` | `-634 +723` | M16 |
| 106 | `usb_monitor.py` | `-823,2 +912,3` | M17 |
| 107 | `usb_monitor.py` | `-825,0 +916,2` | M17 |
| 108 | `usb_monitor.py` | `-834 +926,2` | M17 |
| 109 | `usb_monitor.py` | `-838 +931` | M18 |
| 110 | `usb_monitor.py` | `-849 +942` | M18 |
| 111 | `usb_monitor.py` | `-852,0 +946` | M18 |
| 112 | `usb_monitor.py` | `-871 +965,2` | M17 |
| 113 | `usb_monitor.py` | `-912 +1007` | M19 |
| 114 | `usb_monitor.py` | `-914 +1009` | M19 |
| 115 | `usb_monitor.py` | `-952 +1047` | M20 |
| 116 | `usb_monitor.py` | `-956 +1051` | M20 |
| 117 | `usb_monitor.py` | `-999,2 +1094,2` | M21 |
| 118 | `usb_monitor.py` | `-1002 +1097` | M21 |
| 119 | `usb_monitor.py` | `-1049,10 +1144,4` | M22 |
| 120 | `usb_monitor.py` | `-1073,2 +1162,2` | M23 |
| 121 | `usb_monitor.py` | `-1121 +1210` | M24 |
| 122 | `usb_monitor.py` | `-1245,0 +1335` | M25 |
| 123 | `usb_monitor.py` | `-1278,2 +1368,2` | M26 |
| 124 | `usb_monitor.py` | `-1329 +1419` | M27 |
| 125 | `usb_monitor.py` | `-1352 +1442` | M27 |
| 126 | `usb_monitor.py` | `-1359 +1449` | M27 |
| 127 | `usb_monitor.py` | `-1372 +1462` | M27 |
| 128 | `usb_monitor.py` | `-1379 +1469,2` | M28 |
| 129 | `usb_monitor.py` | `-1386,2 +1477,4` | M28 |
| 130 | `usb_monitor.py` | `-1402 +1495` | M27 |
| 131 | `usb_monitor.py` | `-1428 +1521` | M27 |
| 132 | `usb_monitor.py` | `-1436 +1529,4` | M29 |
| 133 | `usb_monitor.py` | `-1476,3 +1572,6` | M29 |
| 134 | `usb_monitor.py` | `-1506 +1605` | M29 |
| 135 | `usb_monitor.py` | `-1521,2 +1620,4` | M29 |
| 136 | `usb_monitor.py` | `-1549 +1650` | M30 |
| 137 | `usb_monitor.py` | `-1555 +1656` | M30 |
| 138 | `usb_monitor.py` | `-1605 +1706` | M31 |
| 139 | `usb_monitor.py` | `-1607 +1708` | M31 |
| 140 | `usb_monitor.py` | `-1610,0 +1712,8` | M32 |
| 141 | `usb_monitor.py` | `-1612,7 +1721,24` | M32 |
| 142 | `usb_monitor.py` | `-1624 +1750` | M32 |
| 143 | `usb_monitor.py` | `-1625,0 +1752,2` | M32 |
| 144 | `usb_monitor.py` | `-1628,0 +1757,29` | M33, M34 |
| 145 | `usb_monitor.py` | `-1638,0 +1796,4` | M35 |
| 146 | `usb_monitor.py` | `-1645,0 +1807,14` | M35 |
| 147 | `usb_monitor.py` | `-1668 +1843,2` | M34 |
| 148 | `usb_monitor.py` | `-1669,0 +1846,3` | M35 |
| 149 | `usb_monitor.py` | `-1676,0 +1856,2` | M35 |
| 150 | `usb_monitor.py` | `-1679,10 +1860,2` | M35 |
| 151 | `usb_monitor.py` | `-1692 +1865` | M36 |
| 152 | `usb_monitor.py` | `-1707 +1880` | M36 |
| 153 | `usb_monitor.py` | `-1769 +1942` | M37 |
| 154 | `usb_monitor.py` | `-1776,4 +1949` | M37 |
| 155 | `usb_monitor.py` | `-1805 +1975` | M38 |
| 156 | `usb_monitor.py` | `-1815 +1985` | M38 |
| 157 | `usb_monitor.py` | `-1817 +1987` | M38 |
| 158 | `usb_monitor.py` | `-1833 +2003` | M38 |
| 159 | `usb_monitor.py` | `-1849 +2019` | M38 |
| 160 | `usb_monitor.py` | `-1854 +2024` | M38 |
| 161 | `usb_monitor.py` | `-1862 +2032` | M38 |
| 162 | `usb_monitor.py` | `-1887 +2057` | M38 |
| 163 | `usb_monitor.py` | `-1909 +2079` | M38 |
| 164 | `usb_monitor.py` | `-1917 +2087` | M38 |
| 165 | `usb_monitor.py` | `-1928 +2098` | M39 |
| 166 | `usb_monitor.py` | `-1951 +2121` | M38 |
| 167 | `usb_monitor.py` | `-1957 +2127` | M38 |
| 168 | `usb_monitor.py` | `-1964 +2134` | M38 |
| 169 | `usb_monitor.py` | `-1972 +2142` | M39 |
| 170 | `usb_monitor.py` | `-2024 +2194` | M40 |
| 171 | `usb_monitor.py` | `-2026,2 +2196,4` | M40 |
| 172 | `usb_monitor.py` | `-2042 +2214,2` | M41 |
| 173 | `usb_monitor.py` | `-2044,5 +2217,2` | M41 |
| 174 | `usb_monitor.py` | `-2068,0 +2239,3` | M42 |
| 175 | `usb_monitor.py` | `-2071,0 +2245,8` | M43 |
| 176 | `usb_monitor.py` | `-2089 +2270` | M44 |
| 177 | `usb_monitor.py` | `-2103,2 +2284,2` | M45 |
| 178 | `usb_monitor.py` | `-2110,2 +2291,2` | M45 |
| 179 | `usb_monitor.py` | `-2119 +2300,6` | M45 |
| 180 | `usb_monitor.py` | `-2133 +2319` | M45 |
| 181 | `usb_monitor.py` | `-2152,0 +2339,4` | M46 |
| 182 | `usb_monitor.py` | `-2154,4 +2344` | M46 |
| 183 | `usb_monitor.py` | `-2167,5 +2354` | M47 |
| 184 | `usb_monitor.py` | `-2174 +2357,5` | M47 |
| 185 | `usb_monitor.py` | `-2191,4 +2378` | M48 |
| 186 | `usb_monitor.py` | `-2201,4 +2385` | M48 |
| 187 | `usb_monitor.py` | `-2244 +2425,3` | M49 |
| 188 | `usb_monitor.py` | `-2255 +2438` | M50 |
| 189 | `usb_monitor.py` | `-2266 +2449,2` | M50 |
| 190 | `usb_monitor.py` | `-2270 +2454` | M50 |
