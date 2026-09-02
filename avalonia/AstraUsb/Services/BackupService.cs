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
/// затем записываем в журнал то, что действительно доехало, и только потом —
/// если это разрешено настройками — удаляем видео с карты, причём строго по
/// списку сохранённого. Файл, который скопировать не удалось, остаётся на
/// камере.
/// </summary>
public sealed class BackupService
{
    private readonly string _dbPath;
    private readonly Settings _settings;

    public BackupService(string dbPath, Settings settings)
    {
        _dbPath = dbPath;
        _settings = settings;
    }

    /// <summary>Папка камеры в хранилище. Имя не меняется при переименовании камеры.</summary>
    public string FolderFor(long deviceId) =>
        Path.Combine(_settings.BackupRoot, DeviceRegistry.DeviceDirPrefix + deviceId);

    public async Task RunAsync(long deviceId, string mountPoint,
        IProgress<BackupProgress> progress, CancellationToken token = default)
    {
        progress.Report(new BackupProgress(BackupStage.Scanning, 0, "считаем объём"));

        var started = DateTime.Now;
        var stamp = started.ToString("yyyyMMdd_HHmmss");
        var destination = FolderFor(deviceId);

        try
        {
            var total = await Task.Run(() => Measure(mountPoint), token);
            if (total.Files == 0)
            {
                progress.Report(new BackupProgress(BackupStage.Done, 1, "нечего копировать"));
                return;
            }

            // Освобождаем место заранее, если так настроено: иначе копирование
            // упадёт на середине и часть файлов останется недокопированной.
            EnsureSpace(total.Bytes);

            var result = await Task.Run(() => FileCopier.Copy(
                mountPoint, destination, stamp,
                (files, bytes) => progress.Report(new BackupProgress(
                    BackupStage.Copying,
                    total.Bytes > 0 ? (double)bytes / total.Bytes : 0,
                    $"{files} из {total.Files}"))), token);

            RecordCollected(deviceId, mountPoint, destination, result, started);

            if (_settings.DeleteVideoAfterCopy && result.Failed == 0)
                SourceCleaner.DeleteBackedUpVideos(mountPoint, result.BackedUp);

            progress.Report(result.Failed == 0
                ? new BackupProgress(BackupStage.Done, 1,
                    $"{result.CopiedFiles} файлов, {Size(result.CopiedBytes)}")
                : new BackupProgress(BackupStage.Failed, 1,
                    $"{result.Failed} не скопировано"));
        }
        catch (OperationCanceledException)
        {
            progress.Report(new BackupProgress(BackupStage.Failed, 0, "выгрузка прервана"));
        }
        catch (Exception e)
        {
            progress.Report(new BackupProgress(BackupStage.Failed, 0, e.Message));
        }
    }

    /// <summary>Считает, сколько предстоит скопировать. Маркеры не учитываются.</summary>
    private static (int Files, long Bytes) Measure(string mountPoint)
    {
        var files = 0;
        var bytes = 0L;
        try
        {
            foreach (var path in Directory.EnumerateFiles(mountPoint, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                if (name == DeviceRegistry.DeviceIdFile || name == CardIdentity.FileName)
                    continue;
                files++;
                bytes += new FileInfo(path).Length;
            }
        }
        catch (Exception)
        {
            // Карту вынули посреди подсчёта — вернём то, что успели.
        }
        return (files, bytes);
    }

    /// <summary>Освобождает место под выгрузку, если включена перезапись.</summary>
    private void EnsureSpace(long needed)
    {
        var status = StorageManager.Check(_settings.BackupRoot, _settings.MinFreeBytes);
        var shortfall = needed + _settings.MinFreeBytes - status.FreeBytes;
        if (shortfall > 0)
            StorageManager.FreeUpSpace(_settings.BackupRoot, shortfall, _settings.StorageMode);
    }

    /// <summary>
    /// Заносит в журнал то, что действительно лежит в хранилище. Время загрузки
    /// ставит станция: часам камеры доверия нет.
    /// </summary>
    private void RecordCollected(long deviceId, string mountPoint, string destination,
        CopyResult result, DateTime collectedAt)
    {
        try
        {
            var log = new CollectionLog(_dbPath);
            log.Record(result.BackedUp.Select(source =>
            {
                var relative = Path.GetRelativePath(mountPoint, source);
                var dest = Path.Combine(destination, relative);
                DateTime? shot = null;
                long size = 0;
                try
                {
                    var info = new FileInfo(source);
                    shot = info.LastWriteTime;
                    size = info.Exists ? info.Length : 0;
                }
                catch (Exception)
                {
                    // Файл уже удалён автоочисткой — размер и дата не критичны.
                }
                return new CollectedFile(deviceId, dest, size, shot, collectedAt);
            }));
        }
        catch (Exception)
        {
            // Журнал не главнее данных: копии уже на месте.
        }
    }

    private static string Size(long bytes)
    {
        var gb = bytes / 1024d / 1024 / 1024;
        return gb >= 1 ? $"{gb:0.0} ГБ" : $"{bytes / 1024d / 1024:0} МБ";
    }
}
