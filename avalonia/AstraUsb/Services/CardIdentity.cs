using System.Globalization;
using System.Text.RegularExpressions;

namespace AstraUsb.Services;

/// <summary>
/// Совместимость со старым маркером BCU-{станция}-{номер} на карте.
/// DeviceRegistry использует его только для переноса уникальной локальной
/// записи в .astra_id. Сам файл при переходе сохраняется.
/// </summary>
public static class CardIdentity
{
    /// <summary>Файл на карте, в котором лежит номер.</summary>
    public const string FileName = ".bestcam_id";

    private static readonly Regex Mask =
        new(@"^BCU-(\d{2})-(\d{4})$", RegexOptions.Compiled);

    /// <summary>Собирает номер из номера станции и порядкового.</summary>
    public static string Format(int station, int sequence) =>
        $"BCU-{Math.Clamp(station, 0, 99):00}-{Math.Clamp(sequence, 0, 9999):0000}";

    /// <summary>Опознаёт свой формат: такой номер выдан станцией, а не человеком.</summary>
    public static bool IsOurs(string? id) => id is not null && Mask.IsMatch(id.Trim());

    /// <summary>Номер станции, выдавшей номер, или null для чужого формата.</summary>
    public static int? StationOf(string? id)
    {
        var match = id is null ? Match.Empty : Mask.Match(id.Trim());
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
    }

    public static int? SequenceOf(string? id)
    {
        var match = id is null ? Match.Empty : Mask.Match(id.Trim());
        return match.Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Читает номер с карты. Пусто, если файла нет или он нечитаем.</summary>
    public static string? Read(string? mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return null;

        try
        {
            var text = File.ReadAllText(Path.Combine(mountPoint, FileName)).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception)
        {
            // Старого маркера нет или он недоступен для чтения.
            return null;
        }
    }

    /// <summary>Записывает номер на карту. False, если записать не удалось.</summary>
    public static bool Write(string? mountPoint, string id)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return false;

        try
        {
            File.WriteAllText(Path.Combine(mountPoint, FileName), id + "\n");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
