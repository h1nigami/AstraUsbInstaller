using System.Globalization;
using System.Text;

namespace AstraUsb.Services;

public static class DeviceIdentifier
{
    public static long Read(string mountPoint)
    {
        var logId = ReadLogId(mountPoint);
        var recordingId = Directory.Exists(Path.Combine(mountPoint, "DCIM"))
            ? ParseId(RecordingName.FromCard(mountPoint)?.DeviceNo)
            : null;
        if (logId is > 0 && recordingId is > 0 && logId != recordingId)
            throw new InvalidDataException($"Разные ID регистратора: {logId} и {recordingId}");
        return logId ?? recordingId ?? throw new InvalidDataException("ID регистратора не найден");
    }

    private static long? ReadLogId(string mountPoint)
    {
        var directory = Path.Combine(mountPoint, "LOG");
        if (!Directory.Exists(directory))
            return null;

        try
        {
            var files = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);
            foreach (var file in files)
            {
                using var reader = new StreamReader(file, new UTF8Encoding(false, true),
                    detectEncodingFromByteOrderMarks: false);
                for (var index = 0; index < 50; index++)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                        break;
                    var position = line.IndexOf("#ID:", StringComparison.Ordinal);
                    if (position < 0)
                        continue;
                    var value = line[(position + 4)..].Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    return ParseId(value);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new IOException("Не удалось прочитать журнал регистратора", error);
        }
        return null;
    }

    private static long? ParseId(string? text) =>
        text is { Length: > 0 }
        && text.All(c => c is >= '0' and <= '9')
        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : null;
}
