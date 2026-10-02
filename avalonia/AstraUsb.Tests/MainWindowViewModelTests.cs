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
        var mount = Path.Combine(_dir, "firmware-card");
        var dcim = Directory.CreateDirectory(Path.Combine(mount, "DCIM")).FullName;
        File.WriteAllText(Path.Combine(dcim,
            "A11_1234567_222222_20260915120000_0001.mp4"), "video");
        using var model = new MainWindowViewModel(() => [new UsbDevice("File-Stor Gadget", mount)]);
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

        var duplicate = model.Ports.Single(p => p.MountPoint == second);
        Assert.Equal(PortState.Failed, duplicate.State);
        Assert.Contains("Дубликат ID устройства 7", duplicate.Detail);
        Assert.Equal(PortState.Done, model.Ports.Single(p => p.MountPoint == first).State);
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
            Assert.Equal(PortState.Failed, model.Ports.Single(p => p.MountPoint == duplicate).State);
            Assert.Equal(charging ? PortState.ChargeOnly : PortState.Detected,
                model.Ports.Single(p => p.MountPoint == owner).State);
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

    [AvaloniaFact]
    public async Task Stop_reports_an_unreadable_operation_lock_without_throwing()
    {
        using var model = new MainWindowViewModel(() => []);
        var path = AppPaths.Database + ".operations.lock";
        File.Delete(path);
        Directory.CreateDirectory(path);
        model.Ports[0].SafeToRemove = true;

        await model.StopCollectionCommand.ExecuteAsync(null);

        Assert.True(model.SafeRemovalActive);
        Assert.False(model.SafeRemovalReady);
        Assert.Contains("Не удалось", model.SafeRemovalStatus);
        Assert.All(model.Ports, port => Assert.False(port.SafeToRemove));
    }

    [AvaloniaFact]
    public async Task An_unreadable_operation_lock_revokes_safe_removal_readiness()
    {
        using var model = new MainWindowViewModel(() => []);
        await model.StopCollectionCommand.ExecuteAsync(null);
        Assert.True(model.SafeRemovalReady);
        var path = AppPaths.Database + ".operations.lock";
        File.Delete(path);
        Directory.CreateDirectory(path);

        ApplyDevices(model);

        Assert.False(model.SafeRemovalReady);
        Assert.All(model.Ports, port => Assert.False(port.SafeToRemove));
    }

    [AvaloniaFact]
    public void A_changed_service_serial_does_not_spend_a_card_return_attempt()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Path.Combine(_dir, "card");
        CacheCard(model, mount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(mount);
        ApplyDevices(model, new UsbDevice("card", mount, "same-uuid", "first"));

        ApplyDevices(model, new UsbDevice("card", mount, "same-uuid", "second"));

        Assert.Empty(Field<Dictionary<string, int>>(model, "_returnAttempts"));
        Assert.Equal(PortState.ChargeOnly, model.Ports[0].State);
    }

    [AvaloniaFact]
    public async Task Backup_deferred_by_an_update_retries_on_the_next_poll()
    {
        if (!OperatingSystem.IsWindows()) return;
        var mount = Directory.CreateDirectory(Path.Combine(_dir, "deferred-card", "LOG")).Parent!.FullName;
        File.WriteAllText(Path.Combine(mount, "LOG", "card.txt"), "#ID:7\n");
        File.WriteAllText(Path.Combine(mount, "clip.mp4"), "recording");
        var settings = Services.Settings.Load();
        settings.MinFreeGb = 0;
        Assert.True(settings.SelectBackupRoot(AppPaths.BackupsRoot));
        settings.Save();
        using (var registry = new DeviceRegistry(AppPaths.Database))
            registry.ResolveByCard(mount, 1, "CAM", "card");
        var device = new UsbDevice("card", mount);
        using var model = new MainWindowViewModel(() => [device]);
        CacheCard(model, mount);
        var workers = Field<Dictionary<string, Task>>(model, "_workers");
        using (OperationGuard.Acquire(exclusive: true))
        {
            ApplyDevices(model, device);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (workers.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Empty(workers);
            Assert.Equal(PortState.Detected, model.Ports[0].State);
            Assert.False(Field<Dictionary<string, BackupStage>>(model, "_finished").ContainsKey(mount));
        }

        ApplyDevices(model, device);
        var finish = DateTime.UtcNow.AddSeconds(5);
        while (workers.Count > 0 && DateTime.UtcNow < finish) await Task.Delay(10);

        Assert.Empty(workers);
        Assert.Equal(PortState.Done, model.Ports[0].State);
        Assert.Equal("recording", File.ReadAllText(Path.Combine(AppPaths.BackupsRoot, "Device7", "clip.mp4")));
    }

    [AvaloniaFact]
    public async Task A_new_mounted_device_during_the_pause_revokes_safe_removal()
    {
        using var model = new MainWindowViewModel(() => []);
        await model.StopCollectionCommand.ExecuteAsync(null);
        Assert.True(model.SafeRemovalReady);
        var mount = Path.Combine(_dir, "new-mounted-card");

        ApplyDevices(model, UuidDevice("sdb1", mount, "uuid-new"));

        Assert.False(model.SafeRemovalReady);
        Assert.Empty(Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels"));
        Assert.Empty(Field<HashSet<string>>(model, "_identifying"));
    }

    [AvaloniaFact]
    public void An_open_modal_cannot_act_on_a_replacement_device_in_the_same_tile()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Path.Combine(_dir, "same-mount");
        CacheCard(model, mount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(mount);
        ApplyDevices(model, UuidDevice("sdb1", mount, "old"));
        model.OpenBayCommand.Execute(model.Ports[0]);
        ApplyDevices(model, UuidDevice("sdb1", mount, "new"));
        CacheCard(model, mount, 8);
        typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");
        ApplyDevices(model, UuidDevice("sdb1", mount, "new"));
        using var cancel = new CancellationTokenSource();
        Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels")[mount] = cancel;
        model.BayConfirm = "charge";

        model.ChargeOnlyBayCommand.Execute(null);

        Assert.False(cancel.IsCancellationRequested);
        Assert.DoesNotContain(mount, Field<HashSet<string>>(model, "_chargeOnly"));
        Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels").Clear();
    }

    [AvaloniaFact]
    public void A_corrupted_config_revokes_previously_opened_access()
    {
        using var model = new MainWindowViewModel(() => []);
        model.AskForTab(1);
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
        Assert.True(model.AccessAllowed);
        File.WriteAllText(Services.Settings.FilePath, "broken");

        Assert.False(model.AccessAllowed);
    }

    [AvaloniaFact]
    public void Only_three_returns_can_resume_after_repeated_disconnections()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Path.Combine(_dir, "card0");
        CacheCard(model, mount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(mount);
        ApplyDevices(model, UuidDevice("sdb1", mount, "uuid-a"));
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            ApplyDevices(model);
            mount = Path.Combine(_dir, "card" + attempt);
            CacheCard(model, mount, 7);
            ApplyDevices(model, UuidDevice("sd" + (char)('b' + attempt) + "1", mount, "uuid-a"));
            Assert.Equal(attempt <= 3 ? PortState.ChargeOnly : PortState.Failed, model.Ports[0].State);
        }
        Assert.Empty(Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels"));
    }

    [AvaloniaFact]
    public void A_returned_uuid_with_a_different_strict_id_cannot_resume_the_session()
    {
        using var model = new MainWindowViewModel(() => []);
        var oldMount = Path.Combine(_dir, "old-card");
        var newMount = Path.Combine(_dir, "new-card");
        CacheCard(model, oldMount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(oldMount);
        ApplyDevices(model, UuidDevice("sdb1", oldMount, "uuid-a"));
        ApplyDevices(model);
        CacheCard(model, newMount, 8);

        ApplyDevices(model, UuidDevice("sdf1", newMount, "uuid-a"));

        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("ID", model.Ports[0].Detail);
        Assert.False(Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels").ContainsKey(newMount));
    }

    [AvaloniaFact]
    public void A_missing_card_waits_120_seconds_before_releasing_its_tile()
    {
        using var model = new MainWindowViewModel(() => []);
        var clock = typeof(MainWindowViewModel).GetField("_now", PrivateFields);
        Assert.NotNull(clock);
        var now = DateTime.UtcNow;
        clock.SetValue(model, new Func<DateTime>(() => now));
        var mount = Path.Combine(_dir, "waiting-card");
        CacheCard(model, mount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(mount);
        ApplyDevices(model, UuidDevice("sdb1", mount, "uuid-a"));

        now = now.AddSeconds(119);
        ApplyDevices(model);
        Assert.Equal("7", model.Ports[0].CameraId);
        Assert.NotEqual(PortState.Done, model.Ports[0].State);
        now = now.AddSeconds(2);
        ApplyDevices(model);
        Assert.True(model.Ports[0].IsFree);
    }

    [AvaloniaFact]
    public void The_same_uuid_and_strict_id_return_to_their_previous_tile_under_a_new_name()
    {
        using var model = new MainWindowViewModel(() => []);
        var oldMount = Path.Combine(_dir, "old-card");
        var newMount = Path.Combine(_dir, "new-card");
        CacheCard(model, oldMount, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(oldMount);
        ApplyDevices(model, UuidDevice("sdb1", oldMount, "uuid-a"));
        var tile = model.Ports.Single(p => p.MountPoint == oldMount);
        ApplyDevices(model);
        CacheCard(model, newMount, 7);

        ApplyDevices(model, UuidDevice("sdf1", newMount, "uuid-a"));

        Assert.Equal(newMount, tile.MountPoint);
        Assert.Equal("7", tile.CameraId);
        Assert.Equal(PortState.ChargeOnly, tile.State);
    }

    [AvaloniaFact]
    public void A_replacement_uuid_under_the_same_name_does_not_inherit_the_old_success()
    {
        using var model = new MainWindowViewModel(() => []);
        var mount = Path.Combine(_dir, "card");
        CacheCard(model, mount, 7);
        Field<Dictionary<string, BackupStage>>(model, "_finished")[mount] = BackupStage.Done;
        ApplyDevices(model, UuidDevice("sdb1", mount, "uuid-old"));
        model.Ports[0].State = PortState.Done;

        ApplyDevices(model, UuidDevice("sdb1", mount, "uuid-new"));

        Assert.DoesNotContain(model.Ports, p => p.State == PortState.Done && p.MountPoint == mount);
        Assert.False(Field<Dictionary<string, BackupStage>>(model, "_finished").ContainsKey(mount));
    }

    private static UsbDevice UuidDevice(string name, string mount, string uuid)
    {
        var constructor = typeof(UsbDevice).GetConstructors().SingleOrDefault(c => c.GetParameters().Length == 4);
        Assert.NotNull(constructor);
        return (UsbDevice)constructor.Invoke([name, mount, uuid, "serial"]);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_waits_for_workers_before_releasing_media_and_resume_allows_new_work(bool deviceError)
    {
        using var model = new MainWindowViewModel(() => []);
        var command = typeof(MainWindowViewModel).GetProperty("StopCollectionCommand");
        Assert.NotNull(command);
        var mount = Path.Combine(_dir, "active");
        using var cancel = new CancellationTokenSource();
        Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels")[mount] = cancel;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field<Dictionary<string, Task>>(model, "_workers")[mount] = completed.Task;
        Field<Dictionary<string, Mounted>>(model, "_mounted")["camera"] = new Mounted(mount, true);
        var released = false;
        typeof(MainWindowViewModel).GetField("_releaseMount", PrivateFields)!.SetValue(model,
            new Func<Mounted?, bool>(media =>
            {
                Assert.True(media!.Ours);
                return released = true;
            }));
        var stopping = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)command.GetValue(model)!).ExecuteAsync(null);
        Assert.True(cancel.IsCancellationRequested);
        Assert.False(released);
        Assert.False(stopping.IsCompleted);
        if (deviceError) completed.SetException(new DeviceLostException(
            new IOException("EIO", new System.ComponentModel.Win32Exception(5))));
        else completed.SetResult();
        await stopping;
        Assert.True(released);
        Assert.True((bool)typeof(MainWindowViewModel).GetProperty("SafeRemovalReady")!.GetValue(model)!);
        typeof(MainWindowViewModel).GetProperty("ResumeCollectionCommand")!.GetValue(model)!
            .GetType().GetMethod("Execute")!.Invoke(
                typeof(MainWindowViewModel).GetProperty("ResumeCollectionCommand")!.GetValue(model), [null]);
        Assert.False((bool)typeof(MainWindowViewModel).GetProperty("SafeRemovalActive")!.GetValue(model)!);
        Field<Dictionary<string, CancellationTokenSource>>(model, "_cancels").Clear();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_foreign_unmount_never_reports_safe_removal_ready(bool foreign)
    {
        using var model = new MainWindowViewModel(() => []);
        var command = typeof(MainWindowViewModel).GetProperty("StopCollectionCommand");
        Assert.NotNull(command);
        Field<Dictionary<string, Mounted>>(model, "_mounted")["camera"] = new Mounted("/media/CAM", !foreign);
        typeof(MainWindowViewModel).GetField("_releaseMount", PrivateFields)!.SetValue(model,
            new Func<Mounted?, bool>(_ => false));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)command.GetValue(model)!).ExecuteAsync(null);

        Assert.False((bool)typeof(MainWindowViewModel).GetProperty("SafeRemovalReady")!.GetValue(model)!);
        Assert.NotEmpty((string)typeof(MainWindowViewModel).GetProperty("SafeRemovalStatus")!.GetValue(model)!);
    }

    [AvaloniaFact]
    public async Task Safe_removal_waits_for_other_station_operations_and_holds_the_lock_during_release()
    {
        using var model = new MainWindowViewModel(() => []);
        Field<Dictionary<string, Mounted>>(model, "_mounted")["camera"] = new Mounted("/mnt/usb_backup/CAM", true);
        var released = false;
        var releaseWasProtected = false;
        typeof(MainWindowViewModel).GetField("_releaseMount", PrivateFields)!.SetValue(model,
            new Func<Mounted?, bool>(_ =>
            {
                released = true;
                try { using var operation = OperationGuard.Acquire(); }
                catch (StationBusyException) { releaseWasProtected = true; }
                return true;
            }));

        using (OperationGuard.Acquire())
        {
            await model.StopCollectionCommand.ExecuteAsync(null);
            Assert.False(released);
            Assert.False(model.SafeRemovalReady);
            Assert.Contains("обслуживание", model.SafeRemovalStatus);
        }

        await model.StopCollectionCommand.ExecuteAsync(null);

        Assert.True(released);
        Assert.True(releaseWasProtected);
        Assert.True(model.SafeRemovalReady);
        using (OperationGuard.Acquire())
        {
            ApplyDevices(model);
            Assert.False(model.SafeRemovalReady);
        }
        ApplyDevices(model);
        Assert.True(model.SafeRemovalReady);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unreadable_settings_block_the_default_password_for_tabs_and_exit(bool exit)
    {
        using var model = new MainWindowViewModel(() => []);
        File.WriteAllText(Services.Settings.FilePath, "{broken");
        var granted = false;
        model.ExitRequested += () => granted = true;
        model.AccessGranted += _ => granted = true;
        if (exit) model.ExitCommand.Execute(null);
        else model.AskForTab(1);
        model.PasswordInput = PasswordGate.Default();

        model.ConfirmPasswordCommand.Execute(null);

        Assert.False(granted);
        Assert.False(model.AccessAllowed);
        Assert.NotEmpty(model.PasswordError);
    }

    [AvaloniaFact]
    public void A_device_keeps_its_tile_when_the_poll_order_changes()
    {
        using var model = new MainWindowViewModel(() => []);
        var first = Path.Combine(_dir, "first");
        var second = Path.Combine(_dir, "second");
        CacheCard(model, first, 7);
        CacheCard(model, second, 8);
        Field<HashSet<string>>(model, "_chargeOnly").UnionWith([first, second]);
        ApplyDevices(model, new("first", first), new("second", second));
        var tile = model.Ports.Single(p => p.MountPoint == first);

        ApplyDevices(model, new("second", second), new("first", first));

        Assert.Equal(first, tile.MountPoint);
        Assert.Equal("7", tile.CameraId);
        Assert.Equal(PortState.ChargeOnly, tile.State);
    }

    [AvaloniaFact]
    public void Window_count_changes_immediately_and_busy_tiles_cannot_be_hidden()
    {
        using var model = new MainWindowViewModel(() => []);
        model.Settings.BayCount = 12;
        model.Settings.SaveBayCountCommand.Execute(null);
        Assert.Equal(12, model.Ports.Count);
        model.Ports[11].State = PortState.Copying;
        model.Settings.BayCount = 6;

        model.Settings.SaveBayCountCommand.Execute(null);

        Assert.Equal(12, model.Ports.Count);
        Assert.Equal(12, Services.Settings.Load().BayCount);
        Assert.Contains("заняты", model.Settings.Hint);
        model.Ports[11].Clear();
        model.Settings.SaveBayCountCommand.Execute(null);
        Assert.Equal(6, model.Ports.Count);
        Assert.False(model.Settings.RestartNeeded);
    }

    [AvaloniaFact]
    public async Task Overflow_is_visible_and_extra_media_are_still_processed()
    {
        using var model = new MainWindowViewModel(() => []);
        var found = Enumerable.Range(0, 11).Select(i => new UsbDevice("card" + i,
            Directory.CreateDirectory(Path.Combine(_dir, "card" + i)).FullName)).ToArray();
        for (var i = 0; i < found.Length; i++) CacheCard(model, found[i].MountPoint!, 100 + i);
        typeof(MainWindowViewModel).GetField("_priority", PrivateFields)!.SetValue(model, "busy");

        ApplyDevices(model, found);

        Assert.Contains("не показан", model.Status);
        Assert.True(Field<Dictionary<long, string>>(model, "_astraOwners").ContainsKey(110));
        await Task.Yield();
    }

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

    [Fact]
    public void Media_inserted_while_waiting_for_offline_update_is_not_treated_as_camera()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var model = new MainWindowViewModel(() => []);
        var camera = Directory.CreateDirectory(Path.Combine(_dir, "camera")).FullName;
        var usb = Directory.CreateDirectory(Path.Combine(_dir, "usb")).FullName;
        // Носитель без выгрузки: при идущем копировании ожидание не начинается.
        Field<HashSet<string>>(model, "_chargeOnly").Add(camera);
        ApplyDevices(model, new UsbDevice("camera", camera));

        model.StartOfflineUpdateCommand.Execute(null);
        Assert.True(model.OfflineWaiting);
        ApplyDevices(model, new("camera", camera), new("usb", usb));

        Assert.Contains(model.Ports, port => port.MountPoint == camera);
        Assert.DoesNotContain(model.Ports, port => port.MountPoint == usb);

        model.CancelOfflineUpdateCommand.Execute(null);
        Assert.False(model.OfflineWaiting);
        ApplyDevices(model, new("camera", camera), new("usb", usb));
        Assert.Contains(model.Ports, port => port.MountPoint == usb);
    }

    [Fact]
    public void Tile_shows_device_name_when_set_and_number_otherwise()
    {
        using var model = new MainWindowViewModel(() => []);
        var named = Directory.CreateDirectory(Path.Combine(_dir, "named")).FullName;
        var plain = Directory.CreateDirectory(Path.Combine(_dir, "plain")).FullName;
        CacheCard(model, named, 7);
        CacheCard(model, plain, 8);
        var charging = Field<HashSet<string>>(model, "_chargeOnly");
        charging.UnionWith([named, plain]);
        typeof(MainWindowViewModel).GetField("_deviceNames", PrivateFields)!
            .SetValue(model, new Dictionary<long, string> { [7] = "Патруль-3" });

        ApplyDevices(model, new("named", named), new("plain", plain));

        Assert.Equal("Патруль-3", model.Ports.Single(p => p.MountPoint == named).CameraId);
        Assert.Equal("8", model.Ports.Single(p => p.MountPoint == plain).CameraId);
    }

    [Fact]
    public void Device_names_are_read_from_the_shared_database()
    {
        var db = Path.Combine(_dir, "names.db");
        var card = Directory.CreateDirectory(Path.Combine(_dir, "card", "LOG")).Parent!.FullName;
        File.WriteAllText(Path.Combine(card, "LOG", "20260915.txt"), "#ID:42\n");
        using (var registry = new DeviceRegistry(db))
        {
            registry.ResolveByCard(card, 1, "CAM", "sdb1");
            registry.Rename(42, "Патруль-3");
        }

        Assert.Equal("Патруль-3", DeviceRegistry.ReadNames(db)[42]);
        Assert.Empty(DeviceRegistry.ReadNames(Path.Combine(_dir, "missing.db")));
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
