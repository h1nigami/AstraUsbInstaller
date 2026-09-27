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
        // Расхождение значит, что карту переставили в другой регистратор или
        // номер сменили: угадывать нельзя, архив ушёл бы в чужую папку.
        if (logId is > 0 && recordingId is > 0 && logId != recordingId)
            throw new InvalidDataException($"Разные ID в журнале и записях регистратора: {logId} и {recordingId}");
        return logId ?? recordingId ?? throw new InvalidDataException("ID регистратора не найден");
    }

    /// <summary>
    /// Номер из последней строки #ID самого свежего журнала. Более старые
    /// журналы не читаются: их номер мог уже смениться.
    /// </summary>
    private static long? ReadLogId(string mountPoint)
    {
        var directory = Path.Combine(mountPoint, "LOG");
        if (!Directory.Exists(directory))
            return null;

        try
        {
            // Свежий журнал определяем по дате изменения, а не по имени:
            // у части прошивок имена не сортируются по времени.
            var file = new DirectoryInfo(directory).EnumerateFiles()
                .Where(info => info.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ThenByDescending(info => info.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (file is null)
                return null;

            // Читаем построчно и строго: битый журнал не повод брать номер
            // из записей, это ошибка, которую должен увидеть оператор.
            using var reader = new StreamReader(file.FullName, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false);
            long? id = null;
            while (reader.ReadLine() is { } line)
            {
                var position = line.IndexOf("#ID:", StringComparison.Ordinal);
                if (position < 0)
                    continue;
                id = ParseId(line[(position + 4)..].Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            }
            return id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                        or DecoderFallbackException)
        {
            throw new IOException("Не удалось прочитать журнал регистратора", error);
        }
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
