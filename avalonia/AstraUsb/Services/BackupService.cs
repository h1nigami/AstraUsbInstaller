namespace AstraUsb.Services;

/// <summary>Ход выгрузки одной камеры.</summary>
/// <param name="Stage">Что происходит сейчас.</param>
/// <param name="Progress">Доля скопированного, 0..1.</param>
/// <param name="Detail">Строка для оператора.</param>
public sealed record BackupProgress(BackupStage Stage, double Progress, string Detail);

public enum BackupStage
{
    Scanning,
    Copying,
    Done,
    Failed,
}

/// <summary>
/// Выгрузка камеры в хранилище.
///
/// Порядок важен и повторяет проверенный в Python-версии: сначала копируем,
/// затем записываем в журнал то, что действительно доехало, и только потом,
/// если это разрешено настройками, удаляем видео с карты, причём строго по
/// списку сохранённого. Файл, который скопировать не удалось, остаётся на
/// камере.
/// </summary>
public sealed class BackupService
{
    private readonly string _dbPath;
    private readonly Settings? _configured;

    public BackupService(string dbPath, Settings? settings = null)
    {
        _dbPath = dbPath;
        _configured = settings;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var configured = settings ?? Settings.Load();
                using var operation = OperationGuard.Acquire(dbPath: dbPath);
                using var archive = ArchiveGuard.Open(configured.ResolveBackupRoot(), settings: configured);
                ArchiveGuard.RepairOwnership(archive.Root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Выгрузка повторит проверку, когда архив и блокировка станут доступны.
            }
        }
    }

    /// <summary>Папка камеры в хранилище. Имя не меняется при переименовании камеры.</summary>
    public string FolderFor(long deviceId) =>
        Path.Combine((_configured ?? Settings.Load()).ResolveBackupRoot(), DeviceRegistry.DeviceDirPrefix + deviceId);

    public async Task RunAsync(long deviceId, string mountPoint,
        IProgress<BackupProgress> progress, CancellationToken token = default)
    {
        var started = DateTime.Now;
        var stamp = started.ToString("yyyyMMdd_HHmmss");

        try
        {
            token.ThrowIfCancellationRequested();
            var settings = _configured ?? Settings.Load();
            var root = settings.ResolveBackupRoot();
            if (ReadDeviceId(mountPoint) != deviceId)
                throw new InvalidDataException($"ID носителя не совпадает с {deviceId}");

            if (settings.Unreadable)
            {
                progress.Report(new BackupProgress(BackupStage.Failed, 0,
                    "настройки станции не читаются"));
                new ActionLog(_dbPath).Write(ActionLog.Cleanup,
                    "выгрузка остановлена: файл настроек испорчен, папка архива неизвестна");
                return;
            }

            // Том архива мог не смонтироваться. Записать в его прежний путь
            // означало бы создать пустой каталог на системном разделе и
            // отчитаться об успехе, потеряв записи.
            if (!ArchiveGuard.Available(root))
            {
                progress.Report(new BackupProgress(BackupStage.Failed, 0,
                    "том архива не смонтирован"));
                new ActionLog(_dbPath).Write(ActionLog.Cleanup,
                    $"выгрузка остановлена: нет метки тома архива в {root}");
                return;
            }

            var destination = Path.Combine(root, DeviceRegistry.DeviceDirPrefix + deviceId);
            (int Files, long Bytes) total;
            using (OperationGuard.Acquire(dbPath: _dbPath))
            using (ArchiveGuard.Open(root, settings: settings))
            {
                progress.Report(new BackupProgress(BackupStage.Scanning, 0, "считаем объём"));
                total = await Task.Run(() => Measure(mountPoint, token), token);
            }
            if (ReadDeviceId(mountPoint) != deviceId)
                throw new InvalidDataException($"ID носителя не совпадает с {deviceId}");
            if (total.Files == 0)
            {
                progress.Report(new BackupProgress(BackupStage.Done, 1, "нечего копировать"));
                return;
            }

            // Освобождаем место заранее, если так настроено: иначе копирование
            // упадёт на середине и часть файлов останется недокопированной.
            PurgeExpired(settings, root);
            EnsureSpace(settings, root, total.Bytes);

            using var operation = OperationGuard.Acquire(dbPath: _dbPath);
            using var archive = ArchiveGuard.Open(root, settings: settings);
            token.ThrowIfCancellationRequested();
            if (ReadDeviceId(mountPoint) != deviceId)
                throw new InvalidDataException($"ID носителя не совпадает с {deviceId}");

            var result = await Task.Run(() => FileCopier.Copy(
                mountPoint, destination, stamp,
                (files, bytes) => progress.Report(new BackupProgress(
                    BackupStage.Copying,
                    total.Bytes > 0 ? (double)bytes / total.Bytes : 0,
                    $"{files} из {total.Files}")), token), token);

            token.ThrowIfCancellationRequested();
            archive.Verify();
            ArchiveGuard.RepairOwnership(root, destination);
            var recorded = RecordCollected(deviceId, result, started, settings, archive.Root);
            QueueForServer(settings, result);

            // Карту могли подменить прямо во время копирования: пути из
            // BackedUp тогда относятся к ушедшей карте, и удаление пошло бы
            // по совпадающим путям уже на новой.
            var sameDevice = StillSameDevice(mountPoint, deviceId);

            // Без записи в журнале поиск копию не найдёт, поэтому оригиналы
            // на карте остаются, пока сеанс не запишется.
            token.ThrowIfCancellationRequested();
            archive.Verify();
            if (settings.DeleteVideoAfterCopy && result.Failed == 0 && sameDevice && recorded)
                SourceCleaner.DeleteBackedUpVideos(mountPoint, result.BackedUp);

            var (stage, detail, logLine) =
                result.Failed > 0
                    ? (BackupStage.Failed,
                        $"не скопировано: {Numerals.Plural(result.Failed, "файл", "файла", "файлов")}",
                        $"камера {deviceId}: не скопировано "
                        + $"{Numerals.Plural(result.Failed, "файл", "файла", "файлов")}")
                : !sameDevice
                    ? (BackupStage.Failed, "носитель сменился, файлы сохранены",
                        $"камера {deviceId}: носитель сменился во время копирования, исходные файлы сохранены")
                : !recorded
                    ? (BackupStage.Failed, "копия сделана, но не записана в журнал",
                        $"камера {deviceId}: копия сделана, но не записана в журнал")
                : (BackupStage.Done,
                    $"{Numerals.Plural(result.CopiedFiles, "файл", "файла", "файлов")}, {Size(result.CopiedBytes)}",
                    $"камера {deviceId}: загружено "
                    + $"{Numerals.Plural(result.CopiedFiles, "файл", "файла", "файлов")}, {Size(result.CopiedBytes)}");

            progress.Report(new BackupProgress(stage, 1, detail));
            new ActionLog(_dbPath).Write(ActionLog.Backup, logLine);
        }
        catch (OperationCanceledException)
        {
            progress.Report(new BackupProgress(BackupStage.Failed, 0, "выгрузка прервана"));
        }
        catch (StationBusyException) { throw; }
        catch (DeviceLostException) { throw; }
        catch (Exception e)
        {
            progress.Report(new BackupProgress(BackupStage.Failed, 0,
                UserError.Report("Не удалось завершить выгрузку камеры", e)));
        }
    }

    /// <summary>
    /// Ставит собранное в очередь отправки на сервер. Отправка идёт из очереди
    /// отдельно: сеть может пропасть, а записи должны остаться на станции.
    /// </summary>
    private void QueueForServer(Settings settings, CopyResult result)
    {
        if (!settings.FtpEnabled)
            return;

        try
        {
            var queue = new FtpQueue(_dbPath);
            queue.AddRange(result.Destinations.Values);
        }
        catch (Exception)
        {
            // Очередь не главнее данных: копии уже в архиве.
        }
    }

    /// <summary>Считает, сколько предстоит скопировать. Маркеры не учитываются.</summary>
    private static (int Files, long Bytes) Measure(string mountPoint, CancellationToken token)
    {
        var files = 0;
        var bytes = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(mountPoint, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                token.ThrowIfCancellationRequested();
                if (Markers.IsService(Path.GetFileName(path)))
                    continue;
                files++;
                bytes += new FileInfo(path).Length;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException error) when (DeviceLostException.IsRemoval(error) || !Directory.Exists(mountPoint))
        {
            throw new DeviceLostException(error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Не удалось прочитать карту", error);
        }
        return (files, bytes);
    }

    /// <summary>Освобождает место под выгрузку, если включена перезапись.</summary>
    private void EnsureSpace(Settings settings, string root, long needed)
    {
        var status = StorageManager.Check(root, settings.MinFreeBytes);
        var shortfall = needed + settings.MinFreeBytes - status.FreeBytes;
        if (shortfall > 0)
            StorageManager.FreeUpSpace(root, shortfall, settings.StorageMode,
                new CollectionLog(_dbPath));
    }

    /// <summary>
    /// Убирает записи, чей срок хранения вышел. Делается перед выгрузкой:
    /// освободившееся место сразу пригодится, и станция не копит лишнего.
    /// </summary>
    private void PurgeExpired(Settings settings, string root)
    {
        if (settings.KeepDays <= 0)
            return;

        try
        {
            var (files, bytes) = StorageManager.DeleteExpired(
                new CollectionLog(_dbPath),
                DateTime.Now.AddDays(-settings.KeepDays),
                root);

            if (files > 0)
                new ActionLog(_dbPath).Write(ActionLog.Cleanup,
                    $"по сроку хранения удалено {Numerals.Plural(files, "запись", "записи", "записей")}"
                    + $", освобождено {Size(bytes)}");
        }
        catch (Exception)
        {
            // Уборка не главнее выгрузки: не вышло, значит в другой раз.
        }
    }

    /// <summary>На карте всё ещё тот же регистратор, что и при начале выгрузки.</summary>
    private static bool StillSameDevice(string mountPoint, long deviceId)
    {
        try
        {
            return ReadDeviceId(mountPoint) == deviceId;
        }
        catch (DeviceLostException) { throw; }
        catch (Exception)
        {
            return false;
        }
    }

    private static long ReadDeviceId(string mountPoint)
    {
        if (!Directory.Exists(mountPoint))
            throw new DeviceLostException();
        try { return DeviceIdentifier.Read(mountPoint); }
        catch (IOException error) when (DeviceLostException.IsRemoval(error) || !Directory.Exists(mountPoint))
        { throw new DeviceLostException(error); }
    }

    /// <summary>
    /// Заносит в журнал то, что действительно лежит в хранилище. Время загрузки
    /// ставит станция: часам камеры доверия нет. false, если записать не
    /// вышло: без журнала поиск копию не найдёт, и оператор должен это увидеть.
    /// </summary>
    private bool RecordCollected(long deviceId, CopyResult result, DateTime collectedAt,
        Settings settings, string liveRoot)
    {
        try
        {
            var resolve = settings.ArchivePathResolver(liveRoot);
            var recordedRoot = Path.GetFullPath(settings.BackupRoot);
            var log = new CollectionLog(_dbPath);
            log.Record(result.Destinations.Select(saved =>
            {
                var (source, dest) = saved;
                // Ключ сохраняет выбранный корень при смене точек монтирования.
                var loggedPath = Path.Combine(recordedRoot, Path.GetRelativePath(liveRoot, resolve(dest)));
                // Время съёмки камера пишет прямо в имя файла, и это начало
                // записи. Дата файла отмечает её закрытие и легче сбивается,
                // поэтому она идёт запасным вариантом.
                var shot = RecordingName.Parse(Path.GetFileName(source))?.ShotAt;
                long size = 0;
                try
                {
                    var info = new FileInfo(source);
                    shot ??= info.LastWriteTime;
                    size = info.Exists ? info.Length : 0;
                }
                catch (Exception)
                {
                    // Файл уже удалён автоочисткой, размер и дата не критичны.
                }
                return new CollectedFile(deviceId, loggedPath, size, shot, collectedAt);
            }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Size(long bytes)
    {
        var gb = bytes / 1024d / 1024 / 1024;
        if (gb >= 1)
            return $"{gb:0.0} ГБ";

        // Одна фотография весит меньше мегабайта, и округление до целых
        // показывало бы честно скопированный файл как «0 МБ».
        var mb = bytes / 1024d / 1024;
        return mb >= 1 ? $"{mb:0} МБ" : $"{bytes / 1024d:0} КБ";
    }
}
