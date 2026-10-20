using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Копирование это самое опасное место: по списку сохранённых файлов потом
/// удаляются видео с носителя. Проверяем именно это свойство.
/// </summary>
[Collection("Каталог данных")]
public sealed class FileCopierTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-copy-").FullName;
    private readonly string _src;
    private readonly string _dst;

    public FileCopierTests()
    {
        _src = Path.Combine(_root, "src");
        _dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
    }

    private string Write(string relative, string text, DateTime? mtime = null)
    {
        var path = Path.Combine(_src, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (mtime is { } t)
            File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    [Fact]
    public void Copies_new_files_and_reports_them_as_backed_up()
    {
        var photo = Write("photo.jpg", "картинка");

        var result = FileCopier.Copy(_src, _dst, "20260902_120000");

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(0, result.Failed);
        Assert.Contains(photo, result.BackedUp);
        Assert.True(File.Exists(Path.Combine(_dst, "photo.jpg")));
    }

    [Fact]
    public void Backup_preserves_the_source_access_time_after_reading()
    {
        var source = Write("clip.mp4", "video");
        File.SetLastWriteTimeUtc(source, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(source, new DateTime(2050, 2, 3, 4, 5, 6, DateTimeKind.Utc));

        var result = FileCopier.Copy(_src, _dst, "stamp");

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(File.GetLastAccessTimeUtc(source), File.GetLastAccessTimeUtc(Path.Combine(_dst, "clip.mp4")));
    }

    [Fact]
    public void Keeps_nested_structure()
    {
        Write(Path.Combine("DCIM", "100", "clip.mp4"), "видео");

        var result = FileCopier.Copy(_src, _dst, "20260902_120000");

        Assert.Equal(1, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(_dst, "DCIM", "100", "clip.mp4")));
    }

    [Fact]
    public void Identical_file_is_not_copied_again_but_counts_as_backed_up()
    {
        var when = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var source = Write("doc.txt", "текст", when);
        FileCopier.Copy(_src, _dst, "20260902_120000");

        var again = FileCopier.Copy(_src, _dst, "20260902_130000");

        Assert.Equal(0, again.CopiedFiles);
        Assert.Contains(source, again.BackedUp);
        Assert.Single(Directory.GetFiles(_dst));
    }

    [Fact]
    public void Changed_file_is_kept_alongside_the_previous_copy()
    {
        Write("doc.txt", "первая версия", new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        FileCopier.Copy(_src, _dst, "20260902_120000");

        Write("doc.txt", "вторая версия, другого размера",
            new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc));
        var again = FileCopier.Copy(_src, _dst, "20260902_130000");

        Assert.Equal(1, again.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(_dst, "doc.txt")),
            "прежняя копия должна остаться нетронутой");
        Assert.True(File.Exists(Path.Combine(_dst, "doc_20260902_130000.txt")),
            "изменившийся файл кладётся рядом с отметкой времени");
    }

    [Fact]
    public void An_existing_changed_version_is_reused_with_its_real_archive_path()
    {
        var when = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var source = Write(Path.Combine("DCIM", "clip.mp4"), "old", when);
        FileCopier.Copy(_src, _dst, "20260902_120000");
        Write(Path.Combine("DCIM", "clip.mp4"), "new version", when.AddDays(1));
        FileCopier.Copy(_src, _dst, "20260902_130000");

        var result = FileCopier.Copy(_src, _dst, "20260902_140000");

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.CopiedBytes);
        Assert.Equal(0, result.Failed);
        Assert.Contains(source, result.BackedUp);
        Assert.Equal(Path.Combine(_dst, "DCIM", "clip_20260902_130000.mp4"), result.Destinations[source]);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_dst, "DCIM")).Length);
    }

    [Theory]
    [InlineData(-900)]
    [InlineData(900)]
    public void A_timestamped_version_with_less_than_one_second_mtime_difference_is_reused(int milliseconds)
    {
        var when = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var source = Write("clip.mp4", "new", when);
        File.WriteAllText(Path.Combine(_dst, "clip.mp4"), "old version");
        var saved = Path.Combine(_dst, "clip_20260902_120000.mp4");
        File.WriteAllText(saved, "new");
        File.SetLastWriteTimeUtc(saved, when.AddMilliseconds(milliseconds));

        var result = FileCopier.Copy(_src, _dst, "20260902_140000");

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(saved, result.Destinations[source]);
    }

    [Theory]
    [InlineData("clip_old.mp4", "new", 0)]
    [InlineData("other_clip_20260902_120000.mp4", "new", 0)]
    [InlineData("clip_20260902_120000.mp4.bak", "new", 0)]
    [InlineData("clip_20260902_120000.mp4", "different size", 0)]
    [InlineData("clip_20260902_120000.mp4", "new", 1000)]
    public void Similar_names_or_different_metadata_do_not_replace_a_new_backup(string name, string content, int milliseconds)
    {
        var when = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var source = Write("clip.mp4", "new", when);
        File.WriteAllText(Path.Combine(_dst, "clip.mp4"), "old version");
        var candidate = Path.Combine(_dst, name);
        File.WriteAllText(candidate, content);
        File.SetLastWriteTimeUtc(candidate, when.AddMilliseconds(milliseconds));

        var result = FileCopier.Copy(_src, _dst, "20260902_140000");

        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(Path.Combine(_dst, "clip_20260902_140000.mp4"), result.Destinations[source]);
        Assert.Equal(content, File.ReadAllText(candidate));
    }

    [Fact]
    public void A_different_timestamp_collision_keeps_the_original_off_the_backed_up_list()
    {
        var source = Write("clip.mp4", "new");
        File.WriteAllText(Path.Combine(_dst, "clip.mp4"), "old version");
        var collision = Path.Combine(_dst, "clip_20260902_140000.mp4");
        File.WriteAllText(collision, "unrelated existing version");

        var result = FileCopier.Copy(_src, _dst, "20260902_140000");

        Assert.Equal(1, result.Failed);
        Assert.DoesNotContain(source, result.BackedUp);
        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("unrelated existing version", File.ReadAllText(collision));
    }

    [Fact]
    public void Marker_file_is_never_copied()
    {
        Write(Markers.LegacyId, "42");
        Write(Markers.CardId, "BCU-01-0001");
        Write("photo.jpg", "картинка");

        var result = FileCopier.Copy(_src, _dst, "20260902_120000");

        Assert.Equal(1, result.CopiedFiles);
        Assert.False(File.Exists(Path.Combine(_dst, Markers.LegacyId)));
        Assert.False(File.Exists(Path.Combine(_dst, Markers.CardId)));
    }

    [Fact]
    public void Unreachable_destination_marks_files_failed_and_keeps_them_off_the_backed_up_list()
    {
        var video = Write("clip.mp4", "видео");
        // Файл на месте каталога назначения: создать каталог не выйдет.
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "не каталог");

        var result = FileCopier.Copy(_src, blocked, "20260902_120000");

        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(1, result.Failed);
        Assert.DoesNotContain(video, result.BackedUp);
        Assert.Empty(result.BackedUp);
    }

    [Fact]
    public void Progress_is_reported_while_copying()
    {
        Write("a.bin", "раз");
        Write("b.bin", "два");
        var seen = 0;

        FileCopier.Copy(_src, _dst, "20260902_120000", (files, _) => seen = files);

        Assert.Equal(2, seen);
    }

    [Fact]
    public void Progress_counts_skipped_files_and_bytes_without_inflating_copy_statistics()
    {
        var when = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        Write("old.mp4", "123", when);
        FileCopier.Copy(_src, _dst, "20260902_120000");
        Write("new.mp4", "12345", when);
        var seen = new List<(int Files, long Bytes)>();

        var result = FileCopier.Copy(_src, _dst, "20260902_130000", (files, bytes) => seen.Add((files, bytes)));

        Assert.Equal((2, 8L), seen.Last());
        Assert.Equal(2, seen.Count);
        Assert.Equal(1, result.CopiedFiles);
        Assert.Equal(5, result.CopiedBytes);
        Assert.Equal(2, result.BackedUp.Count);
    }

    [Fact]
    public void Progress_reaches_the_end_when_every_file_was_already_saved()
    {
        Write("one.mp4", "123");
        Write("two.mp4", "12345");
        FileCopier.Copy(_src, _dst, "20260902_120000");
        var seen = new List<(int Files, long Bytes)>();

        var result = FileCopier.Copy(_src, _dst, "20260902_130000", (files, bytes) => seen.Add((files, bytes)));

        Assert.NotEmpty(seen);
        Assert.Equal((2, 8L), seen.Last());
        Assert.Equal(0, result.CopiedFiles);
        Assert.Equal(0, result.CopiedBytes);
    }

    [Fact]
    public void A_copy_failure_records_the_source_and_the_underlying_reason()
    {
        var previousRoot = AppPaths.Root;
        AppPaths.Root = _root;
        try
        {
            var source = Write("clip.mp4", "new");
            File.WriteAllText(Path.Combine(_dst, "clip.mp4"), "old version");
            File.WriteAllText(Path.Combine(_dst, "clip_stamp.mp4"), "collision");

            var result = FileCopier.Copy(_src, _dst, "stamp");

            Assert.Equal(1, result.Failed);
            Assert.True(File.Exists(CrashLog.FilePath));
            var log = File.ReadAllText(CrashLog.FilePath);
            Assert.Contains(source, log);
            Assert.Contains("IOException", log);
            Assert.Contains("clip_stamp.mp4", log);
        }
        finally { AppPaths.Root = previousRoot; }
    }

    [Fact]
    public void An_unreachable_destination_records_its_path_and_reason()
    {
        var previousRoot = AppPaths.Root;
        AppPaths.Root = _root;
        try
        {
            Write("clip.mp4", "new");
            var blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "not a directory");

            var result = FileCopier.Copy(_src, blocked, "stamp");

            Assert.Equal(1, result.Failed);
            Assert.True(File.Exists(CrashLog.FilePath));
            var log = File.ReadAllText(CrashLog.FilePath);
            Assert.Contains(blocked, log);
            Assert.Contains("Exception", log);
        }
        finally { AppPaths.Root = previousRoot; }
    }

    [Fact]
    public void A_linked_destination_subfolder_is_rejected()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var link = Path.Combine(_dst, "DCIM");
        MakeDirectoryLink(link, outside);
        try
        {
            var source = Write("DCIM/clip.mp4", "keep source");
            var result = FileCopier.Copy(_src, _dst, "stamp");
            Assert.True(result.Failed > 0);
            Assert.DoesNotContain(source, result.BackedUp);
            Assert.False(File.Exists(Path.Combine(outside, "clip.mp4")));
        }
        finally { Directory.Delete(link); }
    }

    internal static void MakeDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(link, target);
        else
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
    }

    [Fact]
    public void Cancellation_between_files_keeps_originals_and_stops_the_copy()
    {
        var one = Write("one.mp4", "one");
        var two = Write("two.mp4", "two");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => FileCopier.Copy(_src, _dst, "stamp",
            (_, _) => cancellation.Cancel(), cancellation.Token));
        Assert.True(File.Exists(one));
        Assert.True(File.Exists(two));
        Assert.Single(Directory.GetFiles(_dst));
    }

    [Fact]
    public void Cancellation_during_a_large_file_removes_its_partial_copy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var source = Write("large.mp4", "");
        using (var file = File.OpenWrite(source))
            file.SetLength(512L * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(() => FileCopier.Copy(_src, _dst, "stamp", token: cancellation.Token));
        var target = Path.Combine(_dst, "large.mp4");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(target) || task.IsCompleted, TimeSpan.FromSeconds(10)));
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void A_removed_source_stops_the_session_for_recovery()
    {
        Write("one.mp4", "one");
        Write("two.mp4", "two");
        var error = Assert.ThrowsAny<IOException>(() => FileCopier.Copy(_src, _dst, "stamp",
            (_, _) => Directory.Move(_src, _src + "-removed")));
        Assert.Equal("DeviceLostException", error.GetType().Name);
        Assert.Single(Directory.GetFiles(_dst));
        Assert.Equal(2, Directory.GetFiles(_src + "-removed").Length);
    }

    [Fact]
    public void A_wrapped_native_source_error_is_recognized_for_recovery()
    {
        var classify = typeof(DeviceLostException).GetMethod("IsRemoval",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var native = new System.ComponentModel.Win32Exception(OperatingSystem.IsLinux() ? 5 : 1117);
        var read = new IOException("read failed", native);
        var identify = new IOException("cannot read device ID", read);
        Assert.True((bool)classify.Invoke(null, [identify])!);
        var denied = new IOException("access denied",
            new System.ComponentModel.Win32Exception(OperatingSystem.IsLinux() ? 13 : 5));
        Assert.False((bool)classify.Invoke(null, [denied])!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
