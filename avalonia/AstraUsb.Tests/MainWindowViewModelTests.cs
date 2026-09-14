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

    [AvaloniaFact]
    public async Task A_duplicate_astra_id_never_starts_a_second_backup()
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Directory.CreateDirectory(Path.Combine(_dir, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_dir, "second")).FullName;
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var identified = (System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_identified", fields)!.GetValue(model)!;
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        foreach (var mount in new[] { first, second })
            identified[mount] = Activator.CreateInstance(cardType, [7L, "Astra ID 7", "", "", "", ""]);

        typeof(MainWindowViewModel).GetMethod("Apply", fields)!.Invoke(model,
            [new UsbDevice[] { new("first", first), new("second", second) }, StorageState.Unknown("archive")]);

        var cancels = (Dictionary<string, CancellationTokenSource>)typeof(MainWindowViewModel)
            .GetField("_cancels", fields)!.GetValue(model)!;
        try
        {
            Assert.NotEqual(PortState.Failed, model.Ports[0].State);
            Assert.Equal(PortState.Failed, model.Ports[1].State);
            Assert.Contains("Дубликат Astra ID 7", model.Ports[1].Detail);
            Assert.False(cancels.ContainsKey(second));
        }
        finally
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (cancels.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Empty(cancels);
        }
    }

    [AvaloniaFact]
    public void A_later_duplicate_cannot_take_the_id_of_a_finished_camera()
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Directory.CreateDirectory(Path.Combine(_dir, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_dir, "second")).FullName;
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var identified = (System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_identified", fields)!.GetValue(model)!;
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        foreach (var mount in new[] { first, second })
            identified[mount] = Activator.CreateInstance(cardType, [7L, "Astra ID 7", "", "", "", ""]);
        var finished = (Dictionary<string, BackupStage>)typeof(MainWindowViewModel)
            .GetField("_finished", fields)!.GetValue(model)!;
        finished[first] = BackupStage.Done;
        model.Ports[1].State = PortState.Done;
        // Занятость очереди удерживает ошибочный запуск от фоновой записи в тесте.
        typeof(MainWindowViewModel).GetField("_priority", fields)!.SetValue(model, first);

        typeof(MainWindowViewModel).GetMethod("Apply", fields)!.Invoke(model,
            [new UsbDevice[] { new("second", second), new("first", first) }, StorageState.Unknown("archive")]);

        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("Дубликат Astra ID 7", model.Ports[0].Detail);
        Assert.Equal(PortState.Done, model.Ports[1].State);
    }

    [AvaloniaFact]
    public void A_corrupt_astra_marker_is_shown_as_an_error_without_backup()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "corrupt")).FullName;
        File.WriteAllText(Path.Combine(mount, DeviceRegistry.DeviceIdFile), "broken");
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var card = typeof(MainWindowViewModel).GetMethod("ReadCard", fields)!
            .Invoke(model, ["camera", mount]);
        Assert.NotNull(card);
        var identified = (System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_identified", fields)!.GetValue(model)!;
        identified[mount] = card;

        typeof(MainWindowViewModel).GetMethod("Apply", fields)!.Invoke(model,
            [new UsbDevice[] { new("camera", mount) }, StorageState.Unknown("archive")]);

        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("Некорректный .astra_id", model.Ports[0].Detail);
        Assert.Empty((System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_cancels", fields)!.GetValue(model)!);
        Assert.Equal("broken", File.ReadAllText(Path.Combine(mount, DeviceRegistry.DeviceIdFile)));
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
