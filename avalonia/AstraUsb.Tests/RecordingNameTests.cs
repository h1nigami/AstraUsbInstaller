using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Разбор имён записей. Метаданных внутри файла камера почти не пишет, зато
/// имя несёт модель, номер устройства, номер сотрудника и время начала записи.
/// </summary>
public sealed class RecordingNameTests
{
    [Fact]
    public void Reads_everything_the_camera_puts_in_the_name()
    {
        var info = RecordingName.Parse("A11_2222222_222222_20260902180118_0001.mp4");

        Assert.NotNull(info);
        Assert.Equal("A11", info.Model);
        Assert.Equal("2222222", info.DeviceNo);
        Assert.Equal("222222", info.PersonnelNo);
        Assert.Equal(new DateTime(2026, 9, 2, 18, 1, 18), info.ShotAt);
        Assert.Equal(1, info.Sequence);
    }

    [Fact]
    public void Shot_time_comes_from_the_name_not_the_file_date()
    {
        // Дата файла меняется при копировании, а имя — нет.
        var info = RecordingName.Parse("A11_2222222_222222_20260101093000_0042.MP4");

        Assert.Equal(new DateTime(2026, 1, 1, 9, 30, 0), info!.ShotAt);
        Assert.Equal(42, info.Sequence);
    }

    [Fact]
    public void Factory_zeros_do_not_count_as_numbers()
    {
        var info = RecordingName.Parse("A11_0000000_000000_20260902180118_0001.mp4");

        Assert.NotNull(info);
        Assert.False(info.HasDeviceNo);
        Assert.False(info.HasPersonnelNo);
    }

    [Fact]
    public void Real_numbers_are_recognised()
    {
        var info = RecordingName.Parse("A11_2222222_222222_20260902180118_0001.mp4");

        Assert.True(info!.HasDeviceNo);
        Assert.True(info.HasPersonnelNo);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("A11_2222222_20260902180118_0001.mp4")]
    [InlineData("A11_abc_222222_20260902180118_0001.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void Alien_names_are_not_parsed(string? name)
    {
        Assert.Null(RecordingName.Parse(name));
    }

    [Fact]
    public void Impossible_timestamp_is_rejected()
    {
        Assert.Null(RecordingName.Parse("A11_2222222_222222_20261345990000_0001.mp4"));
    }

    [Fact]
    public void Takes_the_newest_recording_from_the_card()
    {
        var card = Directory.CreateTempSubdirectory("astra-rec-").FullName;
        try
        {
            var video = Path.Combine(card, "DCIM", "VIDEO");
            Directory.CreateDirectory(video);
            File.WriteAllText(Path.Combine(video, "A11_1111111_111111_20260101120000_0001.mp4"), "x");
            File.WriteAllText(Path.Combine(video, "A11_2222222_222222_20260902180118_0002.mp4"), "x");

            var info = RecordingName.FromCard(card);

            Assert.Equal("2222222", info!.DeviceNo);
        }
        finally
        {
            try { Directory.Delete(card, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Empty_card_yields_nothing()
    {
        var card = Directory.CreateTempSubdirectory("astra-rec-empty-").FullName;
        try
        {
            Assert.Null(RecordingName.FromCard(card));
            Assert.Null(RecordingName.FromCard(null));
        }
        finally
        {
            try { Directory.Delete(card, recursive: true); } catch (IOException) { }
        }
    }
}
