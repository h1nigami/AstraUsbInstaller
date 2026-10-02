using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Просмотр и преобразование записей. Исходная запись собрана с регистратора и
/// рисковать ею ради копии в другом формате нельзя, поэтому проверяется, что
/// она остаётся на месте, а недоделанные файлы за собой не остаются.
/// </summary>
[Collection("Каталог данных")]
public sealed class MediaToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-media-").FullName;
    private readonly string _previous = AppPaths.Root;

    public MediaToolsTests() => AppPaths.Root = _dir;

    private string File_(string name, string content = "не настоящее видео")
    {
        var root = Path.Combine(AppPaths.BackupsRoot, "Device1");
        Directory.CreateDirectory(root);
        ArchiveGuard.Mark(AppPaths.BackupsRoot);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Formats_depend_on_the_sort_of_record()
    {
        Assert.Equal(["mp4", "mov"], MediaTools.FormatsFor(MediaKind.Video));
        Assert.Equal(["mp3", "wav", "wma"], MediaTools.FormatsFor(MediaKind.Audio));
        Assert.Equal(["jpg", "png", "bmp"], MediaTools.FormatsFor(MediaKind.Photo));

        // Журнал и служебные выгрузки не переводят никуда.
        Assert.Empty(MediaTools.FormatsFor(MediaKind.Log));
    }

    [Fact]
    public void A_missing_file_is_reported_not_thrown()
    {
        var absent = Path.Combine(_dir, "нет-такого.mp4");

        Assert.False(MediaTools.Open(absent).Ok);
        Assert.False(MediaTools.Convert(absent, "mov").Ok);
    }

    [Fact]
    public void External_viewing_does_not_start_during_exclusive_maintenance()
    {
        var record = File_("record.astra-no-viewer");
        using var maintenance = OperationGuard.Acquire(exclusive: true);

        var result = MediaTools.Open(record);

        Assert.False(result.Ok);
        Assert.Contains("занята", result.Message);
    }

    [Fact]
    public void Conversion_does_not_start_during_exclusive_maintenance()
    {
        var video = File_("clip.mp4");
        using var maintenance = OperationGuard.Acquire(exclusive: true);

        var result = MediaTools.Convert(video, "mov");

        Assert.False(result.Ok);
        Assert.Contains("занята", result.Message);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public void A_valid_image_is_converted_through_the_held_destination()
    {
        if (!MediaTools.ConverterAvailable())
            return;
        var image = File_("pixel.bmp");
        File.WriteAllBytes(image, System.Convert.FromHexString(
            "424d3a000000000000003600000028000000010000000100000001001800"
            + "0000000004000000000000000000000000000000000000000020ff00"));

        var result = MediaTools.Convert(image, "png");

        Assert.True(result.Ok, result.Message);
        var copy = Path.Combine(Path.GetDirectoryName(image)!, "pixel_копия.png");
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(copy)[..8]);
        Assert.True(File.Exists(image));
    }

    [Fact]
    public void Video_is_not_converted_into_sound()
    {
        var video = File_("VID_0001.MP4");

        var result = MediaTools.Convert(video, "mp3");

        Assert.False(result.Ok);
        Assert.Contains("не переводят", result.Message);
    }

    [Fact]
    public void An_empty_format_is_refused()
    {
        var result = MediaTools.Convert(File_("VID_0001.MP4"), "  ");

        Assert.False(result.Ok);
        Assert.Contains("формат", result.Message);
    }

    [Fact]
    public void A_cleared_format_is_reported_without_an_exception()
    {
        var result = MediaTools.Convert(File_("clip.mp4"), null!);

        Assert.False(result.Ok);
        Assert.Contains("формат", result.Message);
    }

    [Fact]
    public void Invalid_media_reports_a_russian_hint_instead_of_converter_stderr()
    {
        var result = MediaTools.Convert(File_("invalid.mp4"), "mov");

        Assert.False(result.Ok);
        Assert.Matches("[а-яА-Я]", result.Message);
        Assert.DoesNotContain(_dir, result.Message);
    }

    [Fact]
    public void The_source_record_survives_a_failed_conversion()
    {
        // Внутри лежит текст, а не видео, поэтому ffmpeg откажется, если он
        // вообще установлен. Исходный файл в любом случае остаётся на месте.
        var video = File_("VID_0001.MP4");

        MediaTools.Convert(video, "mov");

        Assert.True(File.Exists(video));
    }

    [Fact]
    public void A_failed_conversion_leaves_no_half_written_copy()
    {
        var video = File_("VID_0001.MP4");

        var result = MediaTools.Convert(video, "mov");

        if (!result.Ok)
        {
            var copy = Path.Combine(Path.GetDirectoryName(video)!, "VID_0001_копия.mov");
            Assert.False(File.Exists(copy));
        }
    }

    [Fact]
    public void An_existing_copy_is_not_overwritten()
    {
        var video = File_("VID_0001.MP4");
        var copy = Path.Combine(Path.GetDirectoryName(video)!, "VID_0001_копия.mov");
        File.WriteAllText(copy, "прежняя копия");

        var result = MediaTools.Convert(video, "mov");

        Assert.False(result.Ok);
        Assert.Contains("уже есть", result.Message);
        Assert.Equal("прежняя копия",
            File.ReadAllText(copy));
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
