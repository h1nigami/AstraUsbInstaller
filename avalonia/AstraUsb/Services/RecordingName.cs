using System.Globalization;
using System.Text.RegularExpressions;

namespace AstraUsb.Services;

/// <summary>Что камера записала в имя файла.</summary>
/// <param name="Model">Модель, например A11.</param>
/// <param name="DeviceNo">Номер устройства, прописанный в камере.</param>
/// <param name="PersonnelNo">Номер сотрудника, прописанный в камере.</param>
/// <param name="ShotAt">Начало записи по часам камеры.</param>
/// <param name="Sequence">Порядковый номер записи.</param>
public sealed record RecordingInfo(
    string Model,
    string DeviceNo,
    string PersonnelNo,
    DateTime ShotAt,
    int Sequence)
{
    /// <summary>Номер прописан, а не оставлен заводским.</summary>
    public bool HasDeviceNo => DeviceNo.Length > 0 && DeviceNo.Trim('0').Length > 0;

    public bool HasPersonnelNo => PersonnelNo.Length > 0 && PersonnelNo.Trim('0').Length > 0;
}

/// <summary>
/// Разбор имён записей BESTCAM: A11_2222222_222222_20260902180118_0001.mp4.
///
/// Метаданных внутри файла камера почти не пишет, только время создания.
/// Зато имя несёт модель, номер устройства, номер сотрудника и время начала
/// записи. Это надёжнее и журнала (его может не быть), и даты файла (она
/// меняется при копировании).
/// </summary>
public static class RecordingName
{
    private static readonly Regex Pattern = new(
        @"^(?<model>[A-Za-z0-9]+)_(?<device>\d+)_(?<person>\d+)_(?<stamp>\d{14})_(?<seq>\d+)",
        RegexOptions.Compiled);

    public static RecordingInfo? Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        var match = Pattern.Match(Path.GetFileNameWithoutExtension(fileName));
        if (!match.Success)
            return null;

        if (!DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var shotAt))
            return null;

        return new RecordingInfo(
            match.Groups["model"].Value,
            match.Groups["device"].Value,
            match.Groups["person"].Value,
            shotAt,
            int.TryParse(match.Groups["seq"].Value, out var seq) ? seq : 0);
    }

    /// <summary>
    /// Ищет на карте свежую запись и достаёт из её имени номера камеры и
    /// сотрудника. Смотрим самое новое: там номера актуальнее.
    /// </summary>
    public static RecordingInfo? FromCard(string? mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return null;

        var media = Path.Combine(mountPoint, "DCIM");
        var root = Directory.Exists(media) ? media : mountPoint;

        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => (Path: path, Info: Parse(Path.GetFileName(path))))
                .Where(pair => pair.Info is not null)
                .OrderByDescending(pair => pair.Info!.ShotAt)
                .Select(pair => pair.Info)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // Карту вынули посреди обхода, номера возьмутся другим путём.
            return null;
        }
    }
}
