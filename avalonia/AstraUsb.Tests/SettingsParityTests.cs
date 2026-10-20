using System.Text;
using System.Text.Json;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SettingsParityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-settings-parity-").FullName;
    private readonly string _previous = AppPaths.Root;
    private string LogPath => Path.Combine(AppPaths.DataDir, "app.log");

    public SettingsParityTests()
    {
        AppPaths.Root = _root;
        Directory.CreateDirectory(AppPaths.DataDir);
    }

    public static IEnumerable<object[]> InvalidValues()
    {
        yield return ["LockTimeoutMinutes", "{\"LockTimeoutMinutes\":-1}", "не меньше 0"];
        yield return ["KeepDays", "{\"KeepDays\":-1}", "не меньше 0"];
        foreach (var name in new[]
                 {
                     "BackupRoot", "BackupUuid", "BackupSerial", "FtpHost", "FtpUser", "FtpPassword", "FtpFolder",
                     "SqlKind", "SqlHost", "SqlDatabase", "SqlUser", "SqlPassword", "AdminAccount", "PasswordHash", "StationPlace",
                 })
            yield return [name, $"{{\"{name}\":null}}", "строка"];
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void Invalid_values_stop_loading_and_report_the_field_path_and_reason(string field, string json, string reason)
    {
        File.WriteAllText(Settings.FilePath, json);

        var settings = Settings.Load();

        Assert.True(settings.Unreadable);
        Assert.False(settings.Save());
        Assert.Equal(json, File.ReadAllText(Settings.FilePath));
        var message = File.ReadAllText(LogPath);
        Assert.Contains(field, message);
        Assert.Contains(Settings.FilePath, message);
        Assert.Contains(reason, message);
    }

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void A_config_that_became_semantically_invalid_after_loading_is_not_overwritten(string field, string json, string reason)
    {
        Assert.True(new Settings { BackupRoot = "archive" }.Save());
        var settings = Settings.Load();
        File.WriteAllText(Settings.FilePath, json);
        settings.VoiceHints = true;

        Assert.False(settings.Save());

        Assert.Equal(json, File.ReadAllText(Settings.FilePath));
        var message = File.ReadAllText(LogPath);
        Assert.Contains(field, message);
        Assert.Contains(Settings.FilePath, message);
        Assert.Contains(reason, message);
    }

    [Theory]
    [InlineData("{\"LockTimeoutMinutes\":-1}")]
    [InlineData("{\"KeepDays\":-1}")]
    [InlineData("{\"BackupUuid\":null}")]
    public void Invalid_new_settings_are_rejected_before_creating_the_file(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json)!;

        Assert.False(settings.Save());

        Assert.False(File.Exists(Settings.FilePath));
    }

    [Fact]
    public void Zero_timeouts_empty_strings_and_a_null_relative_path_remain_valid()
    {
        File.WriteAllText(Settings.FilePath,
            "{\"LockTimeoutMinutes\":0,\"KeepDays\":0,\"BackupUuid\":\"\",\"BackupSerial\":\"\",\"BackupRelativePath\":null}");

        var settings = Settings.Load();

        Assert.False(settings.Unreadable);
        Assert.Equal(0, settings.LockTimeoutMinutes);
        Assert.Equal(0, settings.KeepDays);
        Assert.Null(settings.BackupRelativePath);
        Assert.True(settings.Save());
        Assert.False(Settings.Load().Unreadable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_repeated_failure_is_reported_again_after_repair_or_file_removal(bool remove)
    {
        const string invalid = "{\"LockTimeoutMinutes\":-1}";
        File.WriteAllText(Settings.FilePath, invalid);
        Assert.True(Settings.Load().Unreadable);
        Assert.True(Settings.Load().Unreadable);
        Assert.Equal(1, File.ReadAllLines(LogPath).Count(line => line.Contains("LockTimeoutMinutes")));

        if (remove) File.Delete(Settings.FilePath);
        else File.WriteAllText(Settings.FilePath, "{\"LockTimeoutMinutes\":0}");
        Assert.False(Settings.Load().Unreadable);
        File.WriteAllText(Settings.FilePath, invalid);
        Assert.True(Settings.Load().Unreadable);

        Assert.Equal(2, File.ReadAllLines(LogPath).Count(line => line.Contains("LockTimeoutMinutes")));
    }

    [Theory]
    [InlineData("{\"LockTimeoutMinutes\":-1}")]
    [InlineData("{\"BackupUuid\":null}")]
    public void Explicit_repair_preserves_the_invalid_file_and_writes_valid_settings(string invalid)
    {
        File.WriteAllText(Settings.FilePath, invalid);
        var settings = Settings.Load();
        Assert.True(settings.Unreadable);
        settings.BackupRoot = Path.Combine(_root, "chosen-archive");

        Assert.True(settings.Save(replaceUnreadable: true));

        Assert.False(settings.Unreadable);
        Assert.False(Settings.Load().Unreadable);
        var preserved = Assert.Single(Directory.GetFiles(AppPaths.DataDir, "settings.json.corrupt.*"));
        Assert.Equal(invalid, File.ReadAllText(preserved));
    }

    [Fact]
    public void Explicit_repair_from_a_stale_snapshot_keeps_its_unchanged_password_hash()
    {
        Assert.True(new Settings { BackupRoot = "archive", PasswordHash = "known-password-hash" }.Save());
        var settings = Settings.Load();
        File.WriteAllText(Settings.FilePath, "{\"KeepDays\":-1}");
        settings.BackupRoot = "chosen archive";

        Assert.True(settings.Save(replaceUnreadable: true));

        var repaired = Settings.Load();
        Assert.Equal("known-password-hash", repaired.PasswordHash);
        Assert.Equal("chosen archive", repaired.BackupRoot);
        Assert.False(repaired.Unreadable);
    }

    [Fact]
    public void A_stale_snapshot_keeps_unchanged_fields_when_the_settings_file_was_removed()
    {
        Assert.True(new Settings { BackupRoot = "archive", PasswordHash = "known-password-hash" }.Save());
        var settings = Settings.Load();
        File.Delete(Settings.FilePath);
        settings.VoiceHints = true;

        Assert.True(settings.Save());

        var restored = Settings.Load();
        Assert.Equal("known-password-hash", restored.PasswordHash);
        Assert.Equal("archive", restored.BackupRoot);
        Assert.True(restored.VoiceHints);
    }

    [Fact]
    public void A_successful_explicit_repair_resets_the_read_failure_suppression()
    {
        const string invalid = "{broken";
        File.WriteAllText(Settings.FilePath, invalid);
        var settings = Settings.Load();
        Assert.True(settings.Unreadable);
        Assert.True(settings.Save(replaceUnreadable: true));
        File.WriteAllText(Settings.FilePath, invalid);

        Assert.True(Settings.Load().Unreadable);

        Assert.Equal(2, File.ReadAllLines(LogPath).Count(line => line.Contains("Не удалось прочитать настройки")));
    }

    [Fact]
    public void A_null_json_document_is_reported_as_an_unreadable_object()
    {
        File.WriteAllText(Settings.FilePath, "null");

        Assert.True(Settings.Load().Unreadable);

        Assert.True(File.Exists(LogPath));
        Assert.Contains("объект JSON", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Invalid_utf8_cannot_be_loaded_or_replaced_by_a_stale_save()
    {
        Assert.True(new Settings { BackupRoot = "archive" }.Save());
        var stale = Settings.Load();
        byte[] invalid = [.. Encoding.UTF8.GetBytes("{\"BackupUuid\":\""), 0xff, .. Encoding.UTF8.GetBytes("\"}")];
        File.WriteAllBytes(Settings.FilePath, invalid);

        Assert.True(Settings.Load().Unreadable);
        Assert.False(stale.Save());

        Assert.Equal(invalid, File.ReadAllBytes(Settings.FilePath));
        Assert.Contains(Settings.FilePath, File.ReadAllText(LogPath));
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Directory.Delete(_root, recursive: true);
    }
}
