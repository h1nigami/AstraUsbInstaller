using System.Text;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class FtpSenderReviewFixTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("astra-ftp-review-").FullName;
    private readonly string _previous = AppPaths.Root;
    private string Root => Path.Combine(_temp, "archive");

    public FtpSenderReviewFixTests() { AppPaths.Root = _temp; Assert.True(ArchiveGuard.Mark(Root)); }

    [Fact]
    public async Task Equal_names_from_different_cameras_reach_distinct_remote_files()
    {
        await using var server = new FtpReviewServer();
        var settings = Config(server);
        var first = Recording("Device7/DCIM/a.mp4", "первая камера");
        var second = Recording("Device8/DCIM/a.mp4", "вторая камера");

        Assert.True(FtpSender.Send(settings, first).Ok);
        Assert.True(FtpSender.Send(settings, second).Ok);

        Assert.Equal(2, server.Files.Count);
        Assert.Equal("первая камера", Encoding.UTF8.GetString(server.Files["/backup/Device7/DCIM/a.mp4"]));
        Assert.Equal("вторая камера", Encoding.UTF8.GetString(server.Files["/backup/Device8/DCIM/a.mp4"]));
    }

    [Fact]
    public async Task Ftp_paths_preserve_spaces_percent_hash_and_unicode()
    {
        await using var server = new FtpReviewServer();
        var settings = Config(server);
        settings.FtpFolder = "архив 50%/случай#7";
        var path = Recording("Device7/папка 50%/запись #1%.mp4", "полные данные");

        var result = FtpSender.Send(settings, path);

        Assert.True(result.Ok, result.Message);
        var remote = Assert.Single(server.Files);
        Assert.Equal("/архив 50%/случай#7/Device7/папка 50%/запись #1%.mp4", remote.Key);
        Assert.Equal("полные данные", Encoding.UTF8.GetString(remote.Value));
    }

    [Fact]
    public async Task A_refused_upload_remains_unsuccessful()
    {
        await using var server = new FtpReviewServer { RefuseUpload = true };
        var result = FtpSender.Send(Config(server), Recording("Device7/a.mp4", "данные"));
        Assert.False(result.Ok);
        Assert.Empty(server.Files);
    }

    private Settings Config(FtpReviewServer server) => new() { BackupRoot = Root,
        FtpHost = "127.0.0.1", FtpPort = server.Port, FtpFolder = "backup", FtpUser = "test", FtpPassword = "local" };

    private string Recording(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public void Dispose() { AppPaths.Root = _previous; Directory.Delete(_temp, true); }
}

