using System.Diagnostics;
using System.Text.Json;

namespace AstraUsb;

/// <summary>Подключённый носитель: имя устройства и точка монтирования, если есть.</summary>
public sealed record UsbDevice(string Name, string? MountPoint);

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
                    found.Add(new UsbDevice(partName, Text(part, "mountpoint")));
            }
            return;
        }

        found.Add(new UsbDevice(name, Text(disk, "mountpoint")));
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
