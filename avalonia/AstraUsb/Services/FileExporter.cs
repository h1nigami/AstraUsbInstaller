namespace AstraUsb.Services;

/// <summary>Чем закончилась выгрузка.</summary>
/// <param name="Copied">Сколько файлов выгружено.</param>
/// <param name="Missing">Сколько не нашлось в хранилище.</param>
/// <param name="Failed">Сколько не удалось скопировать.</param>
/// <param name="Bytes">Сколько байт выгружено.</param>
public sealed record ExportResult(int Copied, int Missing, int Failed, long Bytes);

/// <summary>
/// Выгрузка найденных записей наружу: на флешку, в сетевую папку, куда укажут.
///
/// Файлы кладутся в отдельную папку с датой выгрузки. Имена в хранилище
/// повторяются у разных камер, поэтому при совпадении к имени добавляется
/// номер: молча затирать чужую запись нельзя.
/// </summary>
public static class FileExporter
{
    /// <param name="paths">Пути к файлам в хранилище.</param>
    /// <param name="destination">Куда выгружать.</param>
    /// <param name="stamp">Метка времени в имени папки выгрузки.</param>
    /// <param name="progress">Сколько файлов из скольких уже сделано.</param>
    public static ExportResult Export(IReadOnlyList<string> paths, string destination,
        DateTime stamp, Action<int, int>? progress = null, string? archiveRoot = null, string? dbPath = null)
    {
        using var operation = OperationGuard.Acquire(dbPath: dbPath);
        var settings = Settings.Load();
        using var sourceArchive = ArchiveGuard.Open(archiveRoot ?? settings.ResolveBackupRoot(), settings: settings);
        var resolve = settings.ArchivePathResolver(sourceArchive.Root);
        using var root = ArchiveGuard.Open(destination, create: true, requireMarker: false);
        using var directory = root.CreateDirectory($"Выгрузка_{stamp:yyyyMMdd_HHmmss}");
        var folder = directory.Path;

        var copied = 0;
        var missing = 0;
        var failed = 0;
        var bytes = 0L;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < paths.Count; i++)
        {
            progress?.Invoke(i, paths.Count);

            var source = paths[i];
            try
            {
                var relative = Path.GetRelativePath(sourceArchive.Root, resolve(source));
                var name = FreeName(folder, Path.GetFileName(source), taken);
                bytes += directory.CopyFile(sourceArchive, relative, name);
                copied++;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
                                         || error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 })
            {
                missing++;
            }
            catch (Exception)
            {
                // Место кончилось, носитель вынули, права не те: остальные
                // файлы всё равно стоит попробовать.
                failed++;
            }
        }

        progress?.Invoke(paths.Count, paths.Count);
        return new ExportResult(copied, missing, failed, bytes);
    }

    /// <summary>Имя, которое в этой папке ещё не занято.</summary>
    private static string FreeName(string folder, string name, HashSet<string> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        var candidate = name;
        for (var n = 2; taken.Contains(candidate) || Path.Exists(Path.Combine(folder, candidate)); n++)
            candidate = $"{stem}_{n}{extension}";

        taken.Add(candidate);
        return candidate;
    }
}
