using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AstraUsb.Services;

/// <summary>
/// Обновление станции с релизов GitHub.
///
/// Станция стоит на объекте без обслуживания, поэтому обновляется сама: раз в
/// несколько часов смотрит, что помечено на GitHub как последний релиз, и
/// приводит себя к нему. Репозиторий открытый, ключей на станции нет.
///
/// Запускается это не из киоска, а отдельной службой по таймеру. Причина
/// простая: установка в конце перезапускает службу киоска, и обновление
/// изнутри киоска убивало бы само себя посреди подмены файлов. Отдельная
/// служба живёт в своём cgroup и этот перезапуск переживает, а заодно
/// работает тогда, когда киоск вообще не поднимается, — ради этого случая
/// откат и нужен.
/// </summary>
public static class Updater
{
    private const string Repo = "h1nigami/AstraUsbInstaller";

    /// <summary>Ждать ответа дольше незачем: следующая попытка через шесть часов.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>Сколько дать новой версии подняться, прежде чем судить о ней.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    private const string Service = "astra-usb-avalonia";

    /// <summary>Каталог программы: его и подменяем.</summary>
    private static string AppDir => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Копия прежней версии рядом с каталогом программы.</summary>
    private static string PrevDir => AppDir + ".prev";

    /// <summary>
    /// Тег, на котором обновление уже сломалось. Лежит рядом с каталогом
    /// программы, поэтому переустановка его не стирает: иначе сломанный релиз
    /// ставился бы заново каждые шесть часов.
    /// </summary>
    private static string FailedFile => AppDir + ".failed";

    /// <summary>Версия, установленная сейчас.</summary>
    public static string InstalledTag() => VersionInfo.Tag();

    /// <summary>Записывает файл версии в том же виде, что и релизная сборка.</summary>
    public static void WriteVersion(string tag, DateTime published)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.VersionFile, $"{tag} {published:yyyy-MM-dd}\n", Encoding.UTF8);
    }

    /// <summary>
    /// Надо ли обновляться. Сравнение идёт на неравенство, а не «больше или
    /// меньше»: станция приводится к тому, что помечено последним на GitHub,
    /// поэтому неудачный релиз лечится публикацией другого, а не выездом.
    /// </summary>
    public static bool NeedsUpdate(string installed, string latest) =>
        Release.IsStationTag(latest) && !string.Equals(installed, latest, StringComparison.Ordinal);

    /// <summary>Запоминает тег, на котором обновление сорвалось.</summary>
    public static void RememberFailed(string tag)
    {
        try
        {
            File.WriteAllText(FailedFile, tag, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Не записалось: в худшем случае попробуем этот релиз ещё раз.
        }
    }

    /// <summary>Этот тег уже ломался: второй раз его не ставим.</summary>
    public static bool AlreadyFailed(string tag)
    {
        try
        {
            return File.Exists(FailedFile)
                   && File.ReadAllText(FailedFile).Trim() == tag;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Контрольная сумма файла в том же виде, в каком её пишет sha256sum.</summary>
    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Сходится ли сумма скачанного. Файл сумм приходит в виде sha256sum,
    /// то есть «сумма, два пробела, имя файла».
    /// </summary>
    public static bool ChecksumMatches(string path, string expected)
    {
        var wanted = expected.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return wanted is { Length: 64 }
               && string.Equals(wanted, Sha256(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Под какую платформу станции нужен архив.</summary>
    public static string Platform()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";

        return OperatingSystem.IsMacOS() ? $"osx-{arch}" : $"linux-{arch}";
    }

    /// <summary>
    /// Проверяет и, если нужно, ставит новую версию. Возвращает код выхода
    /// для службы: ноль означает «сделано или делать нечего».
    /// </summary>
    public static int Run()
    {
        // Обновляются только станции. На Windows и macOS программу ставят и
        // обновляют руками: службы и установщика там нет.
        if (!OperatingSystem.IsLinux())
        {
            Say("обновление устроено только для станций на Linux");
            return 0;
        }

        var installed = InstalledTag();
        var answer = Ask(Source());
        if (answer is null)
            return 0;

        var release = Release.Select(answer, Platform());
        if (release is null)
        {
            Say("в ответе нет готового релиза C# для этой платформы");
            return 0;
        }

        if (!NeedsUpdate(installed, release.Tag))
        {
            Say($"версия свежая: {release.Tag}");
            return 0;
        }

        if (AlreadyFailed(release.Tag))
        {
            Say($"релиз {release.Tag} уже не встал, второй раз не пробуем");
            return 0;
        }

        if (BusyMarker.Busy())
        {
            Say("станция сейчас собирает записи, обновимся в следующий раз");
            return 0;
        }

        var asset = release.Pick(Platform());
        if (asset is null)
        {
            Say($"в релизе {release.Tag} нет архива для {Platform()}");
            return 0;
        }

        var work = Directory.CreateTempSubdirectory("astra-update-").FullName;

        try
        {
            return Install(release, asset, work);
        }
        catch (Exception e)
        {
            Say($"обновление сорвалось: {e.Message}");
            RememberFailed(release.Tag);
            return 1;
        }
        finally
        {
            Wipe(work);
        }
    }

    private static int Install(Release release, ReleaseAsset asset, string work)
    {
        var archive = Path.Combine(work, "release.tar.gz");
        if (!Download(asset.Archive, archive))
            return 0;

        var sums = Path.Combine(work, "release.sha256");
        if (!Download(asset.Checksum, sums))
            return 0;

        if (!ChecksumMatches(archive, File.ReadAllText(sums)))
        {
            Say("сумма скачанного не сошлась, ничего не трогаем");
            return 0;
        }

        var unpacked = Path.Combine(work, "unpacked");
        Directory.CreateDirectory(unpacked);
        Unpack(archive, unpacked);

        var root = Root(unpacked);
        var installer = Path.Combine(root, "install_native.sh");
        if (!File.Exists(installer))
        {
            Say("в архиве нет установщика");
            RememberFailed(release.Tag);
            return 1;
        }

        // Новую программу пробуем запустить до того, как трогать рабочую:
        // сборка, которая не стартует, дальше не пойдёт.
        if (!Starts(Path.Combine(root, "AstraUsb")))
        {
            Say("новая сборка не запускается, оставляем прежнюю");
            RememberFailed(release.Tag);
            return 1;
        }

        if (BusyMarker.Busy())
        {
            Say("во время загрузки начался сбор записей, обновимся в следующий раз");
            return 0;
        }

        Snapshot();
        Say($"ставим {release.Tag}");
        try
        {
            if (!Shell("/bin/systemctl", "reset-failed", null, Service))
                throw new IOException("не удалось сбросить счётчик перезапусков службы");
            if (!Shell("/bin/sh", installer, root))
                throw new IOException("установщик завершился с ошибкой");
            if (InstalledTag() != release.Tag)
                throw new IOException("версия после установки не совпала с тегом релиза");
            if (!Healthy())
                throw new IOException("новая служба работает со сбоями");
        }
        catch (Exception e)
        {
            Say(e.Message);
            // Сначала сохраняем сбойный тег: даже неудачный откат не должен потерять его.
            RememberFailed(release.Tag);
            Rollback();
            return 1;
        }

        Say($"обновились до {release.Tag}");
        return 0;
    }

    /// <summary>
    /// Откуда станция узнаёт о релизах. По умолчанию GitHub, но адрес можно
    /// заменить переменной окружения: на закрытом контуре вместо GitHub
    /// ставят зеркало, и переустанавливать из-за этого программу незачем.
    /// </summary>
    private static string Source() =>
        Environment.GetEnvironmentVariable("ASTRA_UPDATE_API") is { Length: > 0 } mirror
            ? mirror
            // ponytail: просматриваем 100 последних релизов; при большем числе нужен отдельный feed.
            : $"https://api.github.com/repos/{Repo}/releases?per_page=100";

    /// <summary>Ответ GitHub или null при любой сетевой беде.</summary>
    private static string? Ask(string url)
    {
        try
        {
            using var client = Client();
            return client.GetStringAsync(url).WaitAsync(Wait).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Сети нет или сервер не ответил: холостой заход, ничего страшного.
            Say($"GitHub не ответил: {e.Message}");
            return null;
        }
    }

    private static bool Download(string url, string target, TimeSpan? timeout = null)
    {
        try
        {
            using var client = Client();
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
            using var source = client.GetStreamAsync(url, deadline.Token).WaitAsync(Wait, deadline.Token)
                .GetAwaiter().GetResult();
            using var file = File.Create(target);
            source.CopyToAsync(file, deadline.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            Say($"не скачалось: {e.Message}");
            return false;
        }
    }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub отказывает запросам без подписи клиента.
        client.DefaultRequestHeaders.Add("User-Agent", "BestCam-Station");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    private static void Unpack(string archive, string target)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
    }

    /// <summary>
    /// Где в распакованном лежит программа: архив бывает и с одной папкой
    /// внутри, и без неё.
    /// </summary>
    private static string Root(string unpacked)
    {
        if (File.Exists(Path.Combine(unpacked, "install_native.sh")))
            return unpacked;

        var inner = Directory.GetDirectories(unpacked);
        return inner.Length == 1 ? inner[0] : unpacked;
    }

    /// <summary>Запускается ли новая сборка вообще.</summary>
    private static bool Starts(string binary)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !File.Exists(binary))
                return false;

            File.SetUnixFileMode(binary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var info = new ProcessStartInfo(binary);
            info.ArgumentList.Add("--version");

            return Execute(info, TimeSpan.FromMinutes(1)).Code == 0;
        }
        catch (Exception e)
        {
            Say($"новая сборка не проверилась: {e.Message}");
            return false;
        }
    }

    /// <summary>Снимает копию рабочего каталога, чтобы было куда вернуться.</summary>
    private static void Snapshot()
    {
        Wipe(PrevDir);
        Copy(AppDir, PrevDir, skipData: true);
    }

    /// <summary>Возвращает прежнюю версию на место.</summary>
    private static void Rollback()
    {
        try
        {
            if (!Directory.Exists(PrevDir))
            {
                Say("откатываться некуда: копии прежней версии нет");
                return;
            }

            foreach (var entry in Directory.GetFileSystemEntries(AppDir))
            {
                var name = Path.GetFileName(entry);
                if (name is "data" or "USB_Backups")
                    continue;

                Wipe(entry);
            }

            Copy(PrevDir, AppDir, skipData: true);
            if (!Shell("/bin/systemctl", "restart", null, Service))
                throw new IOException("файлы восстановлены, но служба не перезапустилась");
            Say("вернулись на прежнюю версию");
        }
        catch (Exception e)
        {
            Say($"откат не удался: {e.Message}");
        }
    }

    /// <summary>Жива ли служба киоска после установки.</summary>
    private static bool Healthy()
    {
        Thread.Sleep(Settle);

        var state = Output("/bin/systemctl", "is-active", Service).Trim();
        if (state != "active")
        {
            Say($"служба после установки в состоянии «{state}»");
            return false;
        }

        var restarts = Output("/bin/systemctl", "show", Service, "-p", "NRestarts", "--value").Trim();
        if (!int.TryParse(restarts, out var count) || count != 0)
        {
            Say($"служба перезапускалась после установки: {restarts}");
            return false;
        }
        return true;
    }

    private static void Copy(string from, string to, bool skipData)
    {
        Directory.CreateDirectory(to);

        foreach (var entry in Directory.GetFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);

            // База станции и собранные записи не копируются: они и так
            // остаются на месте, а весят несоизмеримо больше программы.
            if (skipData && name is "data" or "USB_Backups")
                continue;

            var target = Path.Combine(to, name);

            if (Directory.Exists(entry))
                Copy(entry, target, skipData: false);
            else
                File.Copy(entry, target, overwrite: true);
        }
    }

    private static void Wipe(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Не удалилось: следующая попытка перезапишет.
        }
    }

    private static bool Shell(string command, string first, string? workingDir,
        string? second = null)
    {
        try
        {
            var info = new ProcessStartInfo(command);

            info.ArgumentList.Add(first);
            if (second is not null)
                info.ArgumentList.Add(second);

            if (workingDir is not null)
                info.WorkingDirectory = workingDir;

            var result = Execute(info, TimeSpan.FromMinutes(15));
            if (result.Code != 0)
                Say($"{command} вернул {result.Code}: {Last(result.Error + result.Output)}");

            return result.Code == 0;
        }
        catch (Exception e)
        {
            Say($"{command} не запустился: {e.Message}");
            return false;
        }
    }

    private static string Output(string command, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(command);
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            var result = Execute(info, TimeSpan.FromSeconds(30));
            return result.Code == 0 ? result.Output : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static (int Code, string Output, string Error) Execute(ProcessStartInfo info, TimeSpan timeout)
    {
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var deadline = new CancellationTokenSource(timeout);
        using var proc = Process.Start(info) ?? throw new IOException("процесс не запустился");
        try
        {
            var output = proc.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = proc.StandardError.ReadToEndAsync(deadline.Token);
            Task.WhenAll(output, error, proc.WaitForExitAsync(deadline.Token))
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
            return (proc.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5_000);
            }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private static string Last(string text) => text
        .Split('\n')
        .LastOrDefault(line => line.Trim().Length > 0)?
        .Trim() ?? "";

    /// <summary>Пишет в журнал службы и в файл рядом с базой станции.</summary>
    private static void Say(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} обновление: {message}";
        Console.WriteLine(line);

        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "update.log"),
                line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // Журнал не пишется: остаётся вывод службы.
        }
    }
}
