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
        !string.IsNullOrEmpty(mountPoint)
        && (mountPoint == MountBase || mountPoint.StartsWith(MountBase + "/", StringComparison.Ordinal));

    /// <summary>
    /// Возвращает точку монтирования устройства, монтируя его при необходимости.
    /// </summary>
    /// <param name="deviceName">Имя устройства без /dev, например sdb1.</param>
    /// <param name="grace">
    /// Сколько ждать, не смонтирует ли устройство система. Двойное монтирование
    /// одного FAT опаснее задержки.
    /// </param>
    public static Mounted? Ensure(string deviceName, TimeSpan grace)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(400);

        while (true)
        {
            if (FindExisting(deviceName) is { } existing)
                return new Mounted(existing, IsOurs(existing));

            if (waited >= grace)
                break;

            Thread.Sleep(step);
            waited += step;
        }

        return MountOurselves(deviceName);
    }

    /// <summary>
    /// Отпускает монтирование, если оно наше. Чужое остаётся: рабочий стол
    /// смонтировал его для человека, и отбирать это у него нельзя.
    /// </summary>
    public static void Release(Mounted? mount)
    {
        if (mount is null || !mount.Ours || OperatingSystem.IsWindows())
            return;

        Run("umount", mount.Path);

        try
        {
            if (Directory.Exists(mount.Path) && !Directory.EnumerateFileSystemEntries(mount.Path).Any())
                Directory.Delete(mount.Path);
        }
        catch (Exception)
        {
            // Пустой каталог в /mnt никому не мешает.
        }
    }

    private static Mounted? MountOurselves(string deviceName)
    {
        var target = Path.Combine(MountBase, deviceName);

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception)
        {
            // Нет прав на /mnt: монтировать всё равно не выйдет.
            return null;
        }

        // utf8 нужен для русских имён на FAT, иначе они приезжают знаками
        // вопроса и файл потом не найти.
        if (!Run("mount", "-o", "rw,noatime,utf8", $"/dev/{deviceName}", target)
            && !Run("mount", $"/dev/{deviceName}", target))
        {
            try
            {
                Directory.Delete(target);
            }
            catch (Exception)
            {
                // Каталог остался, но это не мешает работе.
            }
            return null;
        }

        return new Mounted(target, Ours: true);
    }

    private static IReadOnlyList<MountEntry> Current()
    {
        try
        {
            return MountTable.Parse(File.ReadAllLines(MountsFile));
        }
        catch (Exception)
        {
            // На Windows файла нет, на Linux он может быть недоступен.
            return Array.Empty<MountEntry>();
        }
    }

    private static bool Run(string program, params string[] arguments)
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

            using var proc = Process.Start(info);
            if (proc is null)
                return false;

            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            return proc.WaitForExit(15_000) && proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
