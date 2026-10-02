using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-diagnostics-").FullName;
    private readonly string _previous = AppPaths.Root;

    public DiagnosticsTests() => AppPaths.Root = _root;

    [Fact]
    public void The_bundle_contains_version_events_and_summary_without_credentials()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.VersionFile, "v2.0.5 2026-09-15");
        var settings = new Settings { FtpPassword = "ftp-secret", SqlPassword = "sql-secret",
            PasswordHash = "password-hash" };
        Assert.True(settings.Save());
        new ActionLog(AppPaths.Database).Write(ActionLog.Backup, "сохранено Device1; ftp-secret");
        File.WriteAllText(CrashLog.FilePath, "sql-secret PasswordHash=password-hash token=opaque-secret");
        var destination = Path.Combine(_root, "export");

        var bundle = Diagnostics.Export(destination);

        Assert.Contains("2.0.5", File.ReadAllText(Path.Combine(bundle, "version.txt")));
        Assert.Contains("Device1", File.ReadAllText(Path.Combine(bundle, "app.log")));
        Assert.Contains("событий", File.ReadAllText(Path.Combine(bundle, "summary.txt")));
        var all = string.Join("\n", Directory.GetFiles(bundle).Select(File.ReadAllText));
        foreach (var secret in new[] { "ftp-secret", "sql-secret", "password-hash", "opaque-secret" })
            Assert.DoesNotContain(secret, all);
        Assert.False(File.Exists(Path.Combine(bundle, "settings.json")));
    }

    [Fact]
    public void A_broken_database_still_allows_exporting_the_crash_log()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.Database, "broken database");
        File.WriteAllText(CrashLog.FilePath, "ошибка запуска");

        var bundle = Diagnostics.Export(Path.Combine(_root, "export"));

        Assert.Contains("ошибка запуска", File.ReadAllText(Path.Combine(bundle, "app.log")));
        Assert.Contains("недоступна", File.ReadAllText(Path.Combine(bundle, "summary.txt")));
    }

    [Fact]
    public void Quoted_old_credentials_are_hidden_when_settings_are_unreadable()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(Settings.FilePath, "{broken");
        File.WriteAllText(CrashLog.FilePath,
            "{\"password\":\"previous-password\",\"token\":\"previous-token\"}\n"
            + "Password=\"previous password\"\nобычная ошибка");

        var bundle = Diagnostics.Export(Path.Combine(_root, "export"));
        var text = File.ReadAllText(Path.Combine(bundle, "app.log"));

        Assert.DoesNotContain("previous", text);
        Assert.Contains("обычная ошибка", text);
    }

    [Fact]
    public void Diagnostics_waits_for_exclusive_station_maintenance()
    {
        using var maintenance = OperationGuard.Acquire(exclusive: true);
        var destination = Path.Combine(_root, "export");

        Assert.Throws<StationBusyException>(() => Diagnostics.Export(destination));
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
