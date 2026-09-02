using System.Diagnostics;

namespace AstraUsb.Services;

/// <summary>
/// Голосовые подсказки станции.
///
/// Оператор ставит регистратор и уходит, а к станции возвращается через
/// несколько минут; фраза «отсек три, можно забирать» слышна от двери, тогда
/// как цвет окна надо разглядеть.
///
/// Синтез берётся системный: на Astra это speech-dispatcher, который в
/// дистрибутиве есть, на Windows встроенный синтез. Своего голоса станция не
/// несёт: записанные фразы пришлось бы обновлять при каждом изменении текста,
/// а модель синтеза весит больше самой программы.
///
/// Если синтеза в системе нет, подсказки просто молчат: станция от них не
/// зависит, и это не повод останавливать сбор.
/// </summary>
public static class Voice
{
    /// <summary>Не чаще одной фразы в три секунды: иначе они наступают друг на друга.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    private static DateTime _last = DateTime.MinValue;
    private static bool? _available;

    /// <summary>Есть ли в системе синтез речи.</summary>
    public static bool Available()
    {
        _available ??= Probe();
        return _available.Value;
    }

    /// <summary>Произносит фразу, если синтез есть и не занят прошлой.</summary>
    public static void Say(string text, DateTime now)
    {
        if (text.Length == 0 || !Available())
            return;

        if (now - _last < Pause)
            return;

        _last = now;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Кавычки в тексте сломали бы команду, поэтому убираем их.
                var safe = text.Replace("'", "").Replace("\"", "");
                Start("powershell", "-NoProfile", "-Command",
                    $"Add-Type -AssemblyName System.Speech; "
                    + $"(New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{safe}')");
                return;
            }

            Start("spd-say", "-l", "ru", "-w", text);
        }
        catch (Exception)
        {
            // Синтез отвалился: станция говорить перестанет, работать нет.
        }
    }

    private static bool Probe()
    {
        if (OperatingSystem.IsWindows())
            return true;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("spd-say", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (proc is null)
                return false;

            proc.StandardOutput.ReadToEnd();
            return proc.WaitForExit(3000);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Start(string program, params string[] arguments)
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
    }
}
