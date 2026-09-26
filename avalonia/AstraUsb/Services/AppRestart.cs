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
            try
            {
                Process.Start(new ProcessStartInfo(self) { UseShellExecute = false });
            }
            catch (Exception)
            {
                // Не вышло: оператор запустит программу вручную.
            }
        }
        Environment.Exit(0);
    }
}
