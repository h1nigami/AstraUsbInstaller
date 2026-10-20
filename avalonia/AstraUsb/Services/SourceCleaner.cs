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
    /// Результат копирования с идентичностью сохранённых оригиналов.
    /// Файл без подтверждённой копии остаётся на носителе.
    /// </param>
    /// <returns>Сколько файлов удалено.</returns>
    public static int DeleteBackedUpVideos(string sourceRoot, CopyResult backedUp, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var deleted = 0;
        Microsoft.Win32.SafeHandles.SafeFileHandle root;
        try { root = ArchiveDirectory.OpenSourceDirectory(sourceRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось прочитать носитель для удаления видео {sourceRoot}", error);
            return 0;
        }
        using var heldRoot = root;
        VerifyRoot();
        foreach (var file in backedUp.Destinations.Keys)
        {
            token.ThrowIfCancellationRequested();
            VerifyRoot();
            if (!VideoExtensions.Contains(Path.GetExtension(file))
                || !backedUp.SourceFiles.TryGetValue(file, out var identity)) continue;

            try
            {
                ArchiveDirectory.DeleteSourceFile(root, sourceRoot, Path.GetRelativePath(sourceRoot, file), identity, token);
                deleted++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CrashLog.Write($"Не удалось удалить исходный файл {file}", error);
            }
        }

        token.ThrowIfCancellationRequested();
        if (deleted > 0) StationLog.Write($"С носителя удалено видео: {deleted}");
        return deleted;

        void VerifyRoot()
        {
            using var current = ArchiveDirectory.OpenSourceDirectory(sourceRoot);
            if (backedUp.SourceRootIdentity != ArchiveDirectory.Identity(root)
                || ArchiveDirectory.Identity(current) != backedUp.SourceRootIdentity)
                throw new IOException("Носитель сменился после копирования");
        }
    }
}
