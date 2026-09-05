using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-settingsvm-").FullName;

    public SettingsViewModelTests() => AppPaths.Root = _dir;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unwritable_archive_path_does_not_replace_the_working_settings(bool empty)
    {
        var original = Path.Combine(_dir, "archive");
        Assert.True(new Settings { BackupRoot = original }.Save());
        var model = new SettingsViewModel(AppPaths.Database);
        var file = Path.Combine(_dir, "occupied");
        File.WriteAllText(file, "занято");
        model.BackupRoot = empty ? "" : file;

        model.SaveStorageCommand.Execute(null);
        model.SaveLockTimeoutCommand.Execute(null);

        Assert.Equal(original, Settings.Load().BackupRoot);
    }

    [Fact]
    public void A_failed_password_save_can_be_retried_with_the_current_password()
    {
        var model = new SettingsViewModel(AppPaths.Database);
        Directory.CreateDirectory(Settings.FilePath);
        model.CurrentPassword = PasswordGate.Default();
        model.NewPassword = model.RepeatPassword = "first-password";
        model.ChangePasswordCommand.Execute(null);
        Directory.Delete(Settings.FilePath);
        model.CurrentPassword = PasswordGate.Default();
        model.NewPassword = model.RepeatPassword = "second-password";

        model.ChangePasswordCommand.Execute(null);

        Assert.True(PasswordGate.Matches(Settings.Load().PasswordHash, "second-password"));
    }

    [Fact]
    public void Retrying_ftp_with_a_broken_database_reports_a_safe_message_and_logs_details()
    {
        var model = new SettingsViewModel(AppPaths.Database);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.Database}"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        File.WriteAllText(AppPaths.Database, "сломанная база");

        model.RetryFtpCommand.Execute(null);

        Assert.DoesNotContain("SQLite", model.FtpState);
        Assert.Contains("Не удалось", model.FtpState);
        Assert.Contains("SqliteException", File.ReadAllText(CrashLog.FilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Slot_buttons_report_database_failure_without_throwing(bool clear)
    {
        var model = new SettingsViewModel(AppPaths.Database);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.Database}"))
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        File.WriteAllText(AppPaths.Database, "сломанная база");

        var error = Record.Exception(() =>
        {
            if (clear) model.ClearSlotsCommand.Execute(null);
            else model.ReloadSlotsCommand.Execute(null);
        });

        Assert.Null(error);
        Assert.Contains("Не удалось", model.Hint);
        Assert.DoesNotContain("SQLite", model.Hint);
    }

    [Fact]
    public void A_malformed_ftp_address_is_reported_without_system_text()
    {
        var result = FtpSender.Test(new Settings { FtpHost = "[invalid" });

        Assert.False(result.Ok);
        Assert.Contains("Не удалось", result.Message);
        Assert.DoesNotContain("URI", result.Message);
        Assert.Contains("UriFormatException", File.ReadAllText(CrashLog.FilePath));
    }

    [Fact]
    public async Task An_invalid_sql_probe_argument_is_reported_without_system_text()
    {
        var result = await SqlProbe.CheckAsync("127.0.0.1", 3306, TimeSpan.FromSeconds(-2));

        Assert.Contains("Не удалось", result);
        Assert.DoesNotContain("Parameter", result);
        Assert.Contains("ArgumentOutOfRangeException", File.ReadAllText(CrashLog.FilePath));
    }

    [Fact]
    public async Task A_database_server_error_is_logged_without_showing_protocol_text()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var payload = new byte[] { 0xff, 0x28, 0x04 }
                .Concat(System.Text.Encoding.UTF8.GetBytes("#HY000 INTERNAL_DB_DETAILS")).ToArray();
            var packet = new byte[] { (byte)payload.Length, 0, 0, 0 }.Concat(payload).ToArray();
            await client.GetStream().WriteAsync(packet, timeout.Token);
        });
        var external = new ExternalDatabase(new Settings
        {
            SqlHost = "127.0.0.1",
            SqlPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
            SqlDatabase = "test",
        });

        var result = await external.CheckAsync(timeout.Token);
        await reply;

        Assert.False(result.Ok);
        Assert.DoesNotContain("INTERNAL_DB_DETAILS", result.Message);
        Assert.Contains("Не удалось", result.Message);
        Assert.Contains("INTERNAL_DB_DETAILS", File.ReadAllText(CrashLog.FilePath));
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MSSQL")]
    public async Task A_legacy_provider_is_reported_and_not_silently_replaced(string kind)
    {
        Assert.True(new Settings { SqlKind = kind }.Save());
        var model = new SettingsViewModel(AppPaths.Database);

        Assert.Equal(new[] { "MySQL" }, model.SqlKinds);
        Assert.Equal(-1, model.SqlKindIndex);
        Assert.Contains(kind, model.SqlState);
        Assert.Equal(kind, Settings.Load().SqlKind);

        await model.TestSqlCommand.ExecuteAsync(null);

        Assert.Contains(kind, model.SqlState);
        Assert.Contains("MySQL", model.SqlState);
        model.SaveSqlCommand.Execute(null);
        Assert.Equal(kind, Settings.Load().SqlKind);

        model.SqlKindIndex = 0;
        Assert.Empty(model.SqlState);
        model.SaveSqlCommand.Execute(null);
        Assert.Equal("MySQL", Settings.Load().SqlKind);
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
