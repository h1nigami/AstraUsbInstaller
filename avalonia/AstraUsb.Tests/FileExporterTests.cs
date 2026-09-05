using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Выгрузка найденного наружу. Это то, ради чего записи и собирают, поэтому
/// проверяется, что ничего не теряется и не затирается по дороге.
/// </summary>
public sealed class FileExporterTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 9, 2, 16, 30, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("astra-export-").FullName;
    private readonly string _store;
    private readonly string _target;

    public FileExporterTests()
    {
        _store = Path.Combine(_dir, "store");
        _target = Path.Combine(_dir, "flash");
        Directory.CreateDirectory(_store);
        Directory.CreateDirectory(_target);
    }

    private string File_(string relative, string content = "видео")
    {
        var path = Path.Combine(_store, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string ExportFolder => Path.Combine(_target, "Выгрузка_20260902_163000");

    [Fact]
    public void A_directory_with_the_same_name_does_not_block_export()
    {
        var source = File_("clip.mp4");
        Directory.CreateDirectory(Path.Combine(ExportFolder, "clip.mp4"));

        var result = FileExporter.Export([source], _target, Stamp);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(ExportFolder, "clip_2.mp4")));
    }

    [Fact]
    public void Files_land_in_a_folder_named_by_the_moment_of_export()
    {
        var one = File_("Device1/запись.mp4");

        var result = FileExporter.Export([one], _target, Stamp);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(Path.Combine(ExportFolder, "запись.mp4")));
    }

    [Fact]
    public void Same_names_from_different_cameras_do_not_overwrite_each_other()
    {
        var first = File_("Device1/запись.mp4", "первая");
        var second = File_("Device2/запись.mp4", "вторая");

        var result = FileExporter.Export([first, second], _target, Stamp);

        Assert.Equal(2, result.Copied);
        Assert.Equal("первая", File.ReadAllText(Path.Combine(ExportFolder, "запись.mp4")));
        Assert.Equal("вторая", File.ReadAllText(Path.Combine(ExportFolder, "запись_2.mp4")));
    }

    [Fact]
    public void A_file_missing_from_the_store_is_counted_not_hidden()
    {
        var present = File_("Device1/есть.mp4");
        var absent = Path.Combine(_store, "Device1", "нет.mp4");

        var result = FileExporter.Export([present, absent], _target, Stamp);

        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Missing);
    }

    [Fact]
    public void The_result_counts_the_bytes_actually_written()
    {
        var one = File_("Device1/запись.mp4", "12345");

        var result = FileExporter.Export([one], _target, Stamp);

        Assert.Equal(new FileInfo(one).Length, result.Bytes);
    }

    [Fact]
    public void Progress_is_reported_from_zero_to_the_end()
    {
        var files = new[] { File_("Device1/a.mp4"), File_("Device1/b.mp4") };
        var seen = new List<(int Done, int Total)>();

        FileExporter.Export(files, _target, Stamp, (done, total) => seen.Add((done, total)));

        Assert.Equal((0, 2), seen.First());
        Assert.Equal((2, 2), seen.Last());
    }

    [Fact]
    public void An_empty_selection_still_leaves_a_folder_and_no_files()
    {
        var result = FileExporter.Export([], _target, Stamp);

        Assert.Equal(0, result.Copied);
        Assert.True(Directory.Exists(ExportFolder));
        Assert.Empty(Directory.EnumerateFiles(ExportFolder));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
