namespace AstraUsb.Services;

/// <summary>
/// Удаление видео с носителя после копирования. Перенесено из Python-версии
/// (usb_monitor._delete_source_videos) вместе с защитой, ради которой оно и
/// писалось: удаляются только файлы, которые точно лежат в назначении.
/// </summary>
public static class SourceCleaner
{
    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".mpg", ".mpeg",
            ".m4v", ".3gp", ".ts", ".flv", ".webm", ".m2ts", ".vob", ".mts",
        };

    /// <summary>
    /// Удаляет видео с носителя.
    /// </summary>
    /// <param name="sourceRoot">Корень носителя.</param>
    /// <param name="backedUp">
    /// Пути, которые точно сохранены. Видео вне этого списка не трогается:
    /// файл, который не удалось скопировать, обязан остаться на носителе.
    /// </param>
    /// <returns>Сколько файлов удалено.</returns>
    public static int DeleteBackedUpVideos(string sourceRoot, IReadOnlySet<string> backedUp)
    {
        var deleted = 0;

        foreach (var file in EnumerateFiles(sourceRoot))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file)))
                continue;

            if (!backedUp.Contains(file))
                continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception)
            {
                // Файл занят или носитель только для чтения — пропускаем,
                // потерять данные тут страшнее, чем оставить лишнее.
            }
        }

        return deleted;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
