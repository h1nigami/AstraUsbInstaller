using System.Reflection;

namespace AstraUsb.Services;

/// <summary>
/// Версия программы.
///
/// Источников два, и порядок между ними важен. Файл VERSION кладёт релизная
/// сборка: в нём тег и дата публикации, то есть ровно то, чем станция себя
/// считает при обновлении. Если файла нет (сборка из исходников или он
/// потерялся), берётся версия, вшитая в саму программу.
///
/// Спрашивать версию у сервера нельзя: на объекте сети может не быть неделями,
/// а знать, что стоит на станции, нужно всегда.
/// </summary>
public static class VersionInfo
{
    /// <summary>Версия, вшитая в сборку, в виде тега.</summary>
    public static string Build()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "v0.0" : $"v{version.Major}.{version.Minor}";
    }

    /// <summary>Тег установленной версии: из файла релиза или из сборки.</summary>
    public static string Tag() => Parse() is { } release ? release.Tag : Build();

    /// <summary>Подпись для раздела «О программе».</summary>
    public static string Label()
    {
        if (Parse() is { } release)
            return $"версия {release.Tag.TrimStart('v')} от {release.Date:dd.MM.yy}";

        return $"версия {Build().TrimStart('v')}";
    }

    /// <summary>Разбирает файл релиза: «тег дата».</summary>
    private static (string Tag, DateTime Date)? Parse()
    {
        try
        {
            var parts = File.ReadAllText(AppPaths.VersionFile)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 && DateTime.TryParse(parts[1], out var date))
                return (parts[0], date);
        }
        catch (Exception)
        {
            // Файла нет или он испорчен: версию возьмём из сборки.
        }

        return null;
    }
}
