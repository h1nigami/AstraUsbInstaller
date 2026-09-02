namespace AstraUsb.Services;

/// <summary>
/// Что известно о томе архива: сколько всего, сколько свободно и как он
/// подписан.
///
/// Читается это в стороне от интерфейса: обращение к диску иногда занимает
/// заметное время, а на доске от него зависит только полоса внизу.
/// </summary>
public sealed record StorageState(long Total, long Free, string Label, bool Available)
{
    public static StorageState Unknown(string root) => new(0, 0, root, false);

    /// <summary>Опрашивает том архива. Ошибка это не беда: полоса просто не меняется.</summary>
    public static StorageState Read(string backupRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(backupRoot));
            if (string.IsNullOrEmpty(root))
                return Unknown(backupRoot);

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return Unknown(backupRoot);

            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? root : drive.VolumeLabel;

            return new StorageState(drive.TotalSize, drive.AvailableFreeSpace, label,
                ArchiveGuard.Available(backupRoot));
        }
        catch (Exception)
        {
            return Unknown(backupRoot);
        }
    }
}
