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
    internal (string Device, ulong Inode)? SourceRootIdentity { get; init; }
    internal IReadOnlyDictionary<string, SourceFileIdentity> SourceFiles { get; init; } =
        new Dictionary<string, SourceFileIdentity>();
}

internal readonly record struct SourceFileIdentity(string Device, ulong Inode, long Length, DateTime Modified);

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
        var sourceFiles = new Dictionary<string, SourceFileIdentity>(StringComparer.Ordinal);
        (string Device, ulong Inode) sourceIdentity;
        using (var sourceDirectory = ArchiveDirectory.OpenSourceDirectory(sourceRoot))
            sourceIdentity = ArchiveDirectory.Identity(sourceDirectory);
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
                    using var sourceHandle = ArchiveDirectory.OpenSource(sourceFile);
                    var sourceIdentityBefore = ArchiveDirectory.SourceIdentity(sourceHandle);
                    var name = Path.GetFileName(sourceFile);
                    var target = Path.Combine(destDir, name);

                    if (Path.Exists(Path.Combine(targetDirectory.Path, name)))
                    {
                        var savedName = SavedCopy(targetDirectory, sourceFile);
                        if (savedName is not null)
                        {
                            var savedSize = sourceIdentityBefore.Length;
                            VerifySource();
                            backedUp.Add(sourceFile, Path.Combine(destDir, savedName));
                            sourceFiles.Add(sourceFile, sourceIdentityBefore);
                            sourceHandle.Dispose();
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
                    VerifySource();

                    copiedFiles++;
                    copiedBytes += size;
                    processedFiles++;
                    processedBytes += size;
                    backedUp.Add(sourceFile, target);
                    sourceFiles.Add(sourceFile, sourceIdentityBefore);
                    sourceHandle.Dispose();
                    onProgress?.Invoke(processedFiles, processedBytes);
                    onFile?.Invoke(processedFiles, processedBytes, Path.GetFileName(sourceFile), true);

                    void VerifySource()
                    {
                        using var current = ArchiveDirectory.OpenSource(sourceFile);
                        if (ArchiveDirectory.SourceIdentity(sourceHandle) != sourceIdentityBefore
                            || ArchiveDirectory.SourceIdentity(current) != sourceIdentityBefore)
                            throw new IOException("Исходный файл изменился во время копирования");
                    }
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
        return new CopyResult(copiedFiles, copiedBytes, backedUp, failed + walkErrors)
        {
            SourceRootIdentity = sourceIdentity,
            SourceFiles = sourceFiles,
        };
    }

    internal static (long Bytes, IReadOnlySet<string> ReusedFiles) PendingBytes(
        string sourceRoot, string destRoot, CancellationToken token)
    {
        using var destination = ArchiveGuard.Open(destRoot, create: true, requireMarker: false);
        var bytes = 0L;
        var reused = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in EnumerateDirectories(sourceRoot, () => throw new IOException("Носитель не читается")))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory);
            using var target = destination.CreateDirectory(relative == "." ? "" : relative);
            foreach (var file in Directory.GetFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (Markers.IsService(name)) continue;
                var saved = SavedCopy(target, file);
                if (saved is null)
                    bytes = checked(bytes + new FileInfo(file).Length);
                else
                {
                    reused.Add(Path.Combine(target.Root, saved));
                    // Поиск сохранённой версии также требует исходное имя в архиве.
                    reused.Add(Path.Combine(target.Root, name));
                }
            }
        }
        return (bytes, reused);
    }

    private static string? SavedCopy(ArchiveDirectory directory, string source)
    {
        var name = Path.GetFileName(source);
        return !Path.Exists(Path.Combine(directory.Path, name)) ? null
            : directory.SameFile(source, name) ? name : SameTimestampedCopy(directory, source);
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
