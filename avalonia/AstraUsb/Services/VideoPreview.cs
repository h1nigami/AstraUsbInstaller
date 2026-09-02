using System.Diagnostics;
using System.Globalization;

namespace AstraUsb.Services;

/// <summary>
/// Просмотр видео на станции.
///
/// Своего проигрывателя в сборке нет: кодеки и вывод видео это десятки
/// мегабайт и отдельная возня с каждым форматом регистратора. Но смотреть
/// запись на станции нужно, поэтому она показывается кадрами: оператор ведёт
/// ползунок по времени, станция достаёт кадр этой секунды через ffmpeg.
///
/// Чего это не даёт: звука и движения. Кому нужно и то и другое, отдаёт
/// запись системному проигрывателю кнопкой рядом.
/// </summary>
public static class VideoPreview
{
    /// <summary>Куда пишется кадр: один файл, перезаписывается на каждом шаге.</summary>
    private static string FramePath => Path.Combine(AppPaths.DataDir, "frame.jpg");

    /// <summary>Разбирает длительность из вывода ffprobe.</summary>
    public static TimeSpan? ParseDuration(string output)
    {
        var text = output.Trim();

        // Точка это разделитель ffprobe, а не системы: на станции запятая, и
        // разбор по её настройкам дал бы длительность в тысячу раз больше.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var seconds))
            return null;

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Подпись времени: часы появляются только когда они есть.</summary>
    public static string Label(TimeSpan at) => at.TotalHours >= 1
        ? $"{(int)at.TotalHours}:{at.Minutes:00}:{at.Seconds:00}"
        : $"{at.Minutes:00}:{at.Seconds:00}";

    /// <summary>Длительность записи или null, если её не удалось узнать.</summary>
    public static TimeSpan? Duration(string path)
    {
        if (!File.Exists(path))
            return null;

        var info = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration",
                     "-of", "default=noprint_wrappers=1:nokey=1",
                     path,
                 })
            info.ArgumentList.Add(argument);

        return Run(info, 10_000, out var output) ? ParseDuration(output) : null;
    }

    /// <summary>
    /// Достаёт кадр указанной секунды. Возвращает путь к нему или null, если
    /// ffmpeg в системе нет или кадр не вышел.
    /// </summary>
    public static string? Frame(string path, TimeSpan at)
    {
        if (!File.Exists(path))
            return null;

        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Directory.CreateDirectory(AppPaths.DataDir);

        // -ss перед -i: так ffmpeg прыгает к нужному месту, а не читает файл
        // с начала. На часовой записи это разница между мгновением и минутой.
        // -nostdin: без него ffmpeg ждёт ввода и подвешивает станцию.
        foreach (var argument in new[]
                 {
                     "-nostdin", "-y",
                     "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", path,
                     "-frames:v", "1",
                     "-q:v", "3",
                     FramePath,
                 })
            info.ArgumentList.Add(argument);

        if (!Run(info, 20_000, out _))
            return null;

        return File.Exists(FramePath) ? FramePath : null;
    }

    /// <summary>Есть ли в системе ffprobe: без него шкалу строить не из чего.</summary>
    public static bool Available()
    {
        var info = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-version");

        return Run(info, 5_000, out _);
    }

    /// <summary>Запускает средство и отдаёт его вывод. Отказ это не исключение.</summary>
    private static bool Run(ProcessStartInfo info, int timeoutMs, out string output)
    {
        output = "";

        try
        {
            using var proc = Process.Start(info);
            if (proc is null)
                return false;

            output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            // Средства может не быть в системе: тогда просмотр по кадрам
            // просто недоступен, а станция работает дальше.
            return false;
        }
    }
}
