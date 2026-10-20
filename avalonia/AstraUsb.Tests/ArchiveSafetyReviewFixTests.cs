using System.ComponentModel;
using System.Runtime.InteropServices;
using AstraUsb.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class ArchiveSafetyReviewFixTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-review18-archive-").FullName;
    private readonly string _previous = AppPaths.Root;

    public ArchiveSafetyReviewFixTests() => AppPaths.Root = Path.Combine(_root, "app");

    [Fact]
    public void Cleanup_keeps_a_replacement_source_root_even_with_the_same_names_and_metadata()
    {
        var source = Source();
        var video = Path.Combine(source, "clip.mp4");
        File.WriteAllText(video, "OLD");
        var copied = FileCopier.Copy(source, Path.Combine(_root, "copies"), "20261020_120000");
        var modified = File.GetLastWriteTimeUtc(video);
        Directory.Move(source, source + "-previous");
        Directory.CreateDirectory(source);
        File.WriteAllText(video, "NEW");
        File.SetLastWriteTimeUtc(video, modified);

        Assert.Throws<IOException>(() => Clean(source, copied));

        Assert.True(File.Exists(video), "Новый носитель нельзя очищать по списку прежнего");
        Assert.Equal("NEW", File.ReadAllText(video));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_root, "copies", "clip.mp4")));
    }

    [Fact]
    public void Cleanup_keeps_a_replacement_file_even_with_the_same_length_and_modification_time()
    {
        var source = Source();
        var video = Path.Combine(source, "clip.mp4");
        File.WriteAllText(video, "OLD");
        var copied = FileCopier.Copy(source, Path.Combine(_root, "copies"), "20261020_120000");
        var modified = File.GetLastWriteTimeUtc(video);
        File.Move(video, video + ".previous");
        File.WriteAllText(video, "NEW");
        File.SetLastWriteTimeUtc(video, modified);

        Clean(source, copied);

        Assert.True(File.Exists(video), "Одноимённый файл без копии должен остаться");
        Assert.Equal("NEW", File.ReadAllText(video));
    }

    [Fact]
    public void Cancelled_cleanup_keeps_all_originals()
    {
        var source = Source();
        var video = Path.Combine(source, "clip.mp4");
        File.WriteAllText(video, "OLD");
        var copied = FileCopier.Copy(source, Path.Combine(_root, "copies"), "20261020_120000");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Clean(source, copied, cancellation.Token));

        Assert.True(File.Exists(video));
    }

    [Fact]
    public void Cancellation_between_deletions_keeps_the_remaining_original()
    {
        var (source, copied) = TwoVideos();
        var paths = copied.Destinations.Keys.ToArray();
        using var cancellation = new CancellationTokenSource();
        copied = copied with { Destinations = new BetweenFiles(copied.Destinations, () =>
        {
            Assert.False(File.Exists(paths[0]));
            cancellation.Cancel();
        }) };

        Assert.Throws<OperationCanceledException>(() => Clean(source, copied, cancellation.Token));

        Assert.True(File.Exists(paths[1]));
    }

    [Fact]
    public void A_file_replaced_between_deletions_is_preserved()
    {
        var (source, copied) = TwoVideos();
        var paths = copied.Destinations.Keys.ToArray();
        var modified = File.GetLastWriteTimeUtc(paths[1]);
        copied = copied with { Destinations = new BetweenFiles(copied.Destinations, () =>
        {
            Assert.False(File.Exists(paths[0]));
            File.Move(paths[1], paths[1] + ".previous");
            File.WriteAllText(paths[1], "NEW");
            File.SetLastWriteTimeUtc(paths[1], modified);
        }) };

        Assert.Equal(1, Clean(source, copied));

        Assert.Equal("NEW", File.ReadAllText(paths[1]));
    }

    [Fact]
    public void Cleanup_pins_the_source_root_through_the_delete_loop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (source, copied) = TwoVideos();
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement")).FullName;
        File.WriteAllText(Path.Combine(replacement, "first.mp4"), "NEW");
        File.WriteAllText(Path.Combine(replacement, "second.mp4"), "NEW");
        var prevented = false;
        copied = copied with { Destinations = new BetweenFiles(copied.Destinations, () =>
        {
            try
            {
                Directory.Move(source, source + "-previous");
                Directory.Move(replacement, source);
            }
            catch (IOException) { prevented = true; }
        }) };

        Assert.Equal(2, Clean(source, copied));

        Assert.True(prevented, "Открытый корень должен запрещать подмену каталога во время очистки");
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(replacement, "first.mp4")));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(replacement, "second.mp4")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_already_saved_source_does_not_evict_another_device(bool timestamped)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Source();
        var journal = Directory.CreateDirectory(Path.Combine(source, "LOG")).FullName;
        File.WriteAllText(Path.Combine(journal, "log.txt"), "#ID:7\n");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), MinFreeGb = 0,
            StorageMode = StorageMode.Overwrite,
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        var destination = Directory.CreateDirectory(Path.Combine(settings.BackupRoot, "Device7")).FullName;
        var savedJournal = Directory.CreateDirectory(Path.Combine(destination, "LOG")).FullName;
        var idLog = Path.Combine(journal, "log.txt");
        var savedLog = Path.Combine(savedJournal, "log.txt");
        File.Copy(idLog, savedLog);
        File.SetLastWriteTimeUtc(savedLog, File.GetLastWriteTimeUtc(idLog));
        var length = new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace + 32L * 1024 * 1024;
        var video = Path.Combine(source, "clip.mp4");
        Sparse(video, length);
        var saved = Path.Combine(destination, timestamped ? "clip_20261019_120000.mp4" : "clip.mp4");
        Sparse(saved, length);
        File.SetLastWriteTimeUtc(saved, File.GetLastWriteTimeUtc(video));
        if (timestamped) File.WriteAllText(Path.Combine(destination, "clip.mp4"), "older version");
        var oldDirectory = Directory.CreateDirectory(Path.Combine(settings.BackupRoot, "Device99")).FullName;
        var old = Path.Combine(oldDirectory, "old.mp4");
        File.WriteAllBytes(old, new byte[1024]);
        var log = new CollectionLog(AppPaths.Database);
        log.Record([
            new CollectedFile(99, old, 1024, null, DateTime.Now.AddYears(-1)),
            new CollectedFile(7, saved, length, null, DateTime.Now.AddDays(-1)),
        ]);
        log.SetImportant(saved, true);
        if (timestamped)
        {
            var original = Path.Combine(destination, "clip.mp4");
            log.Record([new CollectedFile(7, original, 13, null, DateTime.Now.AddDays(-2))]);
            log.SetImportant(original, true);
        }
        var progress = new CapturedProgress();

        await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress);

        Assert.True(File.Exists(old), "Повторная выгрузка без новых байт не должна вытеснять чужую запись");
        Assert.Equal(BackupStage.Done, progress.Updates.Last().Stage);
        Assert.StartsWith("0 файлов", progress.Updates.Last().Detail);
        Assert.Contains(new CollectionLog(AppPaths.Database).CollectedBetween(DateTime.MinValue, DateTime.MaxValue),
            entry => entry.DestPath == old);
    }

    [Fact]
    public async Task A_noop_backup_does_not_evict_other_devices_to_replenish_the_free_space_reserve()
    {
        var source = Source();
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:7\n");
        File.WriteAllText(Path.Combine(source, "clip.mp4"), "saved");
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), StorageMode = StorageMode.Overwrite,
            MinFreeGb = checked((int)(new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace / (1L << 30)) + 1),
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        var copied = FileCopier.Copy(source, Path.Combine(settings.BackupRoot, "Device7"), "20261020_120000");
        var log = new CollectionLog(AppPaths.Database);
        foreach (var saved in copied.Destinations.Values)
        {
            log.Record([new CollectedFile(7, saved, new FileInfo(saved).Length, null, DateTime.Now.AddDays(-1))]);
            log.SetImportant(saved, true);
        }
        var other = Directory.CreateDirectory(Path.Combine(settings.BackupRoot, "Device99")).FullName;
        var old = Path.Combine(other, "old.mp4");
        File.WriteAllText(old, "keep this recording");
        log.Record([new CollectedFile(99, old, new FileInfo(old).Length, null, DateTime.Now.AddYears(-1))]);
        var progress = new CapturedProgress();

        await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress);

        Assert.True(File.Exists(old), "Выгрузка без новых данных не должна удалять записи ради свободного резерва");
        Assert.Equal(BackupStage.Done, progress.Updates.Last().Stage);
        Assert.StartsWith("0 файлов", progress.Updates.Last().Detail);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Overwrite_keeps_reused_copies_and_can_evict_unused_files_from_the_same_device(
        bool timestamped, bool logged)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Source();
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:7\n");
        var original = Path.Combine(source, "saved.mp4");
        File.WriteAllBytes(original, new byte[1024]);
        File.SetLastWriteTimeUtc(original, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), StorageMode = StorageMode.Overwrite,
            MinFreeGb = checked((int)(new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace / (1L << 30)) + 1),
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        var destination = Path.Combine(settings.BackupRoot, "Device7");
        FileCopier.Copy(source, destination, "20261020_120000");
        var saved = Path.Combine(destination, "saved.mp4");
        if (timestamped)
        {
            var previous = saved;
            saved = Path.Combine(destination, "saved_20261019_120000.mp4");
            File.Move(previous, saved);
            File.WriteAllText(previous, "unused older version");
            File.SetLastWriteTimeUtc(previous, new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
        var unused = Path.Combine(destination, "unused.mp4");
        Sparse(unused, 2L * 1024 * 1024 * 1024);
        File.SetLastWriteTimeUtc(unused, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        if (logged)
            new CollectionLog(AppPaths.Database).Record([
                new CollectedFile(7, saved, 1024, null, DateTime.Now.AddYears(-2)),
                new CollectedFile(7, unused, 2L * 1024 * 1024 * 1024, null, DateTime.Now.AddYears(-1)),
            ]);
        File.WriteAllBytes(Path.Combine(source, "new.mp4"), new byte[128]);
        var progress = new CapturedProgress();

        await new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress);

        Assert.True(File.Exists(saved), "Расчёт недостающих байтов опирается на эту сохранённую копию");
        Assert.False(File.Exists(unused), "Неиспользуемая запись того же устройства должна оставаться доступной для вытеснения");
        Assert.Equal(BackupStage.Done, progress.Updates.Last().Stage);
        Assert.Equal("1 файл, 128 Б", progress.Updates.Last().Detail);
        Assert.Equal(128, new FileInfo(Path.Combine(destination, "new.mp4")).Length);
    }

    [Fact]
    public async Task Concurrent_cleanup_rechecks_new_missing_bytes_and_stops_retrying_when_the_plan_is_stable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Source();
        Directory.CreateDirectory(Path.Combine(source, "LOG"));
        File.WriteAllText(Path.Combine(source, "LOG", "log.txt"), "#ID:7\n");
        File.WriteAllBytes(Path.Combine(source, "saved.mp4"), new byte[1024]);
        var settings = new Settings
        {
            BackupRoot = Path.Combine(_root, "archive"), StorageMode = StorageMode.Overwrite,
            MinFreeGb = checked((int)(new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace / (1L << 30)) + 1),
        };
        Assert.True(ArchiveGuard.Mark(settings.BackupRoot));
        Assert.True(settings.Save());
        var destination = Path.Combine(settings.BackupRoot, "Device7");
        FileCopier.Copy(source, destination, "20261020_120000");
        var saved = Path.Combine(destination, "saved.mp4");
        var firstEviction = Path.Combine(destination, "first-eviction.mp4");
        Sparse(firstEviction, 2L << 30);
        var log = new CollectionLog(AppPaths.Database);
        log.Record([
            new CollectedFile(7, saved, 1024, null, DateTime.Now.AddYears(-3)),
            new CollectedFile(7, firstEviction, 2L << 30, null, DateTime.Now.AddYears(-2)),
        ]);
        File.WriteAllBytes(Path.Combine(source, "new.mp4"), new byte[128]);
        var planned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var progress = new CapturedProgress
        {
            OnReport = update =>
            {
                if (update.Detail != "проверяем место в архиве") return;
                planned.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Вторая выгрузка не завершилась");
            },
        };
        var first = Task.Run(() => new BackupService(AppPaths.Database, settings).RunAsync(7, source, progress));
        string initial = "", additional = "", secondCopy = "";
        try
        {
            await planned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other = Directory.CreateDirectory(Path.Combine(_root, "other-source")).FullName;
            Directory.CreateDirectory(Path.Combine(other, "LOG"));
            File.WriteAllText(Path.Combine(other, "LOG", "log.txt"), "#ID:8\n");
            File.WriteAllBytes(Path.Combine(other, "other.mp4"), new byte[64]);
            var secondProgress = new CapturedProgress();

            await new BackupService(AppPaths.Database, settings).RunAsync(8, other, secondProgress);

            Assert.Equal(BackupStage.Done, secondProgress.Updates.Last().Stage);
            Assert.False(File.Exists(saved), "Вторая выгрузка должна вытеснить копию, учтённую первым заданием");
            secondCopy = Path.Combine(settings.BackupRoot, "Device8", "other.mp4");
            initial = Path.Combine(destination, "initial-space.mp4");
            additional = Path.Combine(destination, "additional-space.mp4");
            Sparse(initial, 2L << 30);
            Sparse(additional, 2L << 30);
            log.Record([
                new CollectedFile(7, initial, 2L << 30, null, DateTime.Now.AddYears(-2)),
                new CollectedFile(7, additional, 2L << 30, null, DateTime.Now.AddYears(-1)),
            ]);
        }
        finally
        {
            resume.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(File.Exists(initial));
        Assert.False(File.Exists(additional), "После вытеснения сохранённой копии надо пересчитать нужное место");
        Assert.True(File.Exists(secondCopy), "Стабильный расчёт не должен повторно пополнять недостижимый резерв");
        Assert.True(File.Exists(saved));
        Assert.Equal(BackupStage.Done, progress.Updates.Last().Stage);
    }

    private string Source() => Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;

    private (string Source, CopyResult Copied) TwoVideos()
    {
        var source = Source();
        File.WriteAllText(Path.Combine(source, "first.mp4"), "OLD");
        File.WriteAllText(Path.Combine(source, "second.mp4"), "OLD");
        return (source, FileCopier.Copy(source, Path.Combine(_root, "copies"), "20261020_120000"));
    }

    private sealed class BetweenFiles(IReadOnlyDictionary<string, string> files, Action between)
        : Dictionary<string, string>(files), IReadOnlyDictionary<string, string>
    {
        IEnumerable<string> IReadOnlyDictionary<string, string>.Keys
        {
            get
            {
                var first = true;
                foreach (var key in files.Keys)
                {
                    if (!first) between();
                    first = false;
                    yield return key;
                }
            }
        }
    }

    private static int Clean(string source, CopyResult copied, CancellationToken token = default) =>
        SourceCleaner.DeleteBackedUpVideos(source, copied, token);

    private static void Sparse(string path, long length)
    {
        using var file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.True(DeviceIoControl(file, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        RandomAccess.SetLength(file, length);
        file.Dispose();
        var low = GetCompressedFileSizeW(path, out var high);
        Assert.Equal(0UL, ((ulong)high << 32) | low);
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

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputLength,
        IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);
}
