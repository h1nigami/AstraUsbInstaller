using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Звуковая тревога. Сигнал собирается сам, без файла в поставке, поэтому
/// проверяется, что получается настоящий WAV, а не набор байтов.
/// </summary>
[Collection("Каталог данных")]
public sealed class AlarmTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-alarm-").FullName;
    private readonly string _root;

    public AlarmTests()
    {
        _root = AppPaths.Root;
        AppPaths.Root = _dir;
    }

    [Fact]
    public void The_signal_file_is_built_once_and_reused()
    {
        Alarm.EnsureFile();

        Assert.True(File.Exists(Alarm.FilePath));
        var stamp = File.GetLastWriteTimeUtc(Alarm.FilePath);

        Alarm.EnsureFile();

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Alarm.FilePath));
    }

    [Fact]
    public void The_signal_is_a_real_wav_file()
    {
        Alarm.EnsureFile();
        var bytes = File.ReadAllBytes(Alarm.FilePath);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));

        // Длина в заголовке должна совпадать с настоящей, иначе проигрыватель
        // либо оборвёт сигнал, либо доиграет мусор.
        var declared = BitConverter.ToInt32(bytes, 4);
        Assert.Equal(bytes.Length - 8, declared);
    }

    [Fact]
    public void The_signal_lasts_about_half_a_second()
    {
        Alarm.EnsureFile();
        var bytes = File.ReadAllBytes(Alarm.FilePath);

        var dataSize = BitConverter.ToInt32(bytes, 40);
        var seconds = dataSize / 2.0 / 22050;

        Assert.InRange(seconds, 0.4, 1.2);
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
