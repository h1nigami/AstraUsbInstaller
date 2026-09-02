using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Копирование — самое опасное место: по списку сохранённых файлов потом
/// удаляются видео с носителя. Проверяем именно это свойство.
/// </summary>
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
    public void Marker_file_is_never_copied()
    {
        Write(DeviceRegistry.DeviceIdFile, "42");
        Write("photo.jpg", "картинка");

        var result = FileCopier.Copy(_src, _dst, "20260902_120000");

        Assert.Equal(1, result.CopiedFiles);
        Assert.False(File.Exists(Path.Combine(_dst, DeviceRegistry.DeviceIdFile)));
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
