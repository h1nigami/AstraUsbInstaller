using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-settings-").FullName;
    private readonly string _previous = AppPaths.Root;
    public SettingsTests() => AppPaths.Root = _root;

    [Fact]
    public void Stale_settings_save_only_the_fields_the_operator_changed()
    {
        Assert.True(new Settings { BackupRoot = "first", MinFreeGb = 1 }.Save());
        var storage = Settings.Load();
        var password = Settings.Load();
        storage.BackupRoot = "second";
        Assert.True(storage.Save());
        password.PasswordHash = "new-password-hash";
        Assert.True(password.Save());
        Assert.Equal("second", Settings.Load().BackupRoot);
        Assert.Equal("new-password-hash", Settings.Load().PasswordHash);
    }

    [Fact]
    public void A_config_corrupted_after_loading_is_preserved()
    {
        Assert.True(new Settings { BackupRoot = "archive" }.Save());
        var loaded = Settings.Load();
        File.WriteAllText(Settings.FilePath, "{broken");
        loaded.MinFreeGb = 12;
        Assert.False(loaded.Save());
        Assert.Equal("{broken", File.ReadAllText(Settings.FilePath));
    }

    [Fact]
    public void Valid_JSON_with_a_wrong_setting_type_is_preserved()
    {
        Assert.True(new Settings { BackupRoot = "archive" }.Save());
        var loaded = Settings.Load();
        const string damaged = "{\"MinFreeGb\":\"broken\"}";
        File.WriteAllText(Settings.FilePath, damaged);
        loaded.PasswordHash = "new-password";
        Assert.False(loaded.Save());
        Assert.Equal(damaged, File.ReadAllText(Settings.FilePath));
    }

    [Fact]
    public void Explicit_repair_keeps_a_copy_of_the_corrupted_config()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(Settings.FilePath, "{broken");
        var loaded = Settings.Load();
        loaded.BackupRoot = "chosen archive";
        Assert.True(loaded.Save(replaceUnreadable: true));
        var preserved = Assert.Single(Directory.GetFiles(AppPaths.DataDir, "settings.json.corrupt.*"));
        Assert.Equal("{broken", File.ReadAllText(preserved));
        Assert.Equal("chosen archive", Settings.Load().BackupRoot);
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Directory.Delete(_root, true);
    }
}
