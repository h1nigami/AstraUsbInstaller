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
        public Action<BackupProgress>? OnReport { get; init; }
        public void Report(BackupProgress value)
        {
            Updates.Add(value);
            OnReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task A_corrupt_database_is_logged_without_exposing_SQLite_details_in_progress()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "запись");
        var log = Directory.CreateDirectory(Path.Combine(source, "LOG")).FullName;
        File.WriteAllText(Path.Combine(log, "20260915.txt"), "#ID:1\n");
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
    public async Task Replaced_card_is_not_copied_or_cleaned_under_previous_id()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "replacement", "DCIM")).FullName;
        var mount = Path.GetDirectoryName(source)!;
        var oldRecording = Path.Combine(source,
            "A11_1234567_222222_20260915120000_0001.mp4");
        File.WriteAllText(oldRecording, "old card");
        var db = Path.Combine(_root, "devices.db");
        using (var registry = new DeviceRegistry(db))
            Assert.Equal(1234567, registry.ResolveByCard(mount, 1, "CAM", "sdb1"));
        File.Delete(oldRecording);
        var newRecording = Path.Combine(source,
            "A11_7654321_222222_20260915120000_0001.mp4");
        File.WriteAllText(newRecording, "new card");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"),
            MinFreeGb = 0,
            DeleteVideoAfterCopy = true,
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var progress = new CapturedProgress();

        await new BackupService(db, settings).RunAsync(1234567, mount, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.True(File.Exists(newRecording));
        Assert.False(Directory.Exists(Path.Combine(settings.BackupRoot, "Device1234567")));
    }

    [Fact]
    public async Task Replacement_during_scan_aborts_before_copy_and_cleanup()
    {
        var dcim = Directory.CreateDirectory(Path.Combine(_root, "scan-replacement", "DCIM")).FullName;
        var mount = Path.GetDirectoryName(dcim)!;
        var oldRecording = Path.Combine(dcim,
            "A11_1234567_222222_20260915120000_0001.mp4");
        var newRecording = Path.Combine(dcim,
            "A11_7654321_222222_20260915120000_0001.mp4");
        File.WriteAllText(oldRecording, "old card");
        var db = Path.Combine(_root, "devices.db");
        using (var registry = new DeviceRegistry(db))
            registry.ResolveByCard(mount, 1, "CAM", "sdb1");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"),
            MinFreeGb = 0,
            DeleteVideoAfterCopy = true,
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var progress = new CapturedProgress
        {
            OnReport = update =>
            {
                if (update.Stage != BackupStage.Scanning)
                    return;
                File.Delete(oldRecording);
                File.WriteAllText(newRecording, "new card");
            },
        };

        await new BackupService(db, settings).RunAsync(1234567, mount, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.True(File.Exists(newRecording));
        Assert.False(Directory.Exists(Path.Combine(settings.BackupRoot, "Device1234567")));
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
        var log = Directory.CreateDirectory(Path.Combine(source, "LOG")).FullName;
        File.WriteAllText(Path.Combine(log, "20260915.txt"), "#ID:1\n");

        await service.RunAsync(1, source, new Progress<BackupProgress>());

        var saved = Assert.Single(Directory.GetFiles(service.FolderFor(1), "clip_*.mp4"));
        var entry = Assert.Single(new CollectionLog(db).CollectedBefore(DateTime.Now.AddDays(1)),
            file => Path.GetExtension(file.DestPath) == ".mp4");
        Assert.Equal(saved, entry.DestPath);
        Assert.Contains(new FtpQueue(db).Next(), file => file.Path == saved);
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
