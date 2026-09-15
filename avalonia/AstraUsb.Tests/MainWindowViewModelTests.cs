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
    public void Connected_device_shows_only_its_firmware_id()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Path.Combine(_dir, "firmware-card");
        var dcim = Directory.CreateDirectory(Path.Combine(mount, "DCIM")).FullName;
        File.WriteAllText(Path.Combine(dcim,
            "A11_1234567_222222_20260915120000_0001.mp4"), "video");
        var card = typeof(MainWindowViewModel).GetMethod("ReadCard", PrivateFields)!
            .Invoke(model, ["File-Stor Gadget", mount])!;
        var cardType = card.GetType();
        Assert.Equal(1234567L, cardType.GetProperty("DeviceId")!.GetValue(card));
        Assert.Equal("1234567", cardType.GetProperty("CameraId")!.GetValue(card));
        Assert.Equal("Папка Device1234567", cardType.GetProperty("Origin")!.GetValue(card));
        Assert.False(File.Exists(Path.Combine(mount, ".astra_id")));
        Assert.False(File.Exists(Path.Combine(mount, ".bestcam_id")));
        Field<System.Collections.IDictionary>(model, "_identified")[mount] = card;
        typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");

        ApplyDevices(model, new UsbDevice("File-Stor Gadget", mount));

        Assert.Equal("1234567", model.Ports[0].CameraId);
        using var registry = new DeviceRegistry(AppPaths.Database);
        Assert.True(registry.DeviceExists(1234567));
    }

    [AvaloniaFact]
    public async Task Before_id_is_read_the_port_does_not_show_usb_name()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "identifying-card")).FullName;

        ApplyDevices(model, new UsbDevice("File-Stor Gadget", mount));

        Assert.Equal("", model.Ports[0].CameraId);
        Assert.Equal("Определение ID", model.Ports[0].Detail);
        Assert.Equal("Определение ID", model.Ports[0].StateText);
        Assert.Equal("", model.Ports[0].CameraLine);
        var identifying = Field<HashSet<string>>(model, "_identifying");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (identifying.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Empty(identifying);
    }

    [AvaloniaFact]
    public async Task A_duplicate_device_id_never_starts_a_second_backup()
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Directory.CreateDirectory(Path.Combine(_dir, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_dir, "second")).FullName;
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var identified = (System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_identified", fields)!.GetValue(model)!;
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        foreach (var mount in new[] { first, second })
            identified[mount] = Activator.CreateInstance(cardType, [7L, "7", "", "", "", ""]);

        typeof(MainWindowViewModel).GetMethod("Apply", fields)!.Invoke(model,
            [new UsbDevice[] { new("first", first), new("second", second) }, StorageState.Unknown("archive")]);

        var cancels = (Dictionary<string, CancellationTokenSource>)typeof(MainWindowViewModel)
            .GetField("_cancels", fields)!.GetValue(model)!;
        try
        {
            Assert.NotEqual(PortState.Failed, model.Ports[0].State);
            Assert.Equal(PortState.Failed, model.Ports[1].State);
            Assert.Contains("Дубликат ID устройства 7", model.Ports[1].Detail);
            Assert.Equal("7", model.Ports[0].CameraId);
            Assert.Equal("7", model.Ports[1].CameraId);
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
            identified[mount] = Activator.CreateInstance(cardType, [7L, "7", "", "", "", ""]);
        var finished = (Dictionary<string, BackupStage>)typeof(MainWindowViewModel)
            .GetField("_finished", fields)!.GetValue(model)!;
        finished[first] = BackupStage.Done;
        ApplyDevices(model, new UsbDevice("first", first));
        CacheCard(model, second);
        model.Ports[1].State = PortState.Done;
        // Занятость очереди удерживает ошибочный запуск от фоновой записи в тесте.
        typeof(MainWindowViewModel).GetField("_priority", fields)!.SetValue(model, first);

        typeof(MainWindowViewModel).GetMethod("Apply", fields)!.Invoke(model,
            [new UsbDevice[] { new("second", second), new("first", first) }, StorageState.Unknown("archive")]);

        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("Дубликат ID устройства 7", model.Ports[0].Detail);
        Assert.Equal(PortState.Done, model.Ports[1].State);
    }

    [AvaloniaFact]
    public void Missing_device_id_is_shown_without_name_or_backup()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "corrupt")).FullName;
        File.WriteAllText(Path.Combine(mount, ".astra_id"), "broken");
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
        Assert.Contains("ID регистратора не найден", model.Ports[0].Detail);
        Assert.Equal("", model.Ports[0].CameraId);
        Assert.Empty((System.Collections.IDictionary)typeof(MainWindowViewModel)
            .GetField("_cancels", fields)!.GetValue(model)!);
        Assert.Equal("broken", File.ReadAllText(Path.Combine(mount, ".astra_id")));
    }

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_connected_camera_keeps_its_id_while_charging_or_queued(bool charging, bool duplicateFirst)
    {
        using var model = new MainWindowViewModel(() => []);
        var owner = Directory.CreateDirectory(Path.Combine(_dir, "owner")).FullName;
        var duplicate = Directory.CreateDirectory(Path.Combine(_dir, "duplicate")).FullName;
        CacheCard(model, owner);
        if (charging)
            Field<HashSet<string>>(model, "_chargeOnly").Add(owner);
        else
            typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");
        ApplyDevices(model, new UsbDevice("owner", owner));
        CacheCard(model, duplicate);
        if (!charging)
            typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");
        var found = new UsbDevice[] { new("owner", owner), new("duplicate", duplicate) };
        if (duplicateFirst)
            Array.Reverse(found);
        ApplyDevices(model, found);

        var cancels = Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels");
        try
        {
            Assert.Equal(PortState.Failed, model.Ports[duplicateFirst ? 0 : 1].State);
            Assert.Equal(charging ? PortState.ChargeOnly : PortState.Detected,
                model.Ports[duplicateFirst ? 1 : 0].State);
            Assert.False(cancels.ContainsKey(duplicate));
        }
        finally
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (cancels.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Empty(cancels);
        }
    }

    [AvaloniaTheory]
    [InlineData("charge")]
    [InlineData("resume")]
    [InlineData("priority")]
    [InlineData("remote-priority")]
    public void Modal_commands_target_the_selected_mount_even_when_camera_labels_match(string command)
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Directory.CreateDirectory(Path.Combine(_dir, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_dir, "second")).FullName;
        CacheCard(model, first);
        CacheCard(model, second);
        typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");
        ApplyDevices(model, new("first", first), new("second", second));
        Assert.Equal(model.Ports[0].CameraId, model.Ports[1].CameraId);
        var charging = Field<HashSet<string>>(model, "_chargeOnly");
        if (command != "charge")
            charging.UnionWith([first, second]);
        using var firstCancel = new CancellationTokenSource();
        using var secondCancel = new CancellationTokenSource();
        var cancels = Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels");
        cancels[first] = firstCancel;
        cancels[second] = secondCancel;

        model.OpenBayCommand.Execute(model.Ports[1]);
        model.BayConfirm = command;
        if (command == "charge")
        {
            model.ChargeOnlyBayCommand.Execute(null);
            Assert.Contains(second, charging);
            Assert.DoesNotContain(first, charging);
            Assert.True(secondCancel.IsCancellationRequested);
            Assert.False(firstCancel.IsCancellationRequested);
        }
        else if (command == "resume")
        {
            model.ResumeBayCommand.Execute(null);
            Assert.Contains(first, charging);
            Assert.DoesNotContain(second, charging);
        }
        else
        {
            Prioritize(model, command == "remote-priority");
            Assert.Null(Field<string?>(model, "_priority"));
            Assert.False(firstCancel.IsCancellationRequested);
            Assert.Contains(first, charging);
            Assert.Contains(second, charging);
        }
        cancels.Clear();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prioritizing_a_duplicate_does_not_block_the_next_poll_or_queued_cameras(bool remote)
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Directory.CreateDirectory(Path.Combine(_dir, "first")).FullName;
        var duplicate = Directory.CreateDirectory(Path.Combine(_dir, "duplicate")).FullName;
        var next = Directory.CreateDirectory(Path.Combine(_dir, "next")).FullName;
        CacheCard(model, first);
        CacheCard(model, duplicate);
        typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");
        ApplyDevices(model, new("first", first), new("duplicate", duplicate));
        model.OpenBayCommand.Execute(model.Ports[1]);
        model.BayConfirm = "priority";

        Prioritize(model, remote);
        CacheCard(model, next, 8);
        ApplyDevices(model, new("first", first), new("duplicate", duplicate), new("next", next));

        var cancels = Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels");
        try
        {
            Assert.True(cancels.ContainsKey(first));
            Assert.True(cancels.ContainsKey(next));
            Assert.False(cancels.ContainsKey(duplicate));
            Assert.Equal(PortState.Failed, model.Ports[1].State);
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
    public void A_failed_owner_can_still_be_prioritized_for_retry()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "failed")).FullName;
        CacheCard(model, mount);
        var finished = Field<Dictionary<string, BackupStage>>(model, "_finished");
        finished[mount] = BackupStage.Failed;
        ApplyDevices(model, new UsbDevice("failed", mount));
        model.Ports[0].State = PortState.Failed;
        model.OpenBayCommand.Execute(model.Ports[0]);
        model.BayConfirm = "priority";

        model.PrioritizeBayCommand.Execute(null);

        Assert.Equal(mount, Field<string>(model, "_priority"));
        Assert.False(finished.ContainsKey(mount));
    }

    private const BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Prioritize(MainWindowViewModel model, bool remote)
    {
        if (!remote)
            model.PrioritizeBayCommand.Execute(null);
        else
        {
            StationCommands.Request(StationAction.Prioritize, model.Bay!.Slot);
            typeof(MainWindowViewModel).GetMethod("ApplyRemoteCommands", PrivateFields)!.Invoke(model, null);
        }
    }

    private static T Field<T>(MainWindowViewModel model, string name) =>
        (T)typeof(MainWindowViewModel).GetField(name, PrivateFields)!.GetValue(model)!;

    private static void CacheCard(MainWindowViewModel model, string mount, long id = 7)
    {
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        Field<System.Collections.IDictionary>(model, "_identified")[mount] =
            Activator.CreateInstance(cardType, [id, id.ToString(), "", "", "", ""]);
    }

    private static void ApplyDevices(MainWindowViewModel model, params UsbDevice[] found) =>
        typeof(MainWindowViewModel).GetMethod("Apply", PrivateFields)!.Invoke(model,
            [found, StorageState.Unknown("archive")]);

    public void Dispose()
    {
        AppPaths.Root = _root;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
