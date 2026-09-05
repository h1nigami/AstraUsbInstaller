using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-backup-").FullName;

    [Fact]
    public async Task Changed_recording_is_logged_and_queued_under_its_actual_archive_name()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"),
            MinFreeGb = 0,
            FtpEnabled = true,
            DeleteVideoAfterCopy = true,
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var db = Path.Combine(_root, "devices.db");
        var service = new BackupService(db, settings);
        Directory.CreateDirectory(service.FolderFor(1));
        var original = Path.Combine(service.FolderFor(1), "clip.mp4");
        File.WriteAllText(original, "старое");
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "новое видео другого размера");

        await service.RunAsync(1, source, new Progress<BackupProgress>());

        var saved = Assert.Single(Directory.GetFiles(service.FolderFor(1), "clip_*.mp4"));
        var entry = Assert.Single(new CollectionLog(db).CollectedBefore(DateTime.Now.AddDays(1)));
        Assert.Equal(saved, entry.DestPath);
        Assert.Equal(saved, Assert.Single(new FtpQueue(db).Next()).Path);
        Assert.False(File.Exists(recording));
        Assert.Equal("старое", File.ReadAllText(original));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
