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
        DateTime stamp, Action<int, int>? progress = null)
    {
        var folder = Path.Combine(destination, $"Выгрузка_{stamp:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);

        var copied = 0;
        var missing = 0;
        var failed = 0;
        var bytes = 0L;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < paths.Count; i++)
        {
            progress?.Invoke(i, paths.Count);

            var source = paths[i];
            if (!File.Exists(source))
            {
                missing++;
                continue;
            }

            try
            {
                var target = Path.Combine(folder, FreeName(folder, Path.GetFileName(source), taken));
                File.Copy(source, target);
                bytes += new FileInfo(target).Length;
                copied++;
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
        for (var n = 2; taken.Contains(candidate) || File.Exists(Path.Combine(folder, candidate)); n++)
            candidate = $"{stem}_{n}{extension}";

        taken.Add(candidate);
        return candidate;
    }
}
