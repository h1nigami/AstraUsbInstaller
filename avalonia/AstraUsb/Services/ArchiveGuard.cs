using System.Diagnostics;
using System.Globalization;

namespace AstraUsb.Services;

/// <summary>
/// Присмотр за томом архива.
///
/// Съёмный диск может оказаться не смонтированным, и тогда запись по его
/// прежнему пути создаёт пустой каталог на системном разделе. Копии как будто
/// сохраняются, место на системном диске кончается, а настоящий архив
/// остаётся без записей. Чтобы этого не случилось, папка архива помечается
/// служебным файлом при выборе, и без метки станция писать отказывается.
/// </summary>
public static class ArchiveGuard
{
    public static bool IsDeviceFolderName(string? name) =>
        name is { Length: > 6 }
        && name.StartsWith(DeviceRegistry.DeviceDirPrefix, StringComparison.Ordinal)
        && long.TryParse(name[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0;

    private static ProcessStartInfo? OwnershipCommand(string root, string deviceDir)
    {
        var archive = new DirectoryInfo(Path.GetFullPath(root));
        var folder = new DirectoryInfo(Path.GetFullPath(deviceDir));
        if (!archive.Exists || !folder.Exists || !IsDeviceFolderName(folder.Name)
            || archive.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !string.Equals(folder.Parent?.FullName, archive.FullName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;

        var command = new ProcessStartInfo("chown") { UseShellExecute = false, CreateNoWindow = true };
        command.ArgumentList.Add("-R");
        command.ArgumentList.Add($"--reference={archive.FullName}");
        command.ArgumentList.Add("--");
        command.ArgumentList.Add(folder.FullName);
        return command;
    }

    /// <summary>Передаёт папки устройств владельцу и группе корня архива.</summary>
    public static void RepairOwnership(string root, string? deviceDir = null)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(root))
            return;
        try
        {
            foreach (var folder in deviceDir is null ? Directory.EnumerateDirectories(root) : [deviceDir])
            {
                if (OwnershipCommand(root, folder) is not { } command)
                    continue;
                using var process = Process.Start(command);
                process?.WaitForExit();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            // Отказ файловой системы менять владельца не отменяет сохранённую копию.
        }
    }

    /// <summary>Ставит метку тома. False, если записать не удалось.</summary>
    public static bool Mark(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, Markers.Archive),
                "Метка тома архива BestCam. Не удаляйте: без неё станция не пишет записи.\n");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Том на месте: каталог существует и в нём лежит метка.</summary>
    public static bool Available(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            return File.Exists(Path.Combine(root, Markers.Archive));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Архив лежит на том же разделе, что и система. Задание это запрещает:
    /// системный диск станции невелик, и записи его переполнят.
    /// </summary>
    public static bool OnSystemDrive(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var archive = Path.GetPathRoot(Path.GetFullPath(root));
            var system = Path.GetPathRoot(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } dir
                    ? dir
                    : AppContext.BaseDirectory));

            return !string.IsNullOrEmpty(archive)
                && string.Equals(archive, system, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Носитель, на котором лежит архив. Такой диск никогда не считается
    /// источником: иначе станция начала бы копировать архив сама в себя.
    /// </summary>
    public static bool IsArchiveMedia(string? mountPoint, string? archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || string.IsNullOrWhiteSpace(archiveRoot))
            return false;

        try
        {
            var mount = Path.GetFullPath(mountPoint).TrimEnd(Path.DirectorySeparatorChar);
            var archive = Path.GetFullPath(archiveRoot).TrimEnd(Path.DirectorySeparatorChar);

            return archive.Equals(mount, StringComparison.OrdinalIgnoreCase)
                || archive.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
