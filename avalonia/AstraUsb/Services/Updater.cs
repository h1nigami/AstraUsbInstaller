using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

        // Архив с флешки, оставленный киоском, ставится без сети.
        if (TakeOfflineSpool() is { } offline)
            return RunOffline(installed, offline.Tag, offline.Archive);

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

        return InstallArchive(release.Tag, archive, work);
    }

    /// <summary>Ставит проверенный архив: распаковка, пробный запуск, установщик, проверка, откат.</summary>
    private static int InstallArchive(string tag, string archive, string work)
    {
        var unpacked = Path.Combine(work, "unpacked");
        Directory.CreateDirectory(unpacked);
        Unpack(archive, unpacked);

        var root = Root(unpacked);
        var installer = Path.Combine(root, "install_native.sh");
        if (!File.Exists(installer))
        {
            Say("в архиве нет установщика");
            RememberFailed(tag);
            return 1;
        }

        // Новую программу пробуем запустить до того, как трогать рабочую:
        // сборка, которая не стартует, дальше не пойдёт.
        if (!Starts(Path.Combine(root, "AstraUsb")))
        {
            Say("новая сборка не запускается, оставляем прежнюю");
            RememberFailed(tag);
            return 1;
        }

        if (BusyMarker.Busy())
        {
            Say("во время загрузки начался сбор записей, обновимся в следующий раз");
            return 0;
        }

        Snapshot();
        Say($"ставим {tag}");
        try
        {
            if (!Shell("/bin/systemctl", "reset-failed", null, Service))
                throw new IOException("не удалось сбросить счётчик перезапусков службы");
            if (!Shell("/bin/sh", installer, root))
                throw new IOException("установщик завершился с ошибкой");
            if (InstalledTag() != tag)
                throw new IOException("версия после установки не совпала с тегом релиза");
            if (!Healthy())
                throw new IOException("новая служба работает со сбоями");
        }
        catch (Exception e)
        {
            Say(e.Message);
            // Сначала сохраняем сбойный тег: даже неудачный откат не должен потерять его.
            RememberFailed(tag);
            Rollback();
            return 1;
        }

        Say($"обновились до {tag}");
        return 0;
    }

    // --- Обновление с флешки ---------------------------------------------------

    /// <summary>
    /// Спул: сюда киоск складывает архив с флешки, служба обновления
    /// забирает его без сети. Сам киоск не ставит: установщик в конце
    /// перезапускает его службу и убил бы киоск посреди подмены файлов.
    /// </summary>
    public static string OfflineSpoolDir => AppDir + ".offline";

    private static Regex OfflineArchivePattern(string platform) => new(
        $@"\Abestcam-station-(?<tag>.+)-{Regex.Escape(platform)}\.tar\.gz\z");

    /// <summary>
    /// Архивы обновления в корне каталога, сначала новее. Имена строго по
    /// маске релизов C# под эту платформу; архивы Python и чужих платформ
    /// не берутся.
    /// </summary>
    public static IReadOnlyList<(string Tag, string Archive)> FindOfflineArchives(string directory,
        string platform)
    {
        var pattern = OfflineArchivePattern(platform);
        try
        {
            return Directory.EnumerateFiles(directory)
                .Select(path => (Path: path, Match: pattern.Match(Path.GetFileName(path))))
                .Where(item => item.Match.Success && Release.IsStationTag(item.Match.Groups["tag"].Value))
                .Select(item => (Tag: item.Match.Groups["tag"].Value, Archive: item.Path))
                .OrderByDescending(item => VersionKey(item.Tag))
                .ThenByDescending(item => item.Tag, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Числовая часть тега для сравнения: v2.10 новее v2.9.</summary>
    private static Version VersionKey(string tag)
    {
        var numbers = tag[1..].Split('-')[0].Split('.')
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .Concat([0, 0, 0]).Take(4).ToArray();
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>Null, если сумма рядом с архивом сошлась, иначе текст для оператора.</summary>
    public static string? VerifyChecksumFile(string archive, string sums)
    {
        string expected;
        try
        {
            expected = File.ReadAllText(sums);
        }
        catch (Exception)
        {
            return "рядом нет файла контрольной суммы (.sha256)";
        }

        if (expected.Trim().Length == 0)
            return "файл контрольной суммы пуст";

        try
        {
            return ChecksumMatches(archive, expected) ? null : "контрольная сумма не сошлась — архив битый";
        }
        catch (Exception e)
        {
            return $"архив не читается: {e.Message}";
        }
    }

    /// <summary>Копирует архив и сумму с флешки в спул. Возвращает тег или текст ошибки.</summary>
    public static (string? Tag, string? Error) StageOffline(string archive, string platform,
        string? spoolDir = null)
    {
        var match = OfflineArchivePattern(platform).Match(Path.GetFileName(archive));
        if (!match.Success || !Release.IsStationTag(match.Groups["tag"].Value))
            return (null, "это не архив обновления для этой станции");

        var tag = match.Groups["tag"].Value;
        if (VerifyChecksumFile(archive, archive + ".sha256") is { } error)
            return (null, error);

        var spool = spoolDir ?? OfflineSpoolDir;
        try
        {
            Wipe(spool);
            Directory.CreateDirectory(spool);
            File.Copy(archive, Path.Combine(spool, "release.tar.gz"));
            File.Copy(archive + ".sha256", Path.Combine(spool, "release.tar.gz.sha256"));
            // Тег пишется последним: служба берёт спул только целиком.
            File.WriteAllText(Path.Combine(spool, "tag"), tag, Encoding.UTF8);
        }
        catch (Exception e)
        {
            Wipe(spool);
            return (null, $"не удалось подготовить обновление: {e.Message}");
        }

        // Копия с флешки могла побиться по дороге.
        if (VerifyChecksumFile(Path.Combine(spool, "release.tar.gz"),
                Path.Combine(spool, "release.tar.gz.sha256")) is { } copied)
        {
            Wipe(spool);
            return (null, copied);
        }
        return (tag, null);
    }

    /// <summary>Тег и архив из спула или null, если там ничего готового нет.</summary>
    public static (string Tag, string Archive)? TakeOfflineSpool(string? spoolDir = null)
    {
        var spool = spoolDir ?? OfflineSpoolDir;
        try
        {
            var tagFile = Path.Combine(spool, "tag");
            var archive = Path.Combine(spool, "release.tar.gz");
            if (!File.Exists(tagFile) || !File.Exists(archive))
                return null;
            var tag = File.ReadAllText(tagFile).Trim();
            return Release.IsStationTag(tag) ? (tag, archive) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void ClearOfflineSpool(string? spoolDir = null) => Wipe(spoolDir ?? OfflineSpoolDir);

    private static int RunOffline(string installed, string tag, string archive)
    {
        if (VerifyChecksumFile(archive, archive + ".sha256") is { } error)
        {
            Say($"архив с флешки битый ({error}), пропускаем");
            ClearOfflineSpool();
            return 0;
        }

        if (!NeedsUpdate(installed, tag))
        {
            Say($"архив с флешки {tag} уже установлен");
            ClearOfflineSpool();
            return 0;
        }

        if (AlreadyFailed(tag))
        {
            Say($"релиз {tag} с флешки уже не встал, второй раз не пробуем");
            ClearOfflineSpool();
            return 0;
        }

        // Спул остаётся: поставим при следующей проверке.
        if (BusyMarker.Busy())
        {
            Say("станция сейчас собирает записи, обновление с флешки отложено");
            return 0;
        }

        var work = Directory.CreateTempSubdirectory("astra-update-").FullName;
        Say($"ставим {tag} с флешки (было {installed})");
        try
        {
            return InstallArchive(tag, archive, work);
        }
        catch (Exception e)
        {
            Say($"обновление с флешки сорвалось: {e.Message}");
            RememberFailed(tag);
            return 1;
        }
        finally
        {
            Wipe(work);
            ClearOfflineSpool();
        }
    }

    // --- Запуск проверки из киоска --------------------------------------------

    /// <summary>Служба обновления, которую киоск только запускает.</summary>
    public const string UpdateUnit = Service + "-update.service";

    public const string UpdateStarted = "Проверка обновления запущена";
    public const string NoNetwork = "Нет подключения к интернету";

    /// <summary>
    /// Быстрая проверка, что до источника релизов можно достучаться. Сама
    /// служба при отсутствии сети молча выходит с нулём (иначе таймер
    /// ругался бы каждые шесть часов), и без этой проверки кнопка не
    /// отличила бы «сети нет» от «всё в порядке».
    /// </summary>
    public static bool HasNetwork(TimeSpan? timeout = null)
    {
        try
        {
            var uri = new Uri(Source());
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(timeout ?? TimeSpan.FromSeconds(5))
                   && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Запускает службу обновления, не дожидаясь её конца. Возвращает текст
    /// для оператора. Сама установка идёт вне киоска.
    /// </summary>
    public static string StartService(bool checkNetwork = true)
    {
        if (!OperatingSystem.IsLinux())
            return "На этой платформе программа обновляется вручную";
        if (checkNetwork && !HasNetwork())
            return NoNetwork;
        if (!Directory.Exists("/run/systemd/system"))
            return "Проверка недоступна: нет systemd";
        return Shell("/bin/systemctl", "start", null, "--no-block", UpdateUnit)
            ? UpdateStarted
            : "Не удалось запустить проверку обновлений";
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
        string? second = null, string? third = null)
    {
        try
        {
            var info = new ProcessStartInfo(command);

            info.ArgumentList.Add(first);
            if (second is not null)
                info.ArgumentList.Add(second);
            if (third is not null)
                info.ArgumentList.Add(third);

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
