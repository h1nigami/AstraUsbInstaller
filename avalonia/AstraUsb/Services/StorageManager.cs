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
    /// <returns>Сколько байт освобождено.</returns>
    public static long FreeUpSpace(string root, long bytesToFree, StorageMode mode)
    {
        if (mode != StorageMode.Overwrite || bytesToFree <= 0)
            return 0;

        var freed = 0L;

        foreach (var file in OldestFirst(root))
        {
            if (freed >= bytesToFree)
                break;

            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (Exception)
            {
                // Файл занят или недоступен — идём к следующему. Место всё
                // равно освободится, просто чуть позже.
            }
        }

        RemoveEmptyFolders(root);
        return freed;
    }

    /// <summary>Файлы хранилища от самых ранних к поздним.</summary>
    private static IEnumerable<FileInfo> OldestFirst(string root)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
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
            foreach (var dir in new DirectoryInfo(root)
                         .EnumerateDirectories("*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.FullName.Length))
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                    dir.Delete();
            }
        }
        catch (Exception)
        {
            // Не смогли прибрать — не беда, пустая папка никому не мешает.
        }
    }
}
