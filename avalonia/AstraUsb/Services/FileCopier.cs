using System.Text.RegularExpressions;

namespace AstraUsb.Services;

public sealed class DeviceLostException : IOException
{
    public DeviceLostException(Exception? inner = null) : base("Носитель отключён во время чтения", inner) { }

    internal static bool IsRemoval(IOException error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var code = current is System.ComponentModel.Win32Exception native ? native.NativeErrorCode
                : current is IOException ? current.HResult & 0xffff : -1;
            if (OperatingSystem.IsLinux() ? code is 5 or 6 or 19 or 116 : code is 21 or 1117 or 1167)
                return true;
        }
        return false;
    }
}

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
    public static CopyResult Copy(
        string sourceRoot,
        string destRoot,
        string timestamp,
        Action<int, long>? onProgress = null,
        CancellationToken token = default,
        Action<int, long, string, bool>? onFile = null)
    {
        token.ThrowIfCancellationRequested();
        var copiedFiles = 0;
        var copiedBytes = 0L;
        var processedFiles = 0;
        var processedBytes = 0L;
        var failed = 0;
        var backedUp = new Dictionary<string, string>(StringComparer.Ordinal);
        ArchiveDirectory destination;
        try { destination = ArchiveGuard.Open(destRoot, create: true, requireMarker: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось открыть папку назначения {destRoot}", error);
            var errors = 0;
            var count = EnumerateDirectories(sourceRoot, () => errors++)
                .Sum(dir => Directory.GetFiles(dir).Count(f => !Markers.IsService(Path.GetFileName(f))));
            return new CopyResult(0, 0, backedUp, Math.Max(1, count + errors));
        }
        using var heldDestination = destination;

        // Каталог, который не удалось прочитать, считается неудачей: иначе
        // выдернутый посреди копирования носитель давал бы зелёное «Готово»
        // с неполным числом файлов.
        var walkErrors = 0;
        foreach (var dir in EnumerateDirectories(sourceRoot, () =>
                 {
                     walkErrors++;
                     if (!Directory.Exists(sourceRoot))
                         throw new DeviceLostException();
                 }))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, dir);
            var destDir = relative == "." ? destRoot : Path.Combine(destRoot, relative);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (IOException error) when (DeviceLostException.IsRemoval(error))
            {
                throw new DeviceLostException(error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CrashLog.Write($"Не удалось прочитать каталог {dir}", error);
                if (!Directory.Exists(sourceRoot))
                    throw new DeviceLostException();
                walkErrors++;
                continue;
            }

            var payload = files
                .Where(f => !Markers.IsService(Path.GetFileName(f)))
                .ToArray();

            ArchiveDirectory targetDirectory;
            try
            {
                targetDirectory = destination.CreateDirectory(relative == "." ? "" : relative);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CrashLog.Write($"Не удалось создать папку назначения {destDir}", error);
                // Диск назначения исчез посреди копирования. Всё содержимое
                // каталога считается неперенесённым: удалять с источника нельзя.
                failed += payload.Length;
                continue;
            }
            using var heldDirectory = targetDirectory;

            foreach (var sourceFile in payload)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var name = Path.GetFileName(sourceFile);
                    var target = Path.Combine(destDir, name);

                    if (Path.Exists(Path.Combine(targetDirectory.Path, name)))
                    {
                        var savedName = targetDirectory.SameFile(sourceFile, name)
                            ? name : SameTimestampedCopy(targetDirectory, sourceFile);
                        if (savedName is not null)
                        {
                            var savedSize = new FileInfo(sourceFile).Length;
                            backedUp.Add(sourceFile, Path.Combine(destDir, savedName));
                            processedFiles++;
                            processedBytes += savedSize;
                            onProgress?.Invoke(processedFiles, processedBytes);
                            onFile?.Invoke(processedFiles, processedBytes, name, false);
                            continue;
                        }

                        // Файл изменился: прежнюю копию сохраняем, новую кладём
                        // рядом с отметкой времени.
                        var stem = Path.GetFileNameWithoutExtension(sourceFile);
                        var ext = Path.GetExtension(sourceFile);
                        name = $"{stem}_{timestamp}{ext}";
                        target = Path.Combine(destDir, name);
                    }

                    var size = targetDirectory.CopyFile(sourceFile, name, token);

                    copiedFiles++;
                    copiedBytes += size;
                    processedFiles++;
                    processedBytes += size;
                    backedUp.Add(sourceFile, target);
                    onProgress?.Invoke(processedFiles, processedBytes);
                    onFile?.Invoke(processedFiles, processedBytes, Path.GetFileName(sourceFile), true);
                }
                catch (OperationCanceledException) { throw; }
                catch (DeviceLostException) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    CrashLog.Write($"Не скопирован файл {sourceFile} в {destDir}", error);
                    if (!Directory.Exists(sourceRoot))
                        throw new DeviceLostException();
                    // Намеренно не добавляем в backedUp: файл останется на носителе.
                    failed++;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        return new CopyResult(copiedFiles, copiedBytes, backedUp, failed + walkErrors);
    }

    private static string? SameTimestampedCopy(ArchiveDirectory directory, string source)
    {
        var stem = Regex.Escape(Path.GetFileNameWithoutExtension(source));
        var extension = Regex.Escape(Path.GetExtension(source));
        var pattern = new Regex($@"\A{stem}_\d{{8}}_\d{{6}}{extension}\z");
        foreach (var candidate in Directory.GetFiles(directory.Path))
        {
            var name = Path.GetFileName(candidate);
            if (pattern.IsMatch(name) && directory.IsRegularFile(name) && directory.SameFile(source, name))
                return name;
        }
        return null;
    }

    /// <summary>Обход в глубину, где каждый нечитаемый каталог отмечается через onError.</summary>
    internal static IEnumerable<string> EnumerateDirectories(string root, Action onError)
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
            catch (IOException error) when (DeviceLostException.IsRemoval(error))
            {
                throw new DeviceLostException(error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                CrashLog.Write($"Не удалось обойти каталог {dir}", error);
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
