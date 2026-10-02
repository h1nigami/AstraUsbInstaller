namespace AstraUsb.Services;

/// <summary>Что делать, когда место в хранилище подходит к концу.</summary>
public enum StorageMode
{
    /// <summary>Только предупреждать. Ничего не удаляется.</summary>
    Warn,

    /// <summary>Освобождать место, удаляя самые ранние записи.</summary>
    Overwrite,
}

/// <summary>Состояние хранилища на момент проверки.</summary>
/// <param name="TotalBytes">Объём диска.</param>
/// <param name="FreeBytes">Сколько свободно.</param>
/// <param name="LowOnSpace">Свободного меньше заданного порога.</param>
public sealed record StorageStatus(long TotalBytes, long FreeBytes, bool LowOnSpace)
{
    public double UsedRatio => TotalBytes > 0 ? 1 - (double)FreeBytes / TotalBytes : 0;
}

/// <summary>
/// Присмотр за местом в хранилище.
///
/// По инструкции к станции возможны два поведения: предупреждать о нехватке
/// либо освобождать место, удаляя самые ранние собранные файлы. Второе
/// необратимо, поэтому удаление идёт строго от старых к новым и
/// останавливается ровно тогда, когда порог достигнут.
/// </summary>
public static class StorageManager
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static StorageStatus Check(string root, long minFreeBytes)
    {
        try
        {
            using var archive = ArchiveGuard.Open(root);
            var drive = DriveInfo.GetDrives()
                .Where(d => ArchiveGuard.IsArchiveMedia(d.RootDirectory.FullName, archive.Root))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null)
                return new StorageStatus(0, 0, false);
            if (!drive.IsReady)
                return new StorageStatus(0, 0, false);
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            archive.Verify();
            return new StorageStatus(
                total, free, free < minFreeBytes);
        }
        catch (Exception)
        {
            return new StorageStatus(0, 0, false);
        }
    }

    /// <summary>
    /// Освобождает место, удаляя самые ранние файлы, пока свободного не станет
    /// не меньше запрошенного.
    /// </summary>
    /// <param name="root">Корень хранилища копий.</param>
    /// <param name="bytesToFree">Сколько нужно освободить.</param>
    /// <param name="mode">В режиме предупреждения не удаляется ничего.</param>
    /// <param name="log">
    /// Журнал сбора. Он знает, когда файл приехал на станцию, и по нему
    /// выбирается очередь на удаление. Запись забывается вместе с файлом,
    /// иначе поиск обещал бы оператору то, чего на диске уже нет.
    /// </param>
    /// <returns>Сколько байт освобождено.</returns>
    public static long FreeUpSpace(string root, long bytesToFree, StorageMode mode,
        CollectionLog? log = null)
    {
        if (mode != StorageMode.Overwrite || bytesToFree <= 0)
            return 0;

        using var operation = OperationGuard.Acquire(exclusive: true, dbPath: log?.DatabasePath);
        var settings = Settings.Load();
        using var archive = ArchiveGuard.Open(root, settings: settings);
        var resolve = settings.ArchivePathResolver(archive.Root);

        var freed = 0L;
        var known = log?.CollectedBetween(DateTime.MinValue, DateTime.MaxValue) ?? [];
        var protectedPaths = ProtectedPaths(known, resolve);
        var now = DateTime.Now;

        foreach (var entry in known.Where(entry => entry.CollectedAt < now).OrderBy(entry => entry.CollectedAt))
        {
            if (freed >= bytesToFree)
                break;

            // Важное не трогаем даже ради места: такие записи держат по
            // случаю, и вернуть их будет неоткуда.
            if (entry.Important)
                continue;

            if (Resolve(resolve, entry.DestPath) is { } path && !protectedPaths.Contains(path))
                freed += Remove(archive, path, log, entry.DestPath) ?? 0;
        }

        // Файлы, которых журнал не знает: копии старше журнала или принесённые
        // мимо станции. Их очередь определяется датой на диске.
        var accounted = known
            .Select(e => Resolve(resolve, e.DestPath))
            .Where(path => path is not null)
            .Select(path => Full(path!))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var file in OldestFirst(archive))
        {
            if (freed >= bytesToFree)
                break;

            // Файл из журнала, до которого очередь не дошла: он новее тех,
            // что уже удалены, и трогать его рано.
            var path = Path.Combine(root, Path.GetRelativePath(archive.Path, file.FullName));
            if (accounted.Contains(Full(path)))
                continue;

            freed += Remove(archive, path, log: null) ?? 0;
        }

        archive.RemoveEmptyDeviceFolders();
        return freed;
    }

    /// <summary>
    /// Убирает то, чей срок хранения вышел.
    /// </summary>
    /// <param name="log">Журнал сбора: по нему видно, когда запись приехала.</param>
    /// <param name="olderThan">Всё, загруженное раньше этого момента, уходит.</param>
    /// <param name="root">Корень хранилища, чтобы прибрать опустевшие папки.</param>
    /// <returns>Сколько записей убрано и сколько места освободилось.</returns>
    public static (int Files, long Bytes) DeleteExpired(CollectionLog log, DateTime olderThan,
        string root)
    {
        using var operation = OperationGuard.Acquire(exclusive: true, dbPath: log.DatabasePath);
        var settings = Settings.Load();
        using var archive = ArchiveGuard.Open(root, settings: settings);
        var resolve = settings.ArchivePathResolver(archive.Root);
        var files = 0;
        var bytes = 0L;
        var known = log.CollectedBetween(DateTime.MinValue, DateTime.MaxValue);
        var protectedPaths = ProtectedPaths(known, resolve);

        foreach (var entry in known.Where(entry => entry.CollectedAt < olderThan).OrderBy(entry => entry.CollectedAt))
        {
            // Срок хранения важного не касается.
            if (entry.Important)
                continue;

            if (Resolve(resolve, entry.DestPath) is { } path
                && !protectedPaths.Contains(path)
                && Remove(archive, path, log, entry.DestPath) is { } removed)
            {
                bytes += removed;
                files++;
            }
        }

        if (files > 0)
            archive.RemoveEmptyDeviceFolders();

        return (files, bytes);
    }

    /// <summary>
    /// Удаляет файл и забывает запись о нём. Пропавший файл тоже забывается:
    /// запись о том, чего нет, только вводит оператора в заблуждение.
    /// </summary>
    private static long? Remove(ArchiveDirectory archive, string path, CollectionLog? log, string? loggedPath = null)
    {
        var size = 0L;

        try
        {
            size = archive.DeleteFile(Path.GetRelativePath(archive.Root, path));
        }
        catch (Exception)
        {
            // Файл занят или недоступен: запись оставляем, попробуем позже.
            return null;
        }

        log?.Forget(loggedPath ?? path);
        return size;
    }

    private static string? Resolve(Func<string, string> resolve, string path)
    {
        try { return resolve(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static HashSet<string> ProtectedPaths(IEnumerable<CollectedFile> known, Func<string, string> resolve) =>
        known.Where(entry => entry.Important).Select(entry => Resolve(resolve, entry.DestPath))
            .Where(path => path is not null).Select(path => path!)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Файлы хранилища от самых ранних к поздним.</summary>
    private static IEnumerable<FileInfo> OldestFirst(ArchiveDirectory archive)
    {
        FileInfo[] files;
        try
        {
            archive.Verify();
            files = new DirectoryInfo(archive.Path).EnumerateDirectories()
                .Where(d => ArchiveGuard.IsDeviceFolderName(d.Name) && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .SelectMany(d => d.EnumerateFiles("*", Walk))
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<FileInfo>();
        }

        return files.OrderBy(f => f.LastWriteTimeUtc);
    }

}
