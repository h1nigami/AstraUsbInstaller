using System.Globalization;
using System.Text;

namespace AstraUsb.Services;

/// <summary>
/// ID регистратора из журнала LOG и имён записей DCIM, по тем же правилам,
/// что usb_monitor._read_device_id в Python-версии.
/// </summary>
public static class DeviceIdentifier
{
    public static long Read(string mountPoint)
    {
        var logId = ReadLogId(mountPoint);
        var recordingId = Directory.Exists(Path.Combine(mountPoint, "DCIM"))
            ? ReadRecordingId(mountPoint)
            : null;
        // Журнал главнее: имена старых записей хранят прежний номер, пока
        // регистратор не перезапишет карту, а журнал ведёт уже новый.
        return logId ?? recordingId ?? throw new InvalidDataException("ID регистратора не найден");
    }

    private static long? ReadLogId(string mountPoint)
    {
        var directory = Path.Combine(mountPoint, "LOG");
        if (!Directory.Exists(directory))
            return null;

        FileInfo[] files;
        try
        {
            // Свежий журнал определяем по дате изменения, а не по имени:
            // у части прошивок имена не сортируются по времени.
            files = new DirectoryInfo(directory).EnumerateFiles()
                .Where(file => file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Не удалось прочитать журнал регистратора", error);
        }

        foreach (var file in files)
        {
            string[] lines;
            try
            {
                // Битые байты не повод терять номер: строка с #ID обычно цела.
                // Метку порядка байтов не угадываем: FF FE в мусоре иначе
                // превратил бы весь файл в UTF-16.
                using var reader = new StreamReader(file.FullName, new UTF8Encoding(false, false),
                    detectEncodingFromByteOrderMarks: false);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Последняя запись #ID в файле самая свежая.
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var position = lines[index].IndexOf("#ID:", StringComparison.Ordinal);
                if (position < 0)
                    continue;
                var value = lines[index][(position + 4)..].Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (ParseId(value) is { } id)
                    return id;
            }
        }
        return null;
    }

    /// <summary>Номер из самой свежей записи, где он прописан.</summary>
    private static long? ReadRecordingId(string mountPoint)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(mountPoint, "DCIM"), "*", SearchOption.AllDirectories)
                .Where(path => SourceCleaner.VideoExtensions.Contains(Path.GetExtension(path)))
                .Select(path => RecordingName.Parse(Path.GetFileName(path)))
                .Where(info => info is not null)
                .Select(info => (Info: info!, Id: ParseId(info!.DeviceNo)))
                .Where(item => item.Id is not null)
                .OrderByDescending(item => item.Info.ShotAt)
                .ThenByDescending(item => item.Info.Sequence)
                .Select(item => item.Id)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Не удалось прочитать записи регистратора", error);
        }
    }

    private static long? ParseId(string? text) =>
        text is { Length: > 0 }
        && text.All(c => c is >= '0' and <= '9')
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : null;
}
