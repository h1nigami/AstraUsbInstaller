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

    /// <summary>Читает место на доступном томе архива.</summary>
    public static StorageState Read(string backupRoot)
    {
        try
        {
            using var archive = ArchiveGuard.Open(backupRoot);
            // На Linux DriveInfo читает statvfs переданного пути, включая точку монтирования внутри /mnt.
            var drive = new DriveInfo(archive.Path);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return Unknown(backupRoot);

            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var label = OperatingSystem.IsLinux() || string.IsNullOrEmpty(drive.VolumeLabel)
                ? archive.Root : drive.VolumeLabel;
            archive.Verify();

            return new StorageState(total, free, label, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or ArgumentException or System.Security.SecurityException)
        {
            return Unknown(backupRoot);
        }
    }
}
