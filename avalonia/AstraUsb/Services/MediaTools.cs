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
    public static MediaResult Open(string path, string? archiveRoot = null, string? dbPath = null)
    {
        try
        {
            using var operation = OperationGuard.Acquire(dbPath: dbPath);
            var settings = archiveRoot is null ? Settings.Load() : null;
            using var archive = ArchiveGuard.Open(archiveRoot ?? settings!.ResolveBackupRoot(), settings: settings);
            path = (settings ?? Settings.Load()).ArchivePathResolver(archive.Root)(path);
            if (!File.Exists(path))
                return new MediaResult(false, "файла больше нет в архиве");
            using var input = archive.OpenRead(Path.GetRelativePath(archive.Root, path));
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return new MediaResult(true, "запись открыта");
            }

            // На Astra Linux открытие идёт через xdg-open: он спрашивает у
            // рабочего стола, чем открывать такой файл.
            using var proc = Process.Start(new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                ArgumentList = { path },
            });

            if (proc is null)
                return new MediaResult(false, "не удалось запустить просмотр");

            return new MediaResult(true, "запись открыта");
        }
        catch (StationBusyException)
        {
            return new MediaResult(false, "Станция занята, повторите просмотр после завершения обслуживания");
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

            var output = proc.StandardOutput.ReadToEndAsync();
            var error = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                proc.Kill(entireProcessTree: true);
                return false;
            }
            output.GetAwaiter().GetResult();
            error.GetAwaiter().GetResult();
            return proc.ExitCode == 0;
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
    public static MediaResult Convert(string path, string format, string? archiveRoot = null,
        string? dbPath = null)
    {
        var wanted = (format ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (wanted.Length == 0)
            return new MediaResult(false, "не выбран формат");

        if (!FormatsFor(MediaKinds.Of(path)).Contains(wanted))
            return new MediaResult(false, $"в этот формат такую запись не переводят: {wanted}");

        try
        {
            using var operation = OperationGuard.Acquire(dbPath: dbPath);
            var settings = archiveRoot is null ? Settings.Load() : null;
            using var archive = ArchiveGuard.Open(archiveRoot ?? settings!.ResolveBackupRoot(), settings: settings);
            path = (settings ?? Settings.Load()).ArchivePathResolver(archive.Root)(path);
            if (!File.Exists(path))
                return new MediaResult(false, "файла больше нет в архиве");
            var target = Path.Combine(Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}_копия.{wanted}");
            if (File.Exists(target))
                return new MediaResult(false, $"копия уже есть: {Path.GetFileName(target)}");
            using var input = archive.OpenRead(Path.GetRelativePath(archive.Root, path));
            var inputPath = OperatingSystem.IsLinux()
                ? $"/proc/{Environment.ProcessId}/fd/{input.SafeFileHandle.DangerousGetHandle()}" : path;
            archive.ProduceFile(Path.GetRelativePath(archive.Root, target), outputPath =>
            {
                var info = new ProcessStartInfo("ffmpeg")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                var image = wanted is "jpg" or "png" or "bmp";
                var muxer = image ? "image2" : wanted == "wma" ? "asf" : wanted;
                foreach (var argument in new[] { "-nostdin", "-y", "-i", inputPath, "-f", muxer })
                    info.ArgumentList.Add(argument);
                if (image)
                    foreach (var argument in new[] { "-c:v", wanted == "jpg" ? "mjpeg" : wanted, "-frames:v", "1" })
                        info.ArgumentList.Add(argument);
                info.ArgumentList.Add(outputPath);
                using var proc = Process.Start(info) ?? throw new IOException("ffmpeg не запустился");
                var output = proc.StandardOutput.ReadToEndAsync();
                var error = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(600_000))
                {
                    proc.Kill(entireProcessTree: true);
                    throw new IOException("преобразование затянулось и прервано");
                }
                var errors = error.GetAwaiter().GetResult();
                output.GetAwaiter().GetResult();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException(errors);
            });

            return new MediaResult(true, Path.GetFileName(target));
        }
        catch (StationBusyException)
        {
            return new MediaResult(false, "Станция занята, повторите преобразование после завершения обслуживания");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            CrashLog.Write("Запуск преобразования записи", error);
            return new MediaResult(false,
                "в системе нет ffmpeg: поставьте его, иначе преобразование недоступно");
        }
        catch (Exception e)
        {
            return new MediaResult(false, UserError.Report("Не удалось преобразовать запись", e));
        }
    }
}
