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
    public void Successive_mountpoints_keep_one_logged_key_and_its_protection()
    {
        var settings = new Settings { BackupRoot = Path.Combine(_root, "selected") };
        var db = Path.Combine(_root, "devices.db");
        var source = Path.Combine(_root, "source.mp4");
        File.WriteAllText(source, "recording");
        var service = new BackupService(db, settings);
        var record = typeof(BackupService).GetMethod("RecordCollected",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var key = Path.Combine(settings.BackupRoot, "Device7", "record.mp4");
        var log = new CollectionLog(db);
        log.Record([new CollectedFile(7, key, 9, null, DateTime.Now)]);
        log.SetImportant(key, true);

        foreach (var mount in new[] { "first-mount", "second-mount" })
        {
            var live = Path.Combine(_root, mount);
            var saved = Path.Combine(live, "Device7", "record.mp4");
            var copied = new CopyResult(1, 9, new Dictionary<string, string> { [source] = saved }, 0);
            Assert.Equal(true, record.Invoke(service, [7L, copied, DateTime.Now, settings, live]));
        }

        var file = Assert.Single(log.CollectedBetween(DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(key, file.DestPath);
        Assert.True(file.Important);
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
    public async Task A_source_removed_before_identification_requests_recovery()
    {
        var progress = new CapturedProgress();
        var service = new BackupService(Path.Combine(_root, "devices.db"), new Settings());
        await Assert.ThrowsAsync<DeviceLostException>(() =>
            service.RunAsync(1, Path.Combine(_root, "removed"), progress));
        Assert.Empty(progress.Updates);
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

    [Fact]
    public async Task Card_replaced_during_copy_keeps_source_videos()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "copy-replacement")).FullName;
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "запись");
        var log = Directory.CreateDirectory(Path.Combine(source, "LOG")).FullName;
        var logFile = Path.Combine(log, "20260915.txt");
        File.WriteAllText(logFile, "#ID:1\n");
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
                if (update.Stage == BackupStage.Copying)
                    File.WriteAllText(logFile, "#ID:2\n");
            },
        };

        await new BackupService(Path.Combine(_root, "devices.db"), settings).RunAsync(1, source, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.Contains("носитель сменился", progress.Updates.Last().Detail);
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task Unreadable_settings_stop_the_backup()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(Settings.FilePath, "{ обрезанный файл");
        var settings = Settings.Load();
        Assert.True(settings.Unreadable);
        settings.MinFreeGb = 0;
        settings.DeleteVideoAfterCopy = true;
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "запись");
        var log = Directory.CreateDirectory(Path.Combine(source, "LOG")).FullName;
        File.WriteAllText(Path.Combine(log, "20260915.txt"), "#ID:1\n");
        var progress = new CapturedProgress();

        await new BackupService(Path.Combine(_root, "devices.db"), settings).RunAsync(1, source, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.True(File.Exists(recording));
        Assert.False(Directory.Exists(Path.Combine(settings.BackupRoot, "Device1")));
    }

    [Fact]
    public void Unreadable_settings_are_not_overwritten_silently()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(Settings.FilePath, "{ обрезанный файл");
        var settings = Settings.Load();

        Assert.False(settings.Save());
        Assert.Equal("{ обрезанный файл", File.ReadAllText(Settings.FilePath));

        settings.BackupRoot = Path.Combine(_root, "archive");
        Assert.True(settings.Save(replaceUnreadable: true));
        var reloaded = Settings.Load();
        Assert.False(reloaded.Unreadable);
        Assert.Equal(settings.BackupRoot, reloaded.BackupRoot);
        Assert.False(File.Exists(Settings.FilePath + ".tmp"));
    }

    [Fact]
    public void Missing_settings_file_is_not_unreadable()
    {
        Assert.False(File.Exists(Settings.FilePath));
        Assert.False(Settings.Load().Unreadable);
    }

    [Fact]
    public async Task A_new_session_uses_settings_saved_after_service_creation()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Assert.True(ArchiveGuard.Mark(first));
        Assert.True(ArchiveGuard.Mark(second));
        Assert.True(new Settings { BackupRoot = first, MinFreeGb = 0 }.Save());
        var service = new BackupService(AppPaths.Database);
        var changed = Settings.Load();
        changed.BackupRoot = second;
        changed.DeleteVideoAfterCopy = true;
        Assert.True(changed.Save());
        var dcim = Directory.CreateDirectory(Path.Combine(_root, "session", "DCIM")).FullName;
        var source = Path.GetDirectoryName(dcim)!;
        var recording = Path.Combine(dcim, "A11_1234567_222222_20260915120000_0001.mp4");
        File.WriteAllText(recording, "recording");
        await service.RunAsync(1234567, source, new CapturedProgress());
        Assert.True(File.Exists(Path.Combine(second, "Device1234567", "DCIM", Path.GetFileName(recording))));
        Assert.False(File.Exists(recording));
        Assert.False(Directory.Exists(Path.Combine(first, "Device1234567")));
    }

    [Fact]
    public async Task Cancellation_after_last_file_keeps_source_videos()
    {
        var dcim = Directory.CreateDirectory(Path.Combine(_root, "cancel", "DCIM")).FullName;
        var source = Path.GetDirectoryName(dcim)!;
        var recording = Path.Combine(dcim, "A11_1234567_222222_20260915120000_0001.mp4");
        File.WriteAllText(recording, "recording");
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0, DeleteVideoAfterCopy = true };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        using var cancellation = new CancellationTokenSource();
        var progress = new CapturedProgress { OnReport = p => { if (p.Stage == BackupStage.Copying) cancellation.Cancel(); } };
        await new BackupService(AppPaths.Database, settings).RunAsync(1234567, source, progress, cancellation.Token);
        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task A_busy_maintenance_lock_is_reported_to_the_caller_for_retry()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "busy", "LOG")).Parent!.FullName;
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:1\n");
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0 };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        using var maintenance = OperationGuard.Acquire(exclusive: true, dbPath: AppPaths.Database);
        await Assert.ThrowsAsync<StationBusyException>(() => new BackupService(AppPaths.Database, settings)
            .RunAsync(1, source, new CapturedProgress()));
    }

    public void Dispose()
    {
        AppPaths.Root = _appRoot;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Поздняя фоновая запись прошлых тестов может попасть в каталог
        // прямо во время удаления; остаток временного каталога не ошибка.
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
