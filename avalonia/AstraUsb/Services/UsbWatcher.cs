using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AstraUsb.Services;

/// <summary>Подключённый носитель.</summary>
/// <param name="Name">Имя устройства: sdb1 или буква диска.</param>
/// <param name="MountPoint">Точка монтирования, если носитель смонтирован.</param>
/// <param name="PortPath">
/// Адрес физического гнезда на шине, например «1-4.2». Не меняется при
/// переподключении того же гнезда, поэтому по нему плитка закрепляется за
/// конкретным разъёмом станции.
/// </param>
public sealed record UsbDevice(string Name, string? MountPoint, string? PortPath = null);

/// <summary>
/// Обнаружение съёмных носителей. Логика перенесена из Python-версии
/// (usb_monitor._parse_lsblk_tree): разделы USB-диска перечисляются по одному
/// разу, а диск с файловой системой прямо на нём отдаётся сам.
/// </summary>
public static class UsbWatcher
{
    /// <summary>Из пути sysfs достаём адрес гнезда: usb1/1-4/1-4.2/... → 1-4.2.</summary>
    private static readonly Regex PortInSysPath = new(@"/(\d+-[\d.]+)(?=/|$)", RegexOptions.Compiled);

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
                    found.Add(new UsbDevice(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName));
            }
            catch (IOException)
            {
                // Носитель извлекли между перечислением и опросом — не наша забота.
            }
        }
        return found;
    }

    private static IReadOnlyList<UsbDevice> ListLinux()
    {
        var json = RunLsblk();
        if (json is null)
            return Array.Empty<UsbDevice>();

        var found = new List<UsbDevice>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("blockdevices", out var disks))
                return found;

            foreach (var disk in disks.EnumerateArray())
                CollectFromDisk(disk, found);
        }
        catch (JsonException)
        {
            return Array.Empty<UsbDevice>();
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

        var port = ReadPortPath(name);

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
                    found.Add(new UsbDevice(partName, Text(part, "mountpoint"), port));
            }
            return;
        }

        found.Add(new UsbDevice(name, Text(disk, "mountpoint"), port));
    }

    /// <summary>
    /// Адрес гнезда, в которое воткнут носитель. Берётся из пути sysfs:
    /// /sys/class/block/sdb → .../usb1/1-4/1-4.2/... Последний такой участок и
    /// есть разъём; он одинаков при каждом подключении в тот же порт.
    /// </summary>
    public static string? ReadPortPath(string deviceName)
    {
        try
        {
            var disk = new string(deviceName.TakeWhile(c => !char.IsDigit(c)).ToArray());
            var link = $"/sys/class/block/{(disk.Length > 0 ? disk : deviceName)}";
            if (!Directory.Exists(link))
                return null;

            var real = Path.GetFullPath(new DirectoryInfo(link).ResolveLinkTarget(true)?.FullName ?? link);
            var matches = PortInSysPath.Matches(real.Replace('\\', '/'));
            return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Text(JsonElement el, string property) =>
        el.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? RunLsblk()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "lsblk",
                Arguments = "-J -o NAME,TRAN,TYPE,MOUNTPOINT",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            return proc.WaitForExit(5000) && proc.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // lsblk может отсутствовать: тогда носителей просто не видно.
            return null;
        }
    }
}
