using System.Diagnostics;

namespace AstraUsb.Services;

/// <summary>
/// Звуковая тревога станции.
///
/// Задание требует звук при обрыве сети и нехватке места: экран стоит в стороне,
/// и цветная полоса внизу может остаться незамеченной до конца смены.
///
/// Сигнал не берётся из файла в поставке и не требует библиотеки вывода: он
/// собирается в WAV один раз при первом обращении и играется тем, что есть в
/// системе. Своего вывода звука у Avalonia нет, а тащить в сборку звуковой
/// движок ради трёх гудков неразумно.
/// </summary>
public static class Alarm
{
    /// <summary>Не чаще одного сигнала в минуту: иначе он превращается в шум.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    private static DateTime _last = DateTime.MinValue;

    public static string FilePath => Path.Combine(AppPaths.DataDir, "alarm.wav");

    /// <summary>
    /// Подаёт сигнал, если с прошлого прошла минута. Ошибки проглатываются:
    /// станция без звука работает, а без сбора нет.
    /// </summary>
    public static void Sound(DateTime now)
    {
        if (now - _last < Pause)
            return;

        _last = now;

        try
        {
            EnsureFile();
            Play(FilePath);
        }
        catch (Exception)
        {
            // Нет звуковой карты, нет проигрывателя, нет прав: не беда.
        }
    }

    /// <summary>Собирает файл сигнала, если его ещё нет.</summary>
    public static void EnsureFile()
    {
        if (File.Exists(FilePath))
            return;

        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllBytes(FilePath, Build());
    }

    /// <summary>
    /// Три коротких гудка. Три, а не один: одиночный звук в помещении со
    /// станциями теряется среди прочих.
    /// </summary>
    private static byte[] Build()
    {
        const int rate = 22050;
        const double tone = 880;
        const int beepMs = 140;
        const int gapMs = 90;
        const int beeps = 3;

        var samples = new List<short>();

        for (var i = 0; i < beeps; i++)
        {
            var count = rate * beepMs / 1000;
            for (var n = 0; n < count; n++)
            {
                // Края гудка приглушаются, иначе в динамике слышен щелчок.
                var fade = Math.Min(1.0, Math.Min(n, count - n) / (rate * 0.01));
                var value = Math.Sin(2 * Math.PI * tone * n / rate) * fade * 0.5;
                samples.Add((short)(value * short.MaxValue));
            }

            if (i < beeps - 1)
                samples.AddRange(new short[rate * gapMs / 1000]);
        }

        var data = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(data, i * 2);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1);        // без сжатия
        writer.Write((short)1);        // один канал
        writer.Write(rate);
        writer.Write(rate * 2);        // байт в секунду
        writer.Write((short)2);        // байт на кадр
        writer.Write((short)16);       // бит на отсчёт
        writer.Write("data".ToCharArray());
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>Играет файл тем, что есть в системе.</summary>
    private static void Play(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Start("powershell", "-NoProfile", "-Command",
                $"(New-Object Media.SoundPlayer '{path}').PlaySync()");
            return;
        }

        // На Astra Linux обычно есть aplay из alsa-utils, иначе paplay.
        if (!Start("aplay", "-q", path))
            Start("paplay", path);
    }

    private static bool Start(string program, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var proc = Process.Start(info);
            return proc is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
