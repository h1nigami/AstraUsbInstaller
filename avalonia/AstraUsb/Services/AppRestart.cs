using System.Diagnostics;

namespace AstraUsb.Services;

/// <summary>Перезапуск программы после заводского сброса.</summary>
public static class AppRestart
{
    public static void Now()
    {
        // Под systemd ненулевой код виден службе как сбой, и она поднимает
        // станцию заново (Restart=on-failure), как при перезапуске из панели.
        if (OperatingSystem.IsLinux()
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            Environment.Exit(3);

        // Без службы (Windows, ручной запуск) поднимаем себя сами.
        if (Environment.ProcessPath is { Length: > 0 } self)
        {
            if (TryStart(new ProcessStartInfo(self) { UseShellExecute = false }))
                Environment.Exit(0);
        }
        else CrashLog.Write("Не удалось перезапустить программу: путь процесса неизвестен", null);
    }

    internal static bool TryStart(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            if (process is not null) return true;
            CrashLog.Write($"Не удалось перезапустить программу {info.FileName}: процесс не создан", null);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            CrashLog.Write($"Не удалось перезапустить программу {info.FileName}", error);
        }
        return false;
    }
}
