using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class DeviceIdentifierTests : IDisposable
{
    private readonly string _card = Directory.CreateTempSubdirectory("astra-id-").FullName;

    public void Dispose() => Directory.Delete(_card, recursive: true);

    private void Log(string text, string name = "20260915.txt", DateTime? modified = null)
    {
        var dir = Path.Combine(_card, "LOG");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modified ?? new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc)
            .AddMinutes(string.CompareOrdinal(name, "20260915.txt")));
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
    public void Latest_log_without_id_falls_back_to_older_log()
    {
        Log("#ID:1111111\n", "20260914.txt");
        Log("Запуск регистратора\n");
        Assert.Equal(1111111, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Latest_log_is_chosen_by_modification_time_not_name()
    {
        Log("#ID:1111111\n", "zzz.txt", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        Log("#ID:1234567\n", "aaa.txt", new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Last_id_in_log_wins()
    {
        Log("#ID:1111111\n" + string.Concat(Enumerable.Repeat("строка\n", 100)) + "#ID:1234567\n");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Recording_without_valid_id_is_skipped()
    {
        Recording("1234567", "20260914120000");
        Recording("0000000");
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
    public void Log_wins_when_sources_disagree()
    {
        Log("#ID:1234567\n");
        Recording("9999999");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
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
    public void Broken_bytes_in_log_do_not_hide_id()
    {
        var dir = Path.Combine(_card, "LOG");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "20260915.txt"),
            [0xff, 0xfe, (byte)'\n', .. System.Text.Encoding.ASCII.GetBytes("#ID:1234567\n")]);
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }

    [Fact]
    public void Log_with_garbage_only_falls_back_to_recording()
    {
        var dir = Path.Combine(_card, "LOG");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "20260915.txt"), [0xff, 0xfe]);
        Recording("1234567");
        Assert.Equal(1234567, DeviceIdentifier.Read(_card));
    }
}
