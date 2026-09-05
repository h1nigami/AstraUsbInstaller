using AstraUsb.Services;
using AstraUsb.ViewModels;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-mainvm-").FullName;

    public MainWindowViewModelTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaFact]
    public void Uppercase_C_is_typed_without_clearing_the_password()
    {
        using var model = new MainWindowViewModel(() => []);
        model.PasswordInput = "ab";
        model.KeysUpper = true;

        model.PasswordKeyCommand.Execute("C");

        Assert.Equal("abC", model.PasswordInput);
        model.PasswordKeyCommand.Execute("clear");
        Assert.Empty(model.PasswordInput);
    }

    [AvaloniaFact]
    public void An_unavailable_archive_replaces_the_previous_capacity_and_raises_an_alarm()
    {
        using var model = new MainWindowViewModel(() => []);
        ApplyStorage(model, new StorageState(100_000_000_000, 80_000_000_000, "archive", true));
        Assert.True(model.StorageWidth > 0);

        ApplyStorage(model, StorageState.Unknown("archive"));

        Assert.Equal("хранилище недоступно", model.StorageLabel);
        Assert.Equal(0, model.StorageWidth);
        Assert.Contains("не смонтирован", model.Status);

        model.Status = "носители не подключены";
        ApplyStorage(model, StorageState.Unknown("archive"));
        Assert.Contains("не смонтирован", model.Status);
    }

    [AvaloniaFact]
    public async Task A_poll_does_not_reset_charge_only_to_detected()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "card")).FullName;
        var chargeOnly = (HashSet<string>)typeof(MainWindowViewModel)
            .GetField("_chargeOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        chargeOnly.Add(mount);

        typeof(MainWindowViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [new UsbDevice[] { new("test-card", mount) }, StorageState.Unknown("archive")]);

        try { Assert.Equal(PortState.ChargeOnly, model.Ports[0].State); }
        finally
        {
            var identifying = (HashSet<string>)typeof(MainWindowViewModel)
                .GetField("_identifying", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (identifying.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Empty(identifying);
        }
    }

    [AvaloniaFact]
    public async Task Queued_backup_reports_do_not_overwrite_charge_only_after_cancellation()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "card")).FullName;
        var port = new PortViewModel { Slot = 0 };
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var chargeOnly = (HashSet<string>)typeof(MainWindowViewModel).GetField("_chargeOnly", fields)!
            .GetValue(model)!;
        typeof(MainWindowViewModel).GetMethod("StartBackup", fields)!.Invoke(model, [port, 1L, mount]);
        var cancels = (Dictionary<string, CancellationTokenSource>)typeof(MainWindowViewModel)
            .GetField("_cancels", fields)!.GetValue(model)!;
        chargeOnly.Add(mount);
        port.State = PortState.ChargeOnly;
        cancels[mount].Cancel();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (cancels.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Empty(cancels);
        Assert.Equal(PortState.ChargeOnly, port.State);
    }

    private static void ApplyStorage(MainWindowViewModel model, StorageState storage) =>
        typeof(MainWindowViewModel).GetMethod("UpdateStorage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [storage]);

    public void Dispose()
    {
        AppPaths.Root = _root;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
