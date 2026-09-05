using System.Diagnostics;

namespace AstraUsb.Services;

/// <summary>Чем закончилось преобразование или открытие записи.</summary>
public sealed record MediaResult(bool Ok, string Message);

/// <summary>
/// Воспроизведение и преобразование записей.
///
/// Своего проигрывателя у станции нет: собственный плеер потребовал бы
/// затащить в сборку кодеки и библиотеку вывода, а это десятки мегабайт и
/// отдельная возня с каждым форматом регистратора. Поэтому запись открывается
/// тем, чем система открывает такие файлы, а преобразование делает ffmpeg,
/// если он в системе есть.
///
/// Исходный файл при преобразовании остаётся на месте: задание требует именно
/// этого, и оператор не должен рисковать записью, чтобы получить её копию в
/// другом формате.
/// </summary>
public static class MediaTools
{
    /// <summary>Форматы, в которые можно перевести запись данного рода.</summary>
    public static IReadOnlyList<string> FormatsFor(MediaKind kind) => kind switch
    {
        MediaKind.Video => ["mp4", "mov"],
        MediaKind.Audio => ["mp3", "wav", "wma"],
        MediaKind.Photo => ["jpg", "png", "bmp"],
        _ => [],
    };

    /// <summary>Открывает запись тем, чем система открывает такие файлы.</summary>
    public static MediaResult Open(string path)
    {
        if (!File.Exists(path))
            return new MediaResult(false, "файла больше нет в архиве");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return new MediaResult(true, "запись открыта");
            }

            // На Astra Linux открытие идёт через xdg-open: он спрашивает у
            // рабочего стола, чем открывать такой файл.
            var proc = Process.Start(new ProcessStartInfo("xdg-open", path)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            });

            if (proc is null)
                return new MediaResult(false, "не удалось запустить просмотр");

            return new MediaResult(true, "запись открыта");
        }
        catch (Exception e)
        {
            return new MediaResult(false, UserError.Report("Не удалось открыть запись", e));
        }
    }

    /// <summary>Есть ли в системе ffmpeg: без него преобразование недоступно.</summary>
    public static bool ConverterAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (proc is null)
                return false;

            proc.StandardOutput.ReadToEnd();
            return proc.WaitForExit(5000) && proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Переводит запись в другой формат рядом с исходной. Исходный файл не
    /// трогается: он остаётся тем, что собрано с регистратора.
    /// </summary>
    /// <returns>Путь к новому файлу или причину отказа.</returns>
    public static MediaResult Convert(string path, string format)
    {
        if (!File.Exists(path))
            return new MediaResult(false, "файла больше нет в архиве");

        var wanted = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (wanted.Length == 0)
            return new MediaResult(false, "не выбран формат");

        if (!FormatsFor(MediaKinds.Of(path)).Contains(wanted))
            return new MediaResult(false, $"в этот формат такую запись не переводят: {wanted}");

        var target = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            $"{Path.GetFileNameWithoutExtension(path)}_копия.{wanted}");

        if (File.Exists(target))
            return new MediaResult(false, $"копия уже есть: {Path.GetFileName(target)}");

        try
        {
            var info = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // -nostdin: без него ffmpeg ждёт ввода и подвешивает станцию.
            foreach (var argument in new[] { "-nostdin", "-y", "-i", path, target })
                info.ArgumentList.Add(argument);

            using var proc = Process.Start(info);
            if (proc is null)
                return new MediaResult(false, "ffmpeg не запустился");

            proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(600_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
                return new MediaResult(false, "преобразование затянулось и прервано");
            }

            if (proc.ExitCode != 0)
            {
                Cleanup(target);
                return new MediaResult(false, UserError.Report("Не удалось преобразовать запись",
                    new InvalidOperationException(error)));
            }

            return new MediaResult(true, Path.GetFileName(target));
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            CrashLog.Write("Запуск преобразования записи", error);
            return new MediaResult(false,
                "в системе нет ffmpeg: поставьте его, иначе преобразование недоступно");
        }
        catch (Exception e)
        {
            Cleanup(target);
            return new MediaResult(false, UserError.Report("Не удалось преобразовать запись", e));
        }
    }

    /// <summary>Убирает недоделанный файл: половина записи хуже её отсутствия.</summary>
    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
