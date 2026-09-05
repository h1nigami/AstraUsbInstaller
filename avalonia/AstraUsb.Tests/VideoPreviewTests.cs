using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Просмотр видео на станции. Своего проигрывателя в сборке нет, поэтому
/// запись показывается кадрами по временной шкале, а разбор длительности и
/// подписи времени проверяются здесь: с ними шкала либо врёт, либо пустует.
/// </summary>
public sealed class VideoPreviewTests
{
    [Fact]
    public void The_length_is_read_from_the_probe_output()
    {
        var length = VideoPreview.ParseDuration("12.345000\n");

        Assert.NotNull(length);
        Assert.Equal(12.345, length!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void A_dot_is_the_separator_whatever_the_system_locale_says()
    {
        // ffprobe всегда пишет точку, а на станции запятая: разбор по
        // настройкам системы дал бы длительность в тысячу раз больше.
        var length = VideoPreview.ParseDuration("0.5");

        Assert.NotNull(length);
        Assert.Equal(500, length!.Value.TotalMilliseconds);
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("это не число")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    public void An_unknown_length_is_not_a_length(string output)
    {
        Assert.Null(VideoPreview.ParseDuration(output));
    }

    [Fact]
    public void Short_records_are_labelled_by_minutes_and_seconds()
    {
        Assert.Equal("00:12", VideoPreview.Label(TimeSpan.FromSeconds(12)));
        Assert.Equal("02:05", VideoPreview.Label(TimeSpan.FromSeconds(125)));
    }

    [Fact]
    public void Long_records_get_their_hours()
    {
        Assert.Equal("1:02:03", VideoPreview.Label(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void A_missing_file_gives_no_frame_and_starts_nothing()
    {
        var frame = VideoPreview.Frame(
            Path.Combine(Path.GetTempPath(), "нет-такой-записи.mp4"), TimeSpan.Zero);

        Assert.Null(frame);
    }
}
