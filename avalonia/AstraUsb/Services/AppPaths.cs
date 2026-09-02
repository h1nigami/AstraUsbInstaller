namespace AstraUsb.Services;

/// <summary>
/// Где приложение хранит своё. Раскладка повторяет Python-версию: база и
/// настройки в data рядом с программой, копии — в USB_Backups. Так одна и та
/// же станция может работать любой из версий, не теряя накопленного.
/// </summary>
public static class AppPaths
{
    public static string Root { get; set; } = AppContext.BaseDirectory;

    public static string DataDir => Path.Combine(Root, "data");

    public static string Database => Path.Combine(DataDir, "devices.db");

    public static string BackupsRoot => Path.Combine(Root, "USB_Backups");

    public static string VersionFile => Path.Combine(Root, "VERSION");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupsRoot);
    }
}
