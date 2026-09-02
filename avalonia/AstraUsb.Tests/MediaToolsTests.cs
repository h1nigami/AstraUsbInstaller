using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Просмотр и преобразование записей. Исходная запись собрана с регистратора и
/// рисковать ею ради копии в другом формате нельзя, поэтому проверяется, что
/// она остаётся на месте, а недоделанные файлы за собой не остаются.
/// </summary>
public sealed class MediaToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-media-").FullName;

    private string File_(string name, string content = "не настоящее видео")
    {
        var path = Path.Combine(_dir, name);
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
            var copy = Path.Combine(_dir, "VID_0001_копия.mov");
            Assert.False(File.Exists(copy));
        }
    }

    [Fact]
    public void An_existing_copy_is_not_overwritten()
    {
        var video = File_("VID_0001.MP4");
        File.WriteAllText(Path.Combine(_dir, "VID_0001_копия.mov"), "прежняя копия");

        var result = MediaTools.Convert(video, "mov");

        Assert.False(result.Ok);
        Assert.Contains("уже есть", result.Message);
        Assert.Equal("прежняя копия",
            File.ReadAllText(Path.Combine(_dir, "VID_0001_копия.mov")));
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
