using AstraUsb;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Автоудаление стирает данные с носителя, поэтому проверяем главное:
/// видео, которое не доехало до назначения, обязано остаться на месте.
/// </summary>
public sealed class SourceCleanerTests : IDisposable
{
    private readonly string _src = Directory.CreateTempSubdirectory("astra-clean-").FullName;

    private string Write(string relative)
    {
        var path = Path.Combine(_src, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "содержимое");
        return path;
    }

    [Fact]
    public void Deletes_only_videos_that_are_backed_up()
    {
        var saved = Write("saved.mp4");
        var notSaved = Write("not-saved.mp4");

        var deleted = SourceCleaner.DeleteBackedUpVideos(
            _src, new HashSet<string> { saved });

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(saved));
        Assert.True(File.Exists(notSaved),
            "видео, которое не попало в назначение, должно остаться на носителе");
    }

    [Fact]
    public void Leaves_photos_and_documents_alone()
    {
        var photo = Write("photo.jpg");
        var doc = Write("report.pdf");

        var deleted = SourceCleaner.DeleteBackedUpVideos(
            _src, new HashSet<string> { photo, doc });

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(doc));
    }

    [Fact]
    public void Walks_nested_folders()
    {
        var clip = Write(Path.Combine("DCIM", "100", "clip.MOV"));

        var deleted = SourceCleaner.DeleteBackedUpVideos(
            _src, new HashSet<string> { clip });

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(clip));
    }

    [Fact]
    public void Empty_backed_up_list_deletes_nothing()
    {
        var clip = Write("clip.mp4");

        var deleted = SourceCleaner.DeleteBackedUpVideos(_src, new HashSet<string>());

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(clip), "пустой список сохранённых не даёт удалять ничего");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_src, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
