#if UPDATER_INTEGRATION
namespace AstraUsb.Services;

public static class StationLog
{
    public static void Write(string message) => Console.WriteLine(message);

    public static void Once(string key, string? message)
    {
        if (message is not null)
            Write(message);
    }
}
#endif
