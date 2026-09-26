namespace AstraUsb.Services;

/// <summary>Итог сеанса копирования.</summary>
/// <param name="CopiedFiles">Сколько файлов скопировано в этот раз.</param>
/// <param name="CopiedBytes">Сколько байт перенесено.</param>
/// <param name="Destinations">
/// Пути сохранённых файлов на источнике и соответствующие им пути в архиве.
/// </param>
/// <param name="Failed">Сколько файлов скопировать не удалось.</param>
public sealed record CopyResult(
    int CopiedFiles,
    long CopiedBytes,
    IReadOnlyDictionary<string, string> Destinations,
    int Failed)
{
    public IReadOnlySet<string> BackedUp => Destinations.Keys.ToHashSet(StringComparer.Ordinal);
}

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
        var backedUp = new Dictionary<string, string>(StringComparer.Ordinal);

        // Каталог, который не удалось прочитать, считается неудачей: иначе
        // выдернутый посреди копирования носитель давал бы зелёное «Готово»
        // с неполным числом файлов.
        var walkErrors = 0;
        foreach (var dir in EnumerateDirectories(sourceRoot, () => walkErrors++))
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
                walkErrors++;
                continue;
            }

            var payload = files
                .Where(f => !Markers.IsService(Path.GetFileName(f)))
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
                            backedUp.Add(sourceFile, target);
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
                    backedUp.Add(sourceFile, target);
                    onProgress?.Invoke(copiedFiles, copiedBytes);
                }
                catch (Exception)
                {
                    // Намеренно не добавляем в backedUp: файл останется на носителе.
                    failed++;
                }
            }
        }

        return new CopyResult(copiedFiles, copiedBytes, backedUp, failed + walkErrors);
    }

    private static bool SameFile(string source, string target)
    {
        var a = new FileInfo(source);
        var b = new FileInfo(target);
        return a.Length == b.Length
               && (a.LastWriteTimeUtc - b.LastWriteTimeUtc).Duration() < SameTime;
    }

    /// <summary>Обход в глубину, где каждый нечитаемый каталог отмечается через onError.</summary>
    private static IEnumerable<string> EnumerateDirectories(string root, Action onError)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            yield return dir;

            string[] nested;
            try
            {
                nested = Directory.GetDirectories(dir);
            }
            catch (Exception)
            {
                onError();
                continue;
            }

            // Ссылки на каталоги не обходим: петля ссылок копировала бы без конца.
            for (var index = nested.Length - 1; index >= 0; index--)
                if (new DirectoryInfo(nested[index]).LinkTarget is null)
                    pending.Push(nested[index]);
        }
    }
}
