namespace AstraUsb.Services;

/// <summary>
/// Присмотр за томом архива.
///
/// Съёмный диск может оказаться не смонтированным, и тогда запись по его
/// прежнему пути создаёт пустой каталог на системном разделе. Копии как будто
/// сохраняются, место на системном диске кончается, а настоящий архив
/// остаётся без записей. Чтобы этого не случилось, папка архива помечается
/// служебным файлом при выборе, и без метки станция писать отказывается.
/// </summary>
public static class ArchiveGuard
{
    /// <summary>Ставит метку тома. False, если записать не удалось.</summary>
    public static bool Mark(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, Markers.Archive),
                "Метка тома архива BestCam. Не удаляйте: без неё станция не пишет записи.\n");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Том на месте: каталог существует и в нём лежит метка.</summary>
    public static bool Available(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            return File.Exists(Path.Combine(root, Markers.Archive));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Архив лежит на том же разделе, что и система. Задание это запрещает:
    /// системный диск станции невелик, и записи его переполнят.
    /// </summary>
    public static bool OnSystemDrive(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var archive = Path.GetPathRoot(Path.GetFullPath(root));
            var system = Path.GetPathRoot(Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } dir
                    ? dir
                    : AppContext.BaseDirectory));

            return !string.IsNullOrEmpty(archive)
                && string.Equals(archive, system, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Носитель, на котором лежит архив. Такой диск никогда не считается
    /// источником: иначе станция начала бы копировать архив сама в себя.
    /// </summary>
    public static bool IsArchiveMedia(string? mountPoint, string? archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || string.IsNullOrWhiteSpace(archiveRoot))
            return false;

        try
        {
            var mount = Path.GetFullPath(mountPoint).TrimEnd(Path.DirectorySeparatorChar);
            var archive = Path.GetFullPath(archiveRoot).TrimEnd(Path.DirectorySeparatorChar);

            return archive.Equals(mount, StringComparison.OrdinalIgnoreCase)
                || archive.StartsWith(mount + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
