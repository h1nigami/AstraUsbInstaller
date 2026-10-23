using System.Text;
using Avalonia.Platform;

namespace AstraUsb.Services;

/// <summary>
/// Страница веб-панели.
///
/// Разметка, стили и скрипт лежат в Assets/web как отдельные файлы и уходят
/// в сборку ресурсами: сборщик не нужен, а править страницу можно как страницу,
/// а не как строку в коде. Страница одна и для телефона, и для компьютера.
/// </summary>
public static class WebPage
{
    public static string Html => Text("index.html");
    public static string Css => Text("app.css");
    /// <summary>Модуль страницы по имени файла; null, если такого нет.</summary>
    public static string? Script(string name) =>
        name.EndsWith(".js", StringComparison.Ordinal) && name.IndexOfAny(['/', '\\']) < 0
            ? Read($"avares://AstraUsb/Assets/web/js/{name}") is { } bytes ? Encoding.UTF8.GetString(bytes) : null
            : null;

    /// <summary>Шрифт из ресурсов киоска; null, если такого нет.</summary>
    public static byte[]? Font(string name) =>
        name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(['/', '\\']) < 0
            ? Read($"avares://AstraUsb/Assets/Fonts/{name}")
            : null;

    private static string Text(string name) =>
        Read($"avares://AstraUsb/Assets/web/{name}") is { } bytes ? Encoding.UTF8.GetString(bytes) : "";

    private static byte[]? Read(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
