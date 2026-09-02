using System.Text.RegularExpressions;

namespace AstraUsb.Services;

/// <summary>Чем удостоверяется подключённый регистратор.</summary>
/// <param name="Kind">Каким способом получен идентификатор.</param>
/// <param name="Value">Сам идентификатор; пуст, если опознать нечем.</param>
public sealed record DeviceIdentity(IdentityKind Kind, string Value)
{
    public bool IsKnown => Kind != IdentityKind.Unknown && Value.Length > 0;

    public static readonly DeviceIdentity Unknown = new(IdentityKind.Unknown, "");
}

public enum IdentityKind
{
    /// <summary>Номер, который прошивка пишет в свой журнал. Живёт в аппарате.</summary>
    FirmwareId,

    /// <summary>Наш маркер на карте. Запасной путь: карту можно заменить.</summary>
    CardMarker,

    /// <summary>Опознать нечем — устройство будет заведено заново.</summary>
    Unknown,
}

/// <summary>
/// Опознание регистратора BESTCAM.
///
/// Серийник USB для этого не годится: аппарат представляется как
/// «Linux File-Stor Gadget» с зашитым серийником 123456789ABC, одинаковым у
/// всех экземпляров, — из-за него разные камеры выглядели одним устройством.
/// Зато прошивка пишет собственный номер в журнал на карте:
///
///     2026/09/02-15:49:29 #ID:2222222 #Включение системы
///
/// Номер живёт в самом регистраторе, поэтому переживает замену карты: на новой
/// карте журнал будет создан заново и снова с этим номером.
/// </summary>
public static class DeviceIdentifier
{
    private const string LogFolder = "LOG";

    /// <summary>Заводское значение: номер не прописан, опознавать по нему нельзя.</summary>
    private static readonly Regex FactoryId = new(@"^0+([-_]0+)?$", RegexOptions.Compiled);

    private static readonly Regex IdInLog = new(@"#ID:\s*([0-9A-Za-z][0-9A-Za-z_-]*)", RegexOptions.Compiled);

    /// <summary>
    /// Определяет, чем удостоверяется носитель: сначала номер из журнала,
    /// затем маркер на карте.
    /// </summary>
    public static DeviceIdentity Resolve(string? mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return DeviceIdentity.Unknown;

        var firmware = ReadFirmwareId(mountPoint);
        if (firmware is not null)
            return new DeviceIdentity(IdentityKind.FirmwareId, firmware);

        var marker = DeviceRegistry.ReadDeviceIdFromUsb(mountPoint);
        return marker is { } id
            ? new DeviceIdentity(IdentityKind.CardMarker, id.ToString())
            : DeviceIdentity.Unknown;
    }

    /// <summary>Читает номер из самого свежего журнала камеры.</summary>
    public static string? ReadFirmwareId(string mountPoint)
    {
        foreach (var file in LogFilesNewestFirst(mountPoint))
        {
            string[] lines;
            try
            {
                // Журналы небольшие, но читаем ограниченно: номер стоит в начале.
                lines = File.ReadLines(file).Take(50).ToArray();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var line in lines)
            {
                var match = IdInLog.Match(line);
                if (!match.Success)
                    continue;

                var value = match.Groups[1].Value;
                if (FactoryId.IsMatch(value))
                    return null; // номер не прописан — опознавать нечем

                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string> LogFilesNewestFirst(string mountPoint)
    {
        var dir = Path.Combine(mountPoint, LogFolder);
        try
        {
            if (!Directory.Exists(dir))
                return Array.Empty<string>();

            return Directory.EnumerateFiles(dir)
                .Where(f => Path.GetExtension(f).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}
