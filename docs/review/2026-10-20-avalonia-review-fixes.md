# Исправления ревью Avalonia от 20 октября 2026

Исходная версия: `52100a6`, master и релиз v2.0.8. Исправления закрывают
18 замечаний ревью. Для каждой ошибки сначала наблюдалось ожидаемое падение
регрессионной проверки. Ошибки подготовки стенда в RED не учитывались.

## P1

| № | Исправление | Регрессия и наблюдавшийся RED |
|---|---|---|
| 1 | Очистка принимает снимок подтверждённых источников, проверяет native identity, учитывает отмену. Windows удаляет по проверенному handle, Linux использует карантин с `RENAME_NOREPLACE`. | `ArchiveSafetyReviewFixTests`: подменённый `NEW` удалялся, отмена игнорировалась. `source-cleaner-race.py` подменяет файл непосредственно перед syscall: старое удаление теряло `NEW`. |
| 2 | Место резервируется только под недостающие файлы. Нулевая выгрузка не вытесняет записи ради свободного резерва. | `An_already_saved_source_does_not_evict_another_device`, оба варианта версий; `A_noop_backup_does_not_evict_other_devices_to_replenish_the_free_space_reserve`: исчезала чужая запись. |
| 3 | Имена файлов передаются через data-атрибуты, команды обрабатывает общий DOM listener. | `Archive_actions_treat_a_recording_name_as_data`: desktop/mobile исполняли внедрённый `alert`. Проверяется весь путь FileCopier, журнал, HTTP API, JavaScript. |
| 4 | Windows устанавливает программу в каталог текущего пользователя без повышения прав. | `windows-install-guard.ps1`: настоящий Inno Setup и запуск probe с production `AppPaths`; до исправления процесс возвращал 17 из-за `UnauthorizedAccessException`. После исправления также проверена сохранность данных при удалении программы. |
| 5 | Истечение доступа закрывает просмотрщик и инвалидирует отложенное открытие и кадры. | `Expired_access_closes_and_clears_the_viewer`, `Locked_access_disables_viewer_commands`, `A_late_open_result_cannot_restore_content_after_access_expires`: просмотр и команды оставались доступны. |
| 6 | Отключённые Python-юниты не мешают обновлению C#. Активные или включённые службы требуют явной миграции. | `test_disabled_python_unit_allows_noninteractive_native_update`: прежний prompt прерывал обновление. Дополнительно проверены enabled-служба и работающий Python-апдейтер. |
| 7 | `.deb` и `--units-only` отказывают при конфликте с Python до запуска C#. | `test_units_only_does_not_start_csharp_alongside_active_python`, `test_package_preinst_refuses_active_python_before_stopping_csharp`: прежний код запускал установку при активном Python. |
| 8 | Отсутствие файла считается удалением только на подтверждённом живом архиве. Отключение архива сохраняет очередь и попытки FTP. | `Detaching_the_archive_does_not_remove_pending_files`: очередь теряла задание. Linux fixture дополнительно проверяет отключение, перемонтирование и последующую настоящую FTP-отправку на loopback. |

## P2

| № | Исправление | Регрессия и наблюдавшийся RED |
|---|---|---|
| 9 | `/api/file` использует `ArchiveGuard`, resolver и защищённый поток. Поток и блокировка операции удерживаются до завершения HTTP-ответа. | `WebReviewFixTests`: внешний junction, неправильные DeviceN и отсутствие marker принимались. Проверены Range и блокировка недочитанного ответа. Linux RED отдавал `../Archive/Device7/secret.txt` при корне `archive`. |
| 10 | FTP сохраняет относительную структуру `DeviceN/...`, создаёт папки и кодирует сегменты URL. | `Equal_names_from_different_cameras_reach_distinct_remote_files`, `Ftp_paths_preserve_spaces_percent_hash_and_unicode`: два файла превращались в один, сложный путь отклонялся. Стенд принимает настоящий FTP control/data protocol. |
| 11 | Точный номер камеры имеет приоритет. Неоднозначный частичный поиск не выбирает первую строку. | `Exact_camera_numbers_win_and_ambiguous_names_do_not_pick_a_camera`: номера 7/700 выбирали камеру 1, неоднозначное имя тоже выбирало первую строку. |
| 12 | Поколение офлайн-проверки сверяется после staging и перед handoff. Следующая проверка ждёт очистки предыдущего пакета. | `Cancelling_an_offline_check_does_not_handoff_its_late_result`: отменённая проверка возвращала результат запуска службы. Дополнительно `Cancelling_after_staging_cleans_only_the_old_package_before_the_next_check` проверяет последовательность через барьер и настоящий временный spool. |
| 13 | Сохранение таймаута обновляет действующий `AccessGuard`. | `Saving_the_lock_timeout_changes_the_open_session`: переходы 0 -> 1, 1 -> 0, 10 -> 1 сохраняли старое поведение. |
| 14 | Поколения Reload не допускают смешивания старых и новых строк устройств, отделов и журнала. | `Overlapping_device_reloads_do_not_duplicate_rows`, `Overlapping_department_reloads_do_not_duplicate_rows`, `Overlapping_log_reloads_do_not_duplicate_rows`: строки дублировались. Дополнительно проверен поздний ответ после нового неправильного периода. |
| 15 | Установщик выбирает готовый C# v2.* своей архитектуры, включая prerelease. | `test_online_without_tag_selects_station_instead_of_python_latest`: прежний `/latest` возвращал Python. Дополнительно исключены draft, чужое семейство, неполный релиз и другая архитектура. |
| 16 | SHA256 обязательна до остановки служб и apt. Сверяется хеш скачанного пакета, пути из checksum-файла не исполняются. | `test_online_missing_empty_or_wrong_checksum_never_stops_services_or_installs`: отсутствующая и пустая сумма допускали установку. Также проверены malformed и несовпадающий хеш. |
| 17 | Веб-панель строит ссылки относительно живого корня архива, сохранённые ключи журнала остаются прежними. | `WebRemountReviewIntegration`: прежняя ссылка содержала `../../live/archive/Device7/DCIM/new.mp4`. Проверяются новые и старые записи после перемонтирования. |
| 18 | Снятие сотрудника очищает также legacy `person` в той же записи камеры. | `Removing_a_legacy_employee_assignment_survives_reopening_the_directory`: повторное открытие восстанавливало снятого сотрудника. |

## Проверки

Локальный полный Windows Release: 732 PASS, 0 FAIL, 1 Linux-specific SKIP,
733 теста. Команда: `dotnet test avalonia/AstraUsb.Tests/AstraUsb.Tests.csproj -c Release`.

Функциональная установка Windows прошла с компилятором Inno Setup 6.7.3.
Linux installer harness: 9 PASS. Синтаксис всех 10 shell-скриптов Avalonia
проверен через `bash -n`.

Настоящий Linux I/O в WSL: три сценария подмены источника перед syscall и
шесть сценариев веб-панели/FTP/перемонтирования прошли. Обнаружение физического
тома в этих стендах подменено, native операции файловой системы настоящие.

Контейнерные сценарии архива, журнала, обновлений и установки включены в
GitHub Actions. Их итог и независимое ревью будут добавлены после завершения.

## Эксплуатационные границы

Физические USB, отдельный диск архива и внешний FTP в этой задаче не
использовались. Проверки установки и служб работают с отдельными стендами.
Файловая система Linux без `renameat2(RENAME_NOREPLACE)` безопасно откажет в
очистке и сохранит оригинал. Если во время возврата из карантина исходное имя
уже занято, файл сохраняется под `.astra-clean-*`, а журнал сообщает путь.

Для установки одной командой нужен системный `python3` для разбора JSON.
Новых зависимостей приложения нет. Windows-установка теперь пользовательская;
данные прежней установки в Program Files остаются на месте и требуют переноса
при закрытой программе.
