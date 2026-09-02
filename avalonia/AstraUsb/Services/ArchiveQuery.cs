namespace AstraUsb.Services;

/// <summary>Род файла, как его различает оператор.</summary>
public enum MediaKind
{
    Any,
    Video,
    Audio,
    Photo,
    Log,
}

/// <summary>
/// Что оператор ищет в архиве.
///
/// Пустое поле означает «любой»: заполняют обычно одно или два, а остальные
/// оставляют как есть. Периоды раздельные: время съёмки ставит регистратор и
/// оно может быть сбито, время сбора ставит станция и потому достоверно.
/// </summary>
public sealed record ArchiveFilter
{
    public DateTime? CollectedFrom { get; init; }
    public DateTime? CollectedTo { get; init; }
    public DateTime? ShotFrom { get; init; }
    public DateTime? ShotTo { get; init; }

    /// <summary>Отдел вместе с подчинёнными.</summary>
    public long? DepartmentId { get; init; }

    public long? DeviceId { get; init; }
    public string PersonnelNo { get; init; } = "";
    public string EmployeeName { get; init; } = "";
    public string FileName { get; init; } = "";
    public MediaKind Kind { get; init; } = MediaKind.Any;

    /// <summary>Только защищённые от уборки записи.</summary>
    public bool ProtectedOnly { get; init; }
}

/// <summary>Найденная запись вместе со сведениями о владельце.</summary>
public sealed record ArchiveRow(
    CollectedFile File,
    string CameraName,
    string EmployeeName,
    string PersonnelNo,
    string Department)
{
    public MediaKind Kind => MediaKinds.Of(File.DestPath);
}

/// <summary>Род файла по его расширению.</summary>
public static class MediaKinds
{
    private static readonly string[] Video = [".mp4", ".mov", ".avi", ".mkv", ".mpg", ".mpeg", ".wmv"];
    private static readonly string[] Audio = [".wav", ".mp3", ".wma", ".aac", ".m4a", ".ogg"];
    private static readonly string[] Photo = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"];
    private static readonly string[] Log = [".txt", ".log", ".csv", ".dat"];

    public static MediaKind Of(string? path)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();

        if (Video.Contains(extension)) return MediaKind.Video;
        if (Audio.Contains(extension)) return MediaKind.Audio;
        if (Photo.Contains(extension)) return MediaKind.Photo;
        if (Log.Contains(extension)) return MediaKind.Log;

        // Незнакомое расширение относим к журналам: это служебные выгрузки
        // регистратора, а не запись, и путать их с видео нельзя.
        return MediaKind.Log;
    }

    public static string Name(MediaKind kind) => kind switch
    {
        MediaKind.Video => "Видео",
        MediaKind.Audio => "Аудио",
        MediaKind.Photo => "Фото",
        MediaKind.Log => "Журнал",
        _ => "Все",
    };
}
