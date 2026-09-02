namespace AstraUsb;

/// <summary>Итог сеанса копирования.</summary>
/// <param name="CopiedFiles">Сколько файлов скопировано в этот раз.</param>
/// <param name="CopiedBytes">Сколько байт перенесено.</param>
/// <param name="BackedUp">
/// Пути на источнике, которые точно лежат в назначении — скопированы сейчас или
/// уже были там в том же виде. Удалять с источника можно только их.
/// </param>
/// <param name="Failed">Сколько файлов скопировать не удалось.</param>
public sealed record CopyResult(
    int CopiedFiles,
    long CopiedBytes,
    IReadOnlySet<string> BackedUp,
    int Failed);

/// <summary>
/// Инкрементальное копирование. Перенесено из Python-версии
/// (usb_monitor._copy_files) вместе с главным свойством: файл, который
/// скопировать не удалось, не попадает в список сохранённых и потому не
/// может быть удалён с носителя.
/// </summary>
public static class FileCopier
{
    /// <summary>Совпадение времени с точностью до секунды, как в оригинале.</summary>
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(1);

    public static CopyResult Copy(
        string sourceRoot,
        string destRoot,
        string timestamp,
        Action<int, long>? onProgress = null)
    {
        var copiedFiles = 0;
        var copiedBytes = 0L;
        var failed = 0;
        var backedUp = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in EnumerateDirectories(sourceRoot))
        {
            var relative = Path.GetRelativePath(sourceRoot, dir);
            var destDir = relative == "." ? destRoot : Path.Combine(destRoot, relative);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                continue;
            }

            var payload = files
                .Where(f => Path.GetFileName(f) != DeviceRegistry.DeviceIdFile)
                .ToArray();

            try
            {
                Directory.CreateDirectory(destDir);
            }
            catch (Exception)
            {
                // Диск назначения исчез посреди копирования. Всё содержимое
                // каталога считается неперенесённым: удалять с источника нельзя.
                failed += payload.Length;
                continue;
            }

            foreach (var sourceFile in payload)
            {
                try
                {
                    var target = Path.Combine(destDir, Path.GetFileName(sourceFile));

                    if (File.Exists(target))
                    {
                        if (SameFile(sourceFile, target))
                        {
                            backedUp.Add(sourceFile);
                            continue;
                        }

                        // Файл изменился: прежнюю копию сохраняем, новую кладём
                        // рядом с отметкой времени.
                        var name = Path.GetFileNameWithoutExtension(sourceFile);
                        var ext = Path.GetExtension(sourceFile);
                        target = Path.Combine(destDir, $"{name}_{timestamp}{ext}");
                    }

                    var size = new FileInfo(sourceFile).Length;
                    File.Copy(sourceFile, target, overwrite: false);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(sourceFile));

                    copiedFiles++;
                    copiedBytes += size;
                    backedUp.Add(sourceFile);
                    onProgress?.Invoke(copiedFiles, copiedBytes);
                }
                catch (Exception)
                {
                    // Намеренно не добавляем в backedUp: файл останется на носителе.
                    failed++;
                }
            }
        }

        return new CopyResult(copiedFiles, copiedBytes, backedUp, failed);
    }

    private static bool SameFile(string source, string target)
    {
        var a = new FileInfo(source);
        var b = new FileInfo(target);
        return a.Length == b.Length
               && (a.LastWriteTimeUtc - b.LastWriteTimeUtc).Duration() < SameTime;
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        yield return root;
        IEnumerable<string> nested;
        try
        {
            nested = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var dir in nested)
            yield return dir;
    }
}
