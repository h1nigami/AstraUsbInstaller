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
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? root);
            if (!drive.IsReady)
                return new StorageStatus(0, 0, false);

            return new StorageStatus(
                drive.TotalSize,
                drive.AvailableFreeSpace,
                drive.AvailableFreeSpace < minFreeBytes);
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

        var freed = 0L;
        var known = log?.CollectedBefore(DateTime.Now) ?? [];

        foreach (var entry in known)
        {
            if (freed >= bytesToFree)
                break;

            // Важное не трогаем даже ради места: такие записи держат по
            // случаю, и вернуть их будет неоткуда.
            if (entry.Important)
                continue;

            freed += Remove(root, entry.DestPath, log) ?? 0;
        }

        // Файлы, которых журнал не знает: копии старше журнала или принесённые
        // мимо станции. Их очередь определяется датой на диске.
        var accounted = known
            .Select(e => Full(e.DestPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in OldestFirst(root))
        {
            if (freed >= bytesToFree)
                break;

            // Файл из журнала, до которого очередь не дошла: он новее тех,
            // что уже удалены, и трогать его рано.
            if (accounted.Contains(Full(file.FullName)))
                continue;

            freed += Remove(root, file.FullName, log: null) ?? 0;
        }

        RemoveEmptyFolders(root);
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
        var files = 0;
        var bytes = 0L;

        foreach (var entry in log.CollectedBefore(olderThan))
        {
            // Срок хранения важного не касается.
            if (entry.Important)
                continue;

            if (Remove(root, entry.DestPath, log) is { } removed)
            {
                bytes += removed;
                files++;
            }
        }

        if (files > 0)
            RemoveEmptyFolders(root);

        return (files, bytes);
    }

    /// <summary>
    /// Удаляет файл и забывает запись о нём. Пропавший файл тоже забывается:
    /// запись о том, чего нет, только вводит оператора в заблуждение.
    /// </summary>
    private static long? Remove(string root, string path, CollectionLog? log)
    {
        var size = 0L;

        try
        {
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Markers.IsService(Path.GetFileName(path)))
                return null;

            var file = new FileInfo(path);
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    return null;
                if (Path.GetRelativePath(root, dir.FullName) == ".")
                    break;
            }
            if (file.Exists)
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    return null;
                size = file.Length;
                file.Delete();
            }
        }
        catch (Exception)
        {
            // Файл занят или недоступен: запись оставляем, попробуем позже.
            return null;
        }

        log?.Forget(path);
        return size;
    }

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
    private static IEnumerable<FileInfo> OldestFirst(string root)
    {
        FileInfo[] files;
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return [];
            files = new DirectoryInfo(root)
                .EnumerateFiles("*", Walk)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<FileInfo>();
        }

        return files.OrderBy(f => f.LastWriteTimeUtc);
    }

    /// <summary>Прибирает каталоги, оставшиеся пустыми после удаления.</summary>
    private static void RemoveEmptyFolders(string root)
    {
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return;
            foreach (var dir in new DirectoryInfo(root)
                         .EnumerateDirectories("*", Walk)
                         .OrderByDescending(d => d.FullName.Length))
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                    dir.Delete();
            }
        }
        catch (Exception)
        {
            // Не смогли прибрать, не беда: пустая папка никому не мешает.
        }
    }
}
