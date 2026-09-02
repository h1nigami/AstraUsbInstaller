namespace AstraUsb.Services;

/// <summary>
/// Запись падений в файл рядом с данными станции.
///
/// Станция работает в киоске: окно занимает весь экран, консоли нет, и
/// упавшая программа не оставляет следа, кроме исчезнувшего окна. Под systemd
/// вывод попадает в журнал службы, но добраться до него можно только с
/// клавиатуры и с правами, а файл рядом с базой видно сразу.
/// </summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(AppPaths.DataDir, "crash.log");

    /// <summary>Ставит перехват на всё, что иначе завершило бы программу молча.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("необработанное исключение", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("исключение в фоновой задаче", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Записывает происшествие. Сама запись упасть не должна.</summary>
    public static void Write(string what, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            var text = $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}
                {error}

                """;

            File.AppendAllText(FilePath, text);
        }
        catch (Exception)
        {
            // Некуда писать: диск полон или каталог только для чтения.
        }
    }
}
