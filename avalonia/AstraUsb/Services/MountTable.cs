namespace AstraUsb.Services;

/// <summary>Строка таблицы монтирования.</summary>
/// <param name="Device">Устройство, например /dev/sdb1.</param>
/// <param name="MountPoint">Куда смонтировано.</param>
/// <param name="FileSystem">Тип файловой системы.</param>
/// <param name="Options">Параметры монтирования через запятую.</param>
public sealed record MountEntry(string Device, string MountPoint, string FileSystem, string Options)
{
    /// <summary>Смонтировано только для чтения.</summary>
    public bool ReadOnly =>
        Options.Split(',').Any(o => o.Trim() == "ro");
}

/// <summary>
/// Разбор /proc/mounts.
///
/// Ядро экранирует в путях пробелы и другие разделители восьмеричными кодами
/// (\040), поэтому наивное чтение по пробелу ломается на флешке с пробелом в
/// метке. Разбор вынесен отдельно от работы с системой, чтобы проверяться без
/// прав root.
/// </summary>
public static class MountTable
{
    public static IReadOnlyList<MountEntry> Parse(IEnumerable<string> lines)
    {
        var found = new List<MountEntry>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;

            found.Add(new MountEntry(
                Unescape(parts[0]),
                Unescape(parts[1]),
                Unescape(parts[2]),
                Unescape(parts[3])));
        }

        return found;
    }

    /// <summary>Возвращает восьмеричные последовательности ядра в обычные знаки.</summary>
    public static string Unescape(string field)
    {
        if (!field.Contains('\\'))
            return field;

        var result = new System.Text.StringBuilder(field.Length);

        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
            {
                var code = (field[i + 1] - '0') * 64 + (field[i + 2] - '0') * 8 + (field[i + 3] - '0');
                result.Append((char)code);
                i += 3;
                continue;
            }

            result.Append(field[i]);
        }

        return result.ToString();
    }

    private static bool IsOctal(char c) => c >= '0' && c <= '7';
}
