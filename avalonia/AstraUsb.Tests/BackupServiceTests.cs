using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-backup-").FullName;
    private readonly string _appRoot = AppPaths.Root;

    public BackupServiceTests() => AppPaths.Root = _root;

    private sealed class CapturedProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Updates { get; } = [];
        public void Report(BackupProgress value) => Updates.Add(value);
    }

    [Fact]
    public async Task A_corrupt_database_is_logged_without_exposing_SQLite_details_in_progress()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "запись");
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0 };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var db = Path.Combine(_root, "devices.db");
        File.WriteAllText(db, "сломанная база");
        var progress = new CapturedProgress();

        await new BackupService(db, settings).RunAsync(1, source, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.DoesNotContain(progress.Updates, update => update.Detail.Contains("SQLite"));
        Assert.Contains("Не удалось", progress.Updates.Last().Detail);
        Assert.Contains("SqliteException", File.ReadAllText(CrashLog.FilePath));
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task A_null_archive_root_reports_failure_instead_of_throwing_before_the_guard()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var progress = new CapturedProgress();
        var service = new BackupService(Path.Combine(_root, "devices.db"),
            new Settings { BackupRoot = null! });

        await service.RunAsync(1, source, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
    }

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
        AppPaths.Root = _appRoot;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
