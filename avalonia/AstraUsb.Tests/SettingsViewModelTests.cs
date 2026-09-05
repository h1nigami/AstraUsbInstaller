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
