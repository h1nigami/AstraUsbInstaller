using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class DeviceIdentifierTests : IDisposable
{
    private readonly string _card = Directory.CreateTempSubdirectory("astra-id-").FullName;

    public void Dispose() => Directory.Delete(_card, recursive: true);

    private void Log(string text, string name = "20260915.txt")
    {
        var dir = Path.Combine(_card, "LOG");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), text);
    }

    private void Recording(string id, string stamp = "20260915120000", int sequence = 1)
    {
        var dir = Path.Combine(_card, "DCIM");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,
            $"A11_{id}_222222_{stamp}_{sequence:0000}.mp4"), "video");
    }

    [Fact]
    public void Reader_exists()
    {
        Assert.NotNull(typeof(RecordingName).Assembly.GetType("AstraUsb.Services.DeviceIdentifier"));
    }

    [Fact]
    public void Reads_id_from_latest_log()
    {
        Log("#ID:1111111\n", "20260914.txt");
        Log("2026/09/15 #ID:1234567 #Включение системы\n");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Reads_id_from_latest_recording()
    {
        Recording("1111111", "20260914120000");
        Recording("1234567");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Matching_sources_use_same_id()
    {
        Log("#ID:1234567\n");
        Recording("1234567");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Different_sources_are_rejected()
    {
        Log("#ID:1234567\n");
        Recording("9999999");
        Assert.Throws<InvalidDataException>(() => DeviceIdentifier.Read(_card));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775808")]
    [InlineData("123abc")]
    [InlineData("１２３")]
    public void Invalid_log_id_is_not_used(string value)
    {
        Log($"#ID:{value}\n");
        Assert.Throws<InvalidDataException>(() => DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Old_marker_cannot_replace_missing_id()
    {
        File.WriteAllText(Path.Combine(_card, ".astra_id"), "1234567\n");
        Assert.Throws<InvalidDataException>(() => DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Unreadable_existing_log_aborts_even_when_recording_has_id()
    {
        Log("#ID:1234567\n");
        Recording("1234567");
        var path = Path.Combine(_card, "LOG", "20260915.txt");
        File.WriteAllBytes(path, [0xff, 0xfe]);
        Assert.Throws<IOException>(() => DeviceIdentifier.Read(_card));
    }
}
