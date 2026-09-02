namespace AstraUsb.Services;

/// <summary>
/// Служебные файлы, которые станция кладёт на носители.
///
/// Собраны в одном месте, потому что каждый из них должен быть исключён из
/// сканирования и копирования: иначе метка уезжает в архив вместе с записями
/// и попадает в поиск как файл сотрудника.
/// </summary>
public static class Markers
{
    /// <summary>Номер камеры на её карте.</summary>
    public const string CardId = CardIdentity.FileName;

    /// <summary>Номер носителя, который ставила Python-версия.</summary>
    public const string LegacyId = DeviceRegistry.DeviceIdFile;

    /// <summary>
    /// Метка тома архива. По ней видно, что диск смонтирован: без неё запись
    /// ушла бы в пустой каталог на системном разделе, и станция считала бы,
    /// что всё сохранено.
    /// </summary>
    public const string Archive = ".bestcam_archive";

    /// <summary>Служебный ли это файл станции.</summary>
    public static bool IsService(string? fileName) =>
        fileName is CardId or LegacyId or Archive;
}
