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
    public static ArchiveDirectory Open(string root, bool create = false, bool requireMarker = true,
        Settings? settings = null)
    {
        var directory = ArchiveDirectory.Open(root, create);
        try
        {
            directory.Verify();
            if (requireMarker)
                directory.RequireMarker();
            if (settings is not null && (settings.Unreadable || !IdentityMatches(directory.Root, settings)))
                throw new IOException("Подключён другой диск архива");
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }

    public static IReadOnlySet<string> PhysicalDisks(string device, string sysRoot = "/sys")
    {
        var pending = new Stack<string>();
        pending.Push(Path.Combine(sysRoot, "dev", "block", device));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var disks = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var directory = new DirectoryInfo(pending.Pop());
            var path = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            if (!visited.Add(path))
                continue;
            var number = File.ReadAllText(Path.Combine(path, "dev")).Trim();
            if (string.IsNullOrEmpty(number))
                throw new IOException("Не удалось определить физический диск архива");
            var slaves = Path.Combine(path, "slaves");
            var children = Directory.Exists(slaves) ? Directory.GetDirectories(slaves) : [];
            if (children.Length > 0)
                foreach (var child in children)
                    pending.Push(child);
            else if (File.Exists(Path.Combine(path, "partition")))
                pending.Push(Path.GetDirectoryName(path)!);
            else
            {
                if (path.Replace('\\', '/').Contains("/virtual/", StringComparison.Ordinal))
                    throw new IOException("Не удалось определить физический диск архива");
                disks.Add(number);
            }
        }
        if (disks.Count == 0)
            throw new IOException("Не удалось определить физический диск архива");
        return disks;
    }

    internal static void RequireDevice(string device)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var system = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { "/", "/usr", "/var", "/boot" })
            if (Directory.Exists(path))
                system.Add(ArchiveDirectory.DeviceAt(path));
        if (system.Contains(device))
            throw new IOException("Запись архива на системный диск запрещена");
        var archiveDisks = PhysicalDisks(device);
        foreach (var systemDevice in system)
            if (archiveDisks.Overlaps(PhysicalDisks(systemDevice)))
                throw new IOException("Запись архива на раздел системного диска запрещена");
    }

    public sealed record DestinationIdentity(string Uuid, string Serial, string? RelativePath);
    public static DestinationIdentity DescribeDestination(string root)
    {
        if (!OperatingSystem.IsLinux())
            return new DestinationIdentity("", "", null);
        using var directory = Open(root, requireMarker: false);
        var device = ArchiveDirectory.DeviceAt(directory.Path);
        var mount = Mounts().Where(m => m.Device == device && IsArchiveMedia(m.Path, root))
            .OrderByDescending(m => m.Path.Length).FirstOrDefault();
        var relative = mount.Path is null ? null : Path.GetRelativePath(mount.Path, root);
        return new DestinationIdentity(Uuid(device), Serial(device), relative == "." ? "" : relative);
    }

    public static string ResolveDestination(Settings settings)
    {
        var root = settings.BackupRoot;
        if (!OperatingSystem.IsLinux() || settings.BackupRelativePath is null
            || (string.IsNullOrEmpty(settings.BackupUuid) && string.IsNullOrEmpty(settings.BackupSerial)))
            return root;
        try
        {
            if (Directory.Exists(root) && IdentityMatches(root, settings))
                return root;
            foreach (var mount in Mounts())
            {
                if (!DeviceMatches(mount.Device, settings))
                    continue;
                var candidate = Path.GetFullPath(Path.Combine(mount.Path, settings.BackupRelativePath));
                if (IsArchiveMedia(mount.Path, candidate) && Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return root;
    }

    private static bool IdentityMatches(string root, Settings settings) =>
        !OperatingSystem.IsLinux()
        || (string.IsNullOrEmpty(settings.BackupUuid) && string.IsNullOrEmpty(settings.BackupSerial))
        || DeviceMatches(ArchiveDirectory.DeviceAt(root), settings);

    private static bool DeviceMatches(string device, Settings settings) =>
        !string.IsNullOrEmpty(settings.BackupUuid)
            ? Uuid(device) == settings.BackupUuid
            : !string.IsNullOrEmpty(settings.BackupSerial) && Serial(device) == settings.BackupSerial;

    private static IEnumerable<(string Device, string Path)> Mounts()
    {
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ');
            if (fields.Length >= 6)
                yield return (fields[2], fields[4].Replace("\\040", " ").Replace("\\011", "\t")
                    .Replace("\\012", "\n").Replace("\\134", "\\"));
        }
    }

    private static string Uuid(string device)
    {
        try
        {
            var start = new ProcessStartInfo("blkid")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-o", "value", "-s", "UUID", "/dev/block/" + device })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return "";
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : "";
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return ""; }
    }

    private static string Serial(string device)
    {
        try
        {
            var target = new DirectoryInfo("/sys/dev/block/" + device).ResolveLinkTarget(true)!;
            var directory = new DirectoryInfo(target.FullName);
            while (File.Exists(Path.Combine(directory.FullName, "partition")))
                directory = directory.Parent!;
            return File.ReadAllText(Path.Combine(directory.FullName, "device", "serial")).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NullReferenceException)
        { return ""; }
    }
    public static bool IsDeviceFolderName(string? name) =>
        name is { Length: > 6 }
        && name.StartsWith(DeviceRegistry.DeviceDirPrefix, StringComparison.Ordinal)
        && long.TryParse(name[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
        && id > 0;

    private static ProcessStartInfo? OwnershipCommand(string root, string deviceDir)
    {
        var archive = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var folder = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(deviceDir)));
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
            using var archive = Open(root);
            var folders = deviceDir is null
                ? Directory.EnumerateDirectories(archive.Path)
                    .Select(path => Path.Combine(archive.Root, Path.GetFileName(path)))
                : [deviceDir];
            foreach (var folder in folders)
            {
                if (OwnershipCommand(root, folder) is not { } command)
                    continue;
                using var child = Open(folder, requireMarker: false);
                archive.Verify();
                if (ArchiveDirectory.DeviceAt(archive.Path) != ArchiveDirectory.DeviceAt(child.Path))
                    throw new IOException("В папке устройства смонтирован другой диск");
                AnchorOwnershipCommand(command, archive, child);
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

    private static void AnchorOwnershipCommand(ProcessStartInfo command, ArchiveDirectory archive, ArchiveDirectory child)
    {
        var parent = $"/proc/{Environment.ProcessId}/";
        command.ArgumentList[1] = "--reference=" + archive.Path.Replace("/proc/self/", parent, StringComparison.Ordinal);
        command.ArgumentList[3] = child.Path.Replace("/proc/self/", parent, StringComparison.Ordinal) + "/.";
    }

    /// <summary>Ставит метку тома. False, если записать не удалось.</summary>
    public static bool Mark(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            using var directory = Open(root, create: true, requireMarker: false);
            var marker = Path.Combine(directory.Path, Markers.Archive);
            ArchiveDirectory.RejectLinks(marker, checkParents: false);
            if (File.Exists(marker))
            {
                directory.RequireMarker();
                return true;
            }
            directory.WriteText(Markers.Archive,
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
            using var directory = Open(root);
            return true;
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
            if (OperatingSystem.IsLinux())
            {
                using var directory = Open(root, requireMarker: false);
                return false;
            }
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
            return OperatingSystem.IsLinux();
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
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return archive.Equals(mount, comparison)
                || archive.StartsWith(mount + Path.DirectorySeparatorChar, comparison);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
