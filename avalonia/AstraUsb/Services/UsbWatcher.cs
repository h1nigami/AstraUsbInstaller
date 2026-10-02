using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AstraUsb.Services;

/// <summary>Подключённый носитель.</summary>
/// <param name="Name">Имя устройства: sdb1 или буква диска.</param>
/// <param name="MountPoint">Точка монтирования, если носитель смонтирован.</param>
public sealed record UsbDevice(string Name, string? MountPoint,
    string? FileSystemUuid = null, string? Serial = null);

/// <summary>
/// Обнаружение съёмных носителей. Логика перенесена из Python-версии
/// (usb_monitor._parse_lsblk_tree): разделы USB-диска перечисляются по одному
/// разу, а диск с файловой системой прямо на нём отдаётся сам.
/// </summary>
public static class UsbWatcher
{
    public static IReadOnlyList<UsbDevice> List() =>
        OperatingSystem.IsWindows() ? ListWindows() : ListLinux();

    private static IReadOnlyList<UsbDevice> ListWindows()
    {
        var found = new List<UsbDevice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    GetVolumeInformation(drive.Name, IntPtr.Zero, 0, out var serial,
                        out _, out _, IntPtr.Zero, 0);
                    found.Add(new UsbDevice(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName,
                        serial == 0 ? null : serial.ToString("X8")));
                }
            }
            catch (IOException)
            {
                // Носитель извлекли между перечислением и опросом, это не наша забота.
            }
        }
        return found;
    }

    private static IReadOnlyList<UsbDevice> ListLinux(string sysRoot = "/sys/class/block",
        string uuidRoot = "/dev/disk/by-uuid", string mountsPath = "/proc/mounts",
        Func<string?>? readLsblk = null)
    {
        var json = (readLsblk ?? RunLsblk)();
        if (json is null)
            return ListSysfs(sysRoot, uuidRoot, mountsPath);

        var found = new List<UsbDevice>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("blockdevices", out var disks))
                return ListSysfs(sysRoot, uuidRoot, mountsPath);

            foreach (var disk in disks.EnumerateArray())
                CollectFromDisk(disk, found);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return ListSysfs(sysRoot, uuidRoot, mountsPath);
        }
        return found;
    }

    private static IReadOnlyList<UsbDevice> ListSysfs(string sysRoot, string uuidRoot, string mountsPath)
    {
        var found = new List<UsbDevice>();
        try
        {
            var paths = Directory.GetDirectories(sysRoot);
            var mounts = MountTable.Parse(File.ReadAllLines(mountsPath));
            var uuids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Directory.Exists(uuidRoot))
                foreach (var link in Directory.EnumerateFiles(uuidRoot))
                {
                    var target = new FileInfo(link).LinkTarget;
                    if (target is not null)
                        uuids[Path.GetFileName(target)] = Path.GetFileName(link);
                }

            foreach (var path in paths)
            {
                var real = new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
                if (!real.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith("usb", StringComparison.Ordinal)))
                    continue;
                var name = Path.GetFileName(path);
                var partition = File.Exists(Path.Combine(path, "partition"));
                if (!partition && paths.Any(p => p != path && File.Exists(Path.Combine(p, "partition"))
                    && (new DirectoryInfo(p).ResolveLinkTarget(true)?.FullName ?? p).StartsWith(real + "/", StringComparison.Ordinal)))
                    continue;
                var serialFile = Path.Combine(partition ? Path.GetDirectoryName(real)! : real, "device", "serial");
                var serial = File.Exists(serialFile) ? File.ReadAllText(serialFile).Trim() : null;
                found.Add(new UsbDevice(name, mounts.FirstOrDefault(m => m.Device == "/dev/" + name)?.MountPoint,
                    uuids.GetValueOrDefault(name), serial));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Устройство могло исчезнуть во время обхода sysfs.
        }
        return found;
    }

    private static void CollectFromDisk(JsonElement disk, List<UsbDevice> found)
    {
        if (Text(disk, "tran") != "usb")
            return;

        var name = Text(disk, "name");
        if (string.IsNullOrEmpty(name))
            return;

        if (disk.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array
            && children.GetArrayLength() > 0)
        {
            // У диска есть разделы: берём их, а сам диск пропускаем, иначе одно
            // устройство попало бы в список дважды.
            foreach (var part in children.EnumerateArray())
            {
                var partName = Text(part, "name");
                if (!string.IsNullOrEmpty(partName))
                    found.Add(new UsbDevice(partName, Text(part, "mountpoint"),
                        Text(part, "uuid"), Text(part, "serial") ?? Text(disk, "serial")));
            }
            return;
        }

        found.Add(new UsbDevice(name, Text(disk, "mountpoint"), Text(disk, "uuid"), Text(disk, "serial")));
    }

    private static string? Text(JsonElement el, string property) =>
        el.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? RunLsblk()
    {
        return RunProcess(new ProcessStartInfo
        {
            FileName = "lsblk",
            Arguments = "-J -o NAME,TRAN,TYPE,MOUNTPOINT,UUID,SERIAL",
        }, 5000);
    }

    internal static string? RunProcess(ProcessStartInfo info, int timeoutMilliseconds,
        CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMilliseconds);
        using var proc = new Process { StartInfo = info };
        try
        {
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            token.ThrowIfCancellationRequested();
            if (!proc.Start())
                return null;
            var output = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = proc.StandardError.ReadToEndAsync(timeout.Token);
            proc.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(output, errors).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            return proc.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception) { }
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformation(string root, IntPtr volumeName, uint volumeSize,
        out uint serial, out uint maxComponent, out uint flags, IntPtr filesystem, uint filesystemSize);
}
