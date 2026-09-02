using Avalonia;

namespace AstraUsb;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Служба обновления запускает эту же программу с ключом: отдельный
        // самодостаточный бинарник весил бы столько же, сколько сама станция.
        if (args.Contains("--update"))
        {
            Environment.Exit(Services.Updater.Run());
            return;
        }

        // Версия печатается для проверки свежей сборки перед подменой рабочей.
        if (args.Contains("--version"))
        {
            var tag = Services.Updater.InstalledTag();
            Console.WriteLine(tag.Length > 0 ? tag : "версия неизвестна");
            return;
        }

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
