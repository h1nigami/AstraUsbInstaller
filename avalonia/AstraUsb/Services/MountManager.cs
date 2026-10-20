using System.Diagnostics;

namespace AstraUsb.Services;

/// <summary>Смонтированный носитель.</summary>
/// <param name="Path">Точка монтирования.</param>
/// <param name="Ours">Смонтировали мы сами, значит нам же и отпускать.</param>
public sealed record Mounted(string Path, bool Ours);

/// <summary>
/// Монтирование карт на Astra Linux.
///
/// Правило udev, которое ставит установщик, запрещает рабочему столу
/// монтировать USB-накопители: две одновременные записи на один FAT портят
/// файловую систему, и стик после такого показывает нулевой объём. Раз
/// desktop не монтирует, станция делает это сама.
///
/// Порядок важен и повторяет проверенный в Python-версии: сначала ждём, не
/// смонтирует ли систему кто-то другой (обновлённые станции, ручное
/// монтирование, запуск в контейнере), и только потом монтируем сами. Своё
/// монтирование потом отпускаем, чужое не трогаем никогда.
/// </summary>
public static class MountManager
{
    /// <summary>Каталог, в который станция монтирует карты.</summary>
    public const string MountBase = "/mnt/usb_backup";

    private const string MountsFile = "/proc/mounts";

    /// <summary>Точка монтирования этого устройства, если оно уже смонтировано.</summary>
    public static string? FindExisting(string deviceName)
    {
        var device = $"/dev/{deviceName}";

        foreach (var entry in Current())
        {
            if (entry.Device == device && entry.MountPoint.Length > 0)
                return entry.MountPoint;
        }

        return null;
    }

    /// <summary>Смонтировали эту точку мы: она лежит в нашем каталоге.</summary>
    public static bool IsOurs(string? mountPoint) =>
        IsWithinMountBase(mountPoint, MountBase);

    private static bool IsWithinMountBase(string? mountPoint, string mountBase)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return false;
        try
        {
            var target = ResolveDirectoryPath(mountPoint);
            var root = ResolveDirectoryPath(mountBase);
            return target == root || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            CrashLog.Write($"Не удалось проверить папку монтирования {mountPoint}", error);
            return false;
        }
    }

    private static string ResolveDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(full)!;
        foreach (var part in full[resolved.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var directory = new DirectoryInfo(resolved);
            if (directory.LinkTarget is not null)
                resolved = directory.ResolveLinkTarget(true)!.FullName;
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>
    /// Возвращает точку монтирования устройства, монтируя его при необходимости.
    /// </summary>
    /// <param name="deviceName">Имя устройства без /dev, например sdb1.</param>
    /// <param name="grace">
    /// Сколько ждать, не смонтирует ли устройство система. Двойное монтирование
    /// одного FAT опаснее задержки.
    /// </param>
    public static Mounted? Ensure(string deviceName, TimeSpan grace, CancellationToken token = default)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(400);

        while (true)
        {
            if (token.IsCancellationRequested)
                return null;
            if (FindExisting(deviceName) is { } existing)
                return new Mounted(existing, IsOurs(existing));

            if (waited >= grace)
                break;

            if (token.WaitHandle.WaitOne(step))
                return null;
            waited += step;
        }

        return MountOurselves(deviceName, token);
    }

    /// <summary>
    /// Отпускает монтирование, если оно наше. Чужое остаётся: рабочий стол
    /// смонтировал его для человека, и отбирать это у него нельзя.
    /// </summary>
    public static bool Release(Mounted? mount)
    {
        if (mount is null)
            return true;
        if (!mount.Ours || OperatingSystem.IsWindows())
            return false;

        return ReleaseLinux(mount, MountsFile, Run, DeviceExists, MountBase);
    }

    internal static bool ReleaseLinux(Mounted mount, string mountsFile,
        Func<string, string[], CancellationToken, bool> run, Func<string, bool> deviceExists, string mountBase)
    {
        if (!mount.Ours || !IsWithinMountBase(mount.Path, mountBase))
            return false;
        try
        {
            MountEntry? MountedAtTarget() => MountTable.Parse(File.ReadAllLines(mountsFile))
                .FirstOrDefault(entry => entry.MountPoint == mount.Path);
            Exception? commandError = null;
            bool Unmount(string[] arguments)
            {
                try
                {
                    if (run("umount", arguments, CancellationToken.None))
                        return true;
                    commandError = new IOException("Команда umount завершилась с ошибкой");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    commandError = error;
                }
                return false;
            }

            var entry = MountedAtTarget();
            if (entry is not null && !Unmount([mount.Path]))
            {
                var current = MountedAtTarget();
                if (current is null || current.Device != entry.Device
                    || !current.Device.StartsWith("/dev/", StringComparison.Ordinal) || deviceExists(current.Device))
                {
                    CrashLog.Write($"Не удалось размонтировать {mount.Path}", commandError);
                    return false;
                }
                if (!Unmount(["-l", mount.Path]))
                {
                    CrashLog.Write($"Не удалось снять монтирование пропавшего {current.Device} в {mount.Path}", commandError);
                    return false;
                }
            }
            if (MountedAtTarget() is not null)
            {
                CrashLog.Write($"Монтирование {mount.Path} осталось после umount", null);
                return false;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось размонтировать {mount.Path}", error);
            return false;
        }

        try
        {
            if (Directory.Exists(mount.Path) && !Directory.EnumerateFileSystemEntries(mount.Path).Any())
                Directory.Delete(mount.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось удалить папку монтирования {mount.Path}", error);
        }
        return true;
    }

    /// <summary>Снимает свои монтирования, оставшиеся после отключения носителя.</summary>
    public static void CleanupDeadMounts()
    {
        if (!OperatingSystem.IsWindows())
            CleanupDeadMountsLinux(MountsFile, Run, DeviceExists, MountBase);
    }

    internal static void CleanupDeadMountsLinux(string mountsFile,
        Func<string, string[], CancellationToken, bool> run, Func<string, bool> deviceExists, string mountBase)
    {
        try
        {
            var entries = MountTable.Parse(File.ReadAllLines(mountsFile));
            foreach (var entry in entries)
                if (entry.Device.StartsWith("/dev/", StringComparison.Ordinal)
                    && entry.MountPoint != mountBase && IsWithinMountBase(entry.MountPoint, mountBase)
                    && !deviceExists(entry.Device))
                    ReleaseLinux(new Mounted(entry.MountPoint, Ours: true), mountsFile, run, deviceExists, mountBase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Не удалось убрать монтирования отключённых носителей", error);
        }
    }

    private static bool DeviceExists(string device)
    {
        try { File.GetAttributes(device); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static Mounted? MountOurselves(string deviceName, CancellationToken token,
        Func<string, string[], CancellationToken, bool>? run = null,
        Func<string, string?>? findExisting = null, string mountBase = MountBase)
    {
        run ??= Run;
        findExisting ??= FindExisting;
        var target = Path.Combine(mountBase, deviceName);

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Не удалось создать папку монтирования {target}: {error.Message}", error);
        }

        var errors = new List<Exception>();
        bool TryMount(string[] arguments)
        {
            try
            {
                if (run("mount", arguments, token))
                    return true;
                errors.Add(new IOException("Команда mount завершилась с ошибкой"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                errors.Add(error);
            }
            return false;
        }

        // utf8 нужен для русских имён на FAT, иначе они приезжают знаками
        // вопроса и файл потом не найти.
        if (TryMount(["-o", "rw,noatime,utf8", $"/dev/{deviceName}", target]))
            return new Mounted(target, Ours: true);
        if (findExisting(deviceName) is { } existing)
            return new Mounted(existing, existing == target || IsOurs(existing));
        if (!token.IsCancellationRequested && TryMount([$"/dev/{deviceName}", target]))
            return new Mounted(target, Ours: true);
        if (findExisting(deviceName) is { } late)
            return new Mounted(late, late == target || IsOurs(late));

        try
        {
            Directory.Delete(target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось удалить папку монтирования {target}", error);
        }
        if (token.IsCancellationRequested)
            return null;
        throw new IOException($"Не удалось смонтировать /dev/{deviceName}: {string.Join("; ", errors.Select(e => e.Message))}",
            new AggregateException(errors));
    }

    private static IReadOnlyList<MountEntry> Current(string mountsFile = MountsFile)
    {
        try
        {
            return MountTable.Parse(File.ReadAllLines(mountsFile));
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException($"Не удалось прочитать таблицу монтирования {mountsFile}: {error.Message}", error);
        }
    }

    private static bool Run(string program, string[] arguments, CancellationToken token = default)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = program,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            UsbWatcher.RunProcessChecked(info, 15_000, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }
}
