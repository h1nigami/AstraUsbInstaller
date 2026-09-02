using System.Text;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Обновление станции. Проверяется то, что не требует сети: разбор ответа
/// GitHub, выбор своего архива, решение «обновляться или нет», занятость
/// станции и сверка контрольной суммы.
/// </summary>
[Collection("Каталог данных")]
public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-update-").FullName;
    private readonly string _root;

    private const string Answer = """
        {
          "tag_name": "v2.0",
          "prerelease": false,
          "published_at": "2026-09-02T10:00:00Z",
          "assets": [
            {"name": "bestcam-station-v2.0-linux-x64.tar.gz",
             "browser_download_url": "https://example/linux-x64.tar.gz"},
            {"name": "bestcam-station-v2.0-linux-x64.tar.gz.sha256",
             "browser_download_url": "https://example/linux-x64.sha256"},
            {"name": "bestcam-station-v2.0-linux-arm64.tar.gz",
             "browser_download_url": "https://example/linux-arm64.tar.gz"},
            {"name": "bestcam-station-v2.0-linux-arm64.tar.gz.sha256",
             "browser_download_url": "https://example/linux-arm64.sha256"},
            {"name": "bestcam-station-v2.0-win-x64.zip",
             "browser_download_url": "https://example/win-x64.zip"}
          ]
        }
        """;

    public UpdateTests()
    {
        _root = AppPaths.Root;
        AppPaths.Root = _dir;
    }

    [Fact]
    public void The_answer_gives_the_tag_and_the_date()
    {
        var release = Release.Parse(Answer);

        Assert.NotNull(release);
        Assert.Equal("v2.0", release!.Tag);
        Assert.Equal(new DateTime(2026, 9, 2), release.Published.Date);
    }

    [Fact]
    public void The_station_takes_the_archive_of_its_own_platform()
    {
        var release = Release.Parse(Answer)!;

        var linux = release.Pick("linux-x64");
        Assert.NotNull(linux);
        Assert.Equal("https://example/linux-x64.tar.gz", linux!.Archive);
        Assert.Equal("https://example/linux-x64.sha256", linux.Checksum);

        var arm = release.Pick("linux-arm64");
        Assert.Equal("https://example/linux-arm64.tar.gz", arm!.Archive);
    }

    [Fact]
    public void An_archive_without_a_checksum_is_not_taken()
    {
        // Сумма это единственная защита от битой закачки, без неё ставить
        // нельзя: половина архива хуже старой версии.
        var release = Release.Parse(Answer)!;

        Assert.Null(release.Pick("win-x64"));
    }

    [Fact]
    public void A_platform_without_an_archive_gives_nothing()
    {
        Assert.Null(Release.Parse(Answer)!.Pick("osx-arm64"));
    }

    [Fact]
    public void The_python_release_archive_is_not_taken()
    {
        // Релизы Python-версии лежат в том же репозитории, и станция не
        // должна принимать их за своё обновление.
        var python = """
            {
              "tag_name": "v1.4",
              "assets": [
                {"name": "astra-usb-monitor-v1.4.tar.gz",
                 "browser_download_url": "https://example/python.tar.gz"},
                {"name": "astra-usb-monitor-v1.4.tar.gz.sha256",
                 "browser_download_url": "https://example/python.sha256"}
              ]
            }
            """;

        var release = Release.Parse(python);

        Assert.NotNull(release);
        Assert.Null(release!.Pick("linux-x64"));
        Assert.Null(release.Pick("linux-arm64"));
    }

    [Fact]
    public void A_broken_answer_does_not_throw()
    {
        Assert.Null(Release.Parse("это не ответ сервера"));
        Assert.Null(Release.Parse("{}"));
    }

    [Theory]
    [InlineData("v1.9", "v2.0", true)]
    [InlineData("v2.0", "v2.0", false)]
    [InlineData("", "v2.0", true)]
    // Сравнение идёт на неравенство: станция приводится к тому, что помечено
    // на GitHub как последнее, поэтому откат релиза лечится публикацией.
    [InlineData("v2.1", "v2.0", true)]
    public void The_station_follows_what_github_calls_latest(
        string installed, string latest, bool expected)
    {
        Assert.Equal(expected, Updater.NeedsUpdate(installed, latest));
    }

    [Fact]
    public void A_tag_that_already_failed_is_skipped()
    {
        Updater.RememberFailed("v2.0");

        Assert.True(Updater.AlreadyFailed("v2.0"));
        Assert.False(Updater.AlreadyFailed("v2.1"));
    }

    [Fact]
    public void The_station_is_busy_while_the_marker_is_fresh()
    {
        BusyMarker.Touch();

        Assert.True(BusyMarker.Busy());
    }

    [Fact]
    public void A_stale_marker_means_the_station_is_free()
    {
        BusyMarker.Touch();
        File.SetLastWriteTimeUtc(BusyMarker.FilePath, DateTime.UtcNow.AddMinutes(-5));

        // Иначе упавший киоск запретил бы обновления навсегда.
        Assert.False(BusyMarker.Busy());
    }

    [Fact]
    public void No_marker_means_the_station_is_free()
    {
        Assert.False(BusyMarker.Busy());
    }

    [Fact]
    public void A_download_is_taken_only_with_a_matching_checksum()
    {
        var file = Path.Combine(_dir, "archive.tar.gz");
        File.WriteAllText(file, "содержимое архива");

        var sum = Updater.Sha256(file);

        Assert.True(Updater.ChecksumMatches(file, sum));
        Assert.True(Updater.ChecksumMatches(file, sum.ToUpperInvariant()));
        // Сервер отдаёт строку вида «<сумма>  <имя файла>».
        Assert.True(Updater.ChecksumMatches(file, $"{sum}  archive.tar.gz\n"));
        Assert.False(Updater.ChecksumMatches(file, new string('0', 64)));
        Assert.False(Updater.ChecksumMatches(file, ""));
    }

    [Fact]
    public void The_version_file_is_written_the_way_the_release_writes_it()
    {
        Updater.WriteVersion("v2.0", new DateTime(2026, 9, 2));

        Assert.Equal("v2.0 2026-09-02",
            File.ReadAllText(AppPaths.VersionFile, Encoding.UTF8).Trim());
    }

    [Fact]
    public void The_installed_tag_comes_from_the_version_file()
    {
        Updater.WriteVersion("v2.0", new DateTime(2026, 9, 2));

        Assert.Equal("v2.0", Updater.InstalledTag());
    }

    [Fact]
    public void Without_a_version_file_the_tag_comes_from_the_build()
    {
        // Пустой тег означал бы «версия неизвестна», и станция считала бы себя
        // устаревшей при каждой проверке.
        Assert.Equal(VersionInfo.Build(), Updater.InstalledTag());
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
