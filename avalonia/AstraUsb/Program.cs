using Avalonia;

namespace AstraUsb;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Киоск занимает весь экран, консоли у него нет, и упавшая программа
        // не оставляет следа, кроме исчезнувшего окна. Поэтому падения
        // пишутся в файл рядом с базой станции.
        Services.CrashLog.Install();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Services.CrashLog.Write("программа завершилась с ошибкой", e);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
