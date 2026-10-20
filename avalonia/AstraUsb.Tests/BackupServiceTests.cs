using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-backup-").FullName;
    private readonly string _appRoot = AppPaths.Root;

    public BackupServiceTests() => AppPaths.Root = _root;

    [Fact]
    public async Task The_current_archive_volume_is_rejected_even_if_a_stale_poll_submits_it_as_a_camera()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:7\n");
        var original = Path.Combine(source, "clip.mp4");
        File.WriteAllText(original, "original");
        var settings = new Settings { BackupRoot = Path.Combine(source, "archive"), MinFreeGb = 0 };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        using var cancel = new CancellationTokenSource();
        var progress = new CapturedProgress
        {
            OnReport = update => { if (update.Stage == BackupStage.Copying) cancel.Cancel(); },
        };

        await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress, cancel.Token);

        Assert.False(Directory.Exists(Path.Combine(settings.BackupRoot, "Device7")));
        Assert.DoesNotContain(progress.Updates, update => update.Stage == BackupStage.Copying);
        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Contains("архив", File.ReadAllText(StationLog.FilePath));
    }

    [Theory]
    [InlineData("PurgeExpired")]
    [InlineData("QueueForServer")]
    [InlineData("RecordCollected")]
    public void A_database_failure_in_a_secondary_step_preserves_its_cause(string step)
    {
        var db = Path.Combine(_root, "broken.db");
        File.WriteAllText(db, "не база данных");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), KeepDays = 1, FtpEnabled = true,
        };
        var source = Path.Combine(_root, "clip.mp4");
        File.WriteAllText(source, "original");
        var copied = new CopyResult(1, 8,
            new Dictionary<string, string> { [source] = Path.Combine(settings.BackupRoot, "Device7", "clip.mp4") }, 0);
        var service = new BackupService(db, settings);
        object?[] args = step switch
        {
            "PurgeExpired" => [settings, settings.BackupRoot],
            "QueueForServer" => [settings, copied],
            _ => [7L, copied, DateTime.Now, settings, settings.BackupRoot],
        };

        var result = InvokeStep(service, step, args);

        if (step == "RecordCollected") Assert.Equal(false, result);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(CrashLog.FilePath));
        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("SqliteException", log);
        Assert.Contains(db, log);
    }

    [Theory]
    [InlineData("QueueForServer")]
    [InlineData("RecordCollected")]
    public void A_programming_error_in_a_secondary_step_reaches_the_outer_error_boundary(string step)
    {
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), FtpEnabled = true };
        var service = new BackupService(AppPaths.Database, settings);
        object?[] args = step == "QueueForServer"
            ? [settings, null]
            : [7L, null, DateTime.Now, settings, settings.BackupRoot];

        Assert.Throws<NullReferenceException>(() => InvokeStep(service, step, args));
    }

    [Fact]
    public async Task A_session_logs_new_file_progress_and_only_the_final_progress_for_skips()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:7\n");
        File.WriteAllBytes(Path.Combine(source, "clip.mp4"), new byte[4096]);
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0 };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        var service = new BackupService(AppPaths.Database, settings);

        await service.RunAsync(7, source, new CapturedProgress());
        var first = File.ReadAllLines(StationLog.FilePath);
        Assert.Contains(first, line => line.Contains("clip.mp4") && line.Contains("1/2")
            && line.Contains("осталось") && line.Contains("% |"));
        await service.RunAsync(7, source, new CapturedProgress());

        var log = File.ReadAllLines(StationLog.FilePath);
        Assert.Equal(2, log.Count(line => line.Contains("камера 7: сканирование")));
        Assert.Equal(2, log.Count(line => line.Contains("камера 7: найдено 2") && line.Contains("КБ")));
        Assert.Contains(log, line => line.Contains("камера 7: загружено 0 файлов"));
        var repeated = log.Skip(first.Length).ToArray();
        var final = Assert.Single(repeated, line => line.Contains("% |"));
        Assert.Matches(@"100[.,]0%", final);
        Assert.Contains("2/2", final);
        Assert.DoesNotContain(repeated, line => line.Contains("clip.mp4"));
    }

    [Fact]
    public async Task An_empty_session_writes_its_result_to_the_station_log()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var journal = Directory.CreateDirectory(Path.Combine(_root, "journal")).FullName;
        File.WriteAllText(Path.Combine(journal, "log.txt"), "#ID:7\n");
        var link = Path.Combine(source, "LOG");
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c mklink /J \"{link}\" \"{journal}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        else Directory.CreateSymbolicLink(link, journal);
        var settings = new Settings { BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0 };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        try
        {
            var progress = new CapturedProgress();
            await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress);

            Assert.Equal(BackupStage.Done, progress.Updates.Last().Stage);
            Assert.Equal("нечего копировать", progress.Updates.Last().Detail);
            Assert.Contains("нечего копировать", File.ReadAllText(StationLog.FilePath));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void An_unreadable_file_is_counted_with_zero_bytes_and_does_not_stop_the_scan()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "scan")).FullName;
        var unreadable = Path.Combine(source, "unreadable.mp4");
        File.WriteAllBytes(unreadable, new byte[20]);
        var nested = Directory.CreateDirectory(Path.Combine(source, "other")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "good.mp4"), new byte[12]);
        var length = new Func<string, long>(path => path == unreadable
            ? throw new UnauthorizedAccessException("file access denied: " + path)
            : new FileInfo(path).Length);

        var total = Measure(source, length);

        Assert.Equal((2, 12L), total);
        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains(unreadable, log);
        Assert.Contains("file access denied", log);
        Assert.True(File.Exists(unreadable));
    }

    [Fact]
    public void A_disappeared_subdirectory_is_reported_and_other_directories_are_still_scanned()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "scan-directories")).FullName;
        foreach (var name in new[] { "one", "two", "three" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(source, name)).FullName;
            File.WriteAllBytes(Path.Combine(directory, "clip.mp4"), new byte[4]);
        }
        string? removed = null;
        var length = new Func<string, long>(path =>
        {
            if (removed is null)
            {
                removed = Directory.GetDirectories(source).First(directory => directory != Path.GetDirectoryName(path));
                Directory.Delete(removed, recursive: true);
            }
            return new FileInfo(path).Length;
        });

        Assert.Equal((2, 8L), Measure(source, length));

        Assert.NotNull(removed);
        Assert.Contains(removed, File.ReadAllText(CrashLog.FilePath));
        Assert.Equal(2, Directory.GetDirectories(source).Length);
    }

    [Theory]
    [InlineData(0L, "0 Б")]
    [InlineData(18L, "18 Б")]
    public void Small_files_keep_their_actual_byte_count_in_the_session_summary(long bytes, string expected)
    {
        var formatted = typeof(BackupService).GetMethod("Size",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [bytes]);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void A_disappeared_scan_root_still_requests_device_recovery()
    {
        Assert.Throws<DeviceLostException>(() => Measure(Path.Combine(_root, "removed")));
    }

    [Fact]
    public async Task A_failed_identity_recheck_keeps_originals_and_records_the_actual_cause()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "identity-recheck")).FullName;
        var recording = Path.Combine(source, "clip.mp4");
        File.WriteAllText(recording, "video");
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        var idLog = Path.Combine(source, "LOG", "log.txt");
        File.WriteAllText(idLog, "#ID:7\n");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0, DeleteVideoAfterCopy = true,
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        var progress = new CapturedProgress
        {
            OnReport = state =>
            {
                if (state.Stage == BackupStage.Copying) File.WriteAllBytes(idLog, [0xff]);
            },
        };

        await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress);

        Assert.Equal(BackupStage.Failed, progress.Updates.Last().Stage);
        Assert.True(File.Exists(recording));
        Assert.True(File.Exists(CrashLog.FilePath));
        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("DecoderFallbackException", log);
        Assert.Contains("7", log);
        Assert.Contains(source, log);
    }

    private static (int Files, long Bytes) Measure(string root, Func<string, long>? length = null)
    {
        try
        {
            return ((int, long))typeof(BackupService).GetMethod("Measure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, [root, CancellationToken.None, length])!;
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static object? InvokeStep(BackupService service, string method, params object?[] arguments)
    {
        try
        {
            return typeof(BackupService).GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, arguments);
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

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
