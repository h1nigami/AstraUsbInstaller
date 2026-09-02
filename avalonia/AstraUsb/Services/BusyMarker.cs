namespace AstraUsb.Services;

/// <summary>
/// Занята ли станция прямо сейчас.
///
/// Обновление не должно начинаться посреди сбора: подмена файлов под
/// работающей загрузкой оставила бы записи наполовину скопированными. Киоск
/// отмечает работу временем этого файла, а обновление смотрит, свежая ли
/// отметка.
///
/// Признак «есть ли подключённые носители» для этого не годится: диск архива
/// тоже сидит на USB и подключён постоянно, а регистратор, забытый в гнезде
/// после загрузки, запретил бы обновления навсегда. Отметка отвечает ровно на
/// нужный вопрос: идёт ли запись прямо сейчас. Если киоск упал и отметку
/// никто не обновляет, станция считается свободной, что и требуется: как раз
/// ради такого случая обновление и нужно.
/// </summary>
public static class BusyMarker
{
    /// <summary>Дольше этого отметку считаем протухшей.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    public static string FilePath => Path.Combine(AppPaths.DataDir, ".copying");

    /// <summary>Отмечает, что станция сейчас работает с записями.</summary>
    public static void Touch()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            if (File.Exists(FilePath))
                File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow);
            else
                File.WriteAllText(FilePath, "");
        }
        catch (Exception)
        {
            // Отметку не поставить: обновление сочтёт станцию свободной.
            // Это лучше, чем упасть на ровном месте.
        }
    }

    /// <summary>Идёт ли сейчас работа с записями.</summary>
    public static bool Busy()
    {
        try
        {
            return File.Exists(FilePath)
                   && DateTime.UtcNow - File.GetLastWriteTimeUtc(FilePath) < Fresh;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
