using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class MonitorParityTests : IAsyncLifetime
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly string _previous = AppPaths.Root;
    private readonly string _root = Directory.CreateTempSubdirectory("astra-monitor-parity-").FullName;

    public MonitorParityTests()
    {
        AppPaths.Root = _root;
        AppPaths.EnsureCreated();
        Assert.True(new Settings { AlarmSound = false }.Save());
    }

    [AvaloniaFact]
    public async Task A_failed_poll_keeps_the_existing_camera_and_allows_the_next_poll()
    {
        var fail = true;
        using var model = new MainWindowViewModel(() => fail
            ? throw new IOException("опрос не ответил") : []);
        var mount = Directory.CreateDirectory(Path.Combine(_root, "card")).FullName;
        var device = new UsbDevice("camera", mount, "uuid7");
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        Field<System.Collections.IDictionary>(model, "_identified")[mount] =
            Activator.CreateInstance(cardType, [7L, "7", "", "", "", ""]);
        Field<HashSet<string>>(model, "_chargeOnly").Add(mount);
        Apply(model, device);
        var recent = Field<Dictionary<string, (UsbDevice Device, DateTime Seen)>>(model, "_recent");
        var key = Assert.Single(recent).Key;
        recent[key] = (device, DateTime.UtcNow.AddMinutes(-5));

        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling"));

        Assert.True(recent.ContainsKey(key));
        Assert.Equal(PortState.ChargeOnly, model.Ports[0].State);
        Assert.Contains("опрос", model.Status);
        fail = false;
        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling"));
        Assert.Empty(recent);
    }

    [AvaloniaFact]
    public async Task Identification_failure_releases_busy_state_and_shows_the_error_on_the_same_tile()
    {
        using var model = new MainWindowViewModel(() => throw new InvalidOperationException("ошибка проверки карты"));
        var mount = Directory.CreateDirectory(Path.Combine(_root, "card")).FullName;
        var dcim = Directory.CreateDirectory(Path.Combine(mount, "DCIM")).FullName;
        File.WriteAllText(Path.Combine(dcim, "A11_7_222222_20261020120000_0001.mp4"), "video");

        Apply(model, new UsbDevice("camera", mount, "uuid7"));
        await Until(() => Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Empty(Field<HashSet<string>>(model, "_identifying"));
        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("ошибка проверки карты", model.Ports[0].Detail);
        Assert.Contains("ошибка проверки карты", File.ReadAllText(CrashLog.FilePath));
        Assert.Single(model.Ports, port => port.DeviceKey is not null);
    }

    [AvaloniaFact]
    public async Task Failed_mount_is_visible_and_retries_after_one_missing_poll()
    {
        var mount = Directory.CreateDirectory(Path.Combine(_root, "mounted-card")).FullName;
        var device = new UsbDevice("camera7", null, "uuid7");
        using var model = new MainWindowViewModel(() => [device]);
        var attempts = 0;
        Func<string, TimeSpan, CancellationToken, Mounted?> mountDevice = (_, _, _) =>
            ++attempts == 1 ? throw new IOException("карта ещё не готова") : new Mounted(mount, false);
        var operation = typeof(MainWindowViewModel).GetField("_mountDevice", Private);
        Assert.NotNull(operation);
        operation.SetValue(model, mountDevice);

        Apply(model, device);
        await Until(() => Field<HashSet<string>>(model, "_mounting").Count == 0);

        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.Contains("карта ещё не готова", model.Ports[0].Detail);
        Apply(model);
        Apply(model, device);
        await Until(() => Field<HashSet<string>>(model, "_mounting").Count == 0);
        Assert.Equal(mount, Field<Dictionary<string, Mounted>>(model, "_mounted")[device.Name].Path);
        Assert.Empty(Field<Dictionary<string, string>>(model, "_mountErrors"));
        Assert.Contains("карта ещё не готова", File.ReadAllText(StationLog.FilePath));
    }

    [AvaloniaFact]
    public async Task An_unexpected_mount_fault_is_not_retried_until_confirmed_reconnection()
    {
        var device = new UsbDevice("camera7", null, "uuid7");
        using var model = new MainWindowViewModel(() => [device]);
        var now = DateTime.UtcNow;
        typeof(MainWindowViewModel).GetField("_now", Private)!.SetValue(model, new Func<DateTime>(() => now));
        var attempts = 0;
        Func<string, TimeSpan, CancellationToken, Mounted?> mount = (_, _, _) =>
        {
            attempts++;
            throw new InvalidOperationException("сбой кода монтирования");
        };
        typeof(MainWindowViewModel).GetField("_mountDevice", Private)!.SetValue(model, mount);

        for (var poll = 0; poll < 3; poll++)
        {
            Apply(model, device);
            await Until(() => Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        }

        Assert.Equal(1, attempts);
        Assert.Equal(PortState.Failed, model.Ports[0].State);
        Assert.DoesNotContain("Повторим", model.Ports[0].Detail);
        Assert.Contains("сбой кода монтирования", File.ReadAllText(CrashLog.FilePath));
        now = now.AddSeconds(10);
        Apply(model);
        Apply(model, device);
        await Until(() => Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        Assert.Equal(2, attempts);
    }

    [AvaloniaFact]
    public async Task Expected_mount_error_suppression_resets_after_confirmed_disconnection()
    {
        var device = new UsbDevice("camera7", null, "uuid7");
        using var model = new MainWindowViewModel(() => [device]);
        var now = DateTime.UtcNow;
        typeof(MainWindowViewModel).GetField("_now", Private)!.SetValue(model, new Func<DateTime>(() => now));
        Func<string, TimeSpan, CancellationToken, Mounted?> mount = (_, _, _) => throw new IOException("ещё нет карты");
        typeof(MainWindowViewModel).GetField("_mountDevice", Private)!.SetValue(model, mount);
        for (var poll = 0; poll < 2; poll++)
        {
            Apply(model, device);
            await Until(() => Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        }
        Assert.Single(LogLines("Не удалось смонтировать camera7, повторим"));

        now = now.AddSeconds(10);
        Apply(model);
        Apply(model, device);
        await Until(() => Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Equal(2, LogLines("Не удалось смонтировать camera7, повторим").Length);
    }

    [AvaloniaFact]
    public async Task An_offline_check_task_fault_is_observed_logged_and_allows_a_later_check()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var watch = typeof(MainWindowViewModel).GetMethod("WatchOfflineMedia", Private)!;

        watch.Invoke(model, [new UsbDevice[] { new("update-stick", "\0") }]);
        await Until(() => !Field<bool>(model, "_offlineChecking"));

        Assert.Contains("Проверьте", model.OfflineStatus);
        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("Не удалось проверить флешку обновления", log);
        Assert.Contains("ArgumentException", log);
        Assert.Contains("CheckOfflineMedia", log);
        var valid = Directory.CreateDirectory(Path.Combine(_root, "valid-update-stick")).FullName;
        watch.Invoke(model, [new UsbDevice[] { new("valid-update-stick", valid) }]);
        await Until(() => !Field<bool>(model, "_offlineChecking"));
        Assert.Contains("не найдено", model.OfflineStatus);
    }

    [AvaloniaFact]
    public async Task Busy_backup_deferral_is_logged_once_and_resets_after_successful_acquisition()
    {
        var settings = Settings.Load();
        settings.MinFreeGb = 0;
        Assert.True(settings.SelectBackupRoot(AppPaths.BackupsRoot));
        Assert.True(settings.Save());
        var first = Card(7);
        using var model = new MainWindowViewModel(() => [first]);
        var now = DateTime.UtcNow;
        typeof(MainWindowViewModel).GetField("_now", Private)!.SetValue(model, new Func<DateTime>(() => now));
        Cache(model, first.MountPoint!, 7);
        var workers = Field<Dictionary<string, Task>>(model, "_workers");

        using (OperationGuard.Acquire(exclusive: true))
        {
            for (var poll = 0; poll < 3; poll++)
            {
                Apply(model, first);
                await Until(() => workers.Count == 0);
            }
        }
        Assert.Single(LogLines("Станция занята другой операцией, выгрузка подождёт"));

        Apply(model, first);
        await Until(() => workers.Count == 0);
        Assert.Equal(PortState.Done, model.Ports[0].State);
        model.OpenBayCommand.Execute(model.Ports[0]);
        model.ResumeBayCommand.Execute(null);
        using (OperationGuard.Acquire(exclusive: true))
        {
            Apply(model, first);
            await Until(() => workers.Count == 0);
        }
        Assert.Equal(2, LogLines("Станция занята другой операцией, выгрузка подождёт").Length);
        now = now.AddSeconds(121);
        Apply(model);
        Cache(model, first.MountPoint!, 7);
        using (OperationGuard.Acquire(exclusive: true))
        {
            Apply(model, first);
            await Until(() => workers.Count == 0);
        }
        Assert.Equal(3, LogLines("Станция занята другой операцией, выгрузка подождёт").Length);
    }

    [AvaloniaFact]
    public void A_new_connection_is_logged_once_across_repeated_polls()
    {
        var device = Card(7);
        using var model = new MainWindowViewModel(() => [device]);
        Cache(model, device.MountPoint!, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(device.MountPoint!);

        Apply(model, device);
        Apply(model, device);

        Assert.Single(LogLines("Новое USB-устройство: camera7"));
    }

    [AvaloniaFact]
    public void A_lost_card_is_logged_once_while_its_tile_waits_for_return()
    {
        using var model = RecoverCard();

        Assert.Single(LogLines("ждём возвращения"));
    }

    [AvaloniaFact]
    public void A_returned_card_is_logged_once_without_an_extra_new_connection()
    {
        using var model = RecoverCard();

        Assert.Single(LogLines("карта вернулась как camera-returned"));
        Assert.Single(LogLines("Новое USB-устройство:"));
        Assert.Equal(PortState.ChargeOnly, model.Ports[0].State);
    }

    [AvaloniaFact]
    public async Task Offline_hold_logs_one_deferral_and_logs_the_new_connection_after_cancel()
    {
        var device = Card(7);
        using var model = new MainWindowViewModel(() => [device]) { OfflineWaiting = true };
        typeof(MainWindowViewModel).GetField("_offlineKnown", Private)!.SetValue(model, new HashSet<string>());

        Apply(model, device);
        await Until(() => !Field<bool>(model, "_offlineChecking"));
        Apply(model, device);
        await Until(() => !Field<bool>(model, "_offlineChecking"));

        Assert.Single(LogLines("Новое USB-устройство отложено ради офлайн-обновления: camera7"));
        Assert.Empty(LogLines("Новое USB-устройство:"));
        Assert.All(model.Ports, port => Assert.Null(port.DeviceKey));
        Cache(model, device.MountPoint!, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(device.MountPoint!);
        model.CancelOfflineUpdateCommand.Execute(null);
        Apply(model, device);
        Assert.Single(LogLines("Новое USB-устройство: camera7"));
    }

    [AvaloniaFact]
    public void Archive_capacity_uses_bytes_for_values_below_one_kilobyte()
    {
        using var model = new MainWindowViewModel(() => []);

        typeof(MainWindowViewModel).GetMethod("UpdateStorage", Private)!.Invoke(model,
            [new StorageState(1023, 512, "archive", true)]);

        Assert.Contains("512 Б", model.StorageLabel);
        Assert.Contains("1023 Б", model.StorageLabel);
        typeof(MainWindowViewModel).GetMethod("UpdateStorage", Private)!.Invoke(model,
            [new StorageState(8 * 1024 * 1024, 1024, "archive", true)]);
        Assert.Contains("1,0 КБ", model.StorageLabel);
        Assert.Contains("8,0 МБ", model.StorageLabel);
    }

    [AvaloniaFact]
    public async Task Refresh_keeps_a_previously_self_mounted_archive_out_of_source_cleanup()
    {
        var archive = ArchiveRoot();
        using var model = new MainWindowViewModel(() => [new UsbDevice("archive-disk", archive)]);
        var released = Releases(model);
        var mounts = Field<Dictionary<string, Mounted>>(model, "_mounted");
        mounts["archive-disk"] = new Mounted(archive, true);

        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Empty(released);
        Assert.DoesNotContain("archive-disk", mounts.Keys);
        Assert.All(model.Ports, port => Assert.Null(port.DeviceKey));
        Assert.True(ArchiveGuard.Available(archive));
    }

    [AvaloniaFact]
    public async Task Stop_collection_releases_sources_but_keeps_the_archive_without_a_prior_poll()
    {
        var archive = ArchiveRoot();
        using var model = new MainWindowViewModel(() => []);
        var released = Releases(model);
        var source = Directory.CreateDirectory(Path.Combine(_root, "own-source")).FullName;
        var mounts = Field<Dictionary<string, Mounted>>(model, "_mounted");
        mounts["archive-disk"] = new Mounted(archive, true);
        mounts["camera7"] = new Mounted(source, true);

        await model.StopCollectionCommand.ExecuteAsync(null);

        Assert.Equal(source, Assert.Single(released).Path);
        Assert.Empty(mounts);
        Assert.True(model.SafeRemovalReady);
        Assert.True(ArchiveGuard.Available(archive));
    }

    [AvaloniaFact]
    public async Task Exit_releases_sources_but_keeps_the_archive_without_a_prior_poll()
    {
        var archive = ArchiveRoot();
        var model = new MainWindowViewModel(() => []);
        var released = Releases(model);
        var source = Directory.CreateDirectory(Path.Combine(_root, "own-source")).FullName;
        var mounts = Field<Dictionary<string, Mounted>>(model, "_mounted");
        mounts["archive-disk"] = new Mounted(archive, true);
        mounts["camera7"] = new Mounted(source, true);

        model.Dispose();
        await Until(() => !released.IsEmpty);

        Assert.Equal(source, Assert.Single(released).Path);
        Assert.DoesNotContain("archive-disk", mounts.Keys);
        Assert.True(ArchiveGuard.Available(archive));
    }

    [AvaloniaFact]
    public async Task A_self_mounted_archive_with_recorder_files_is_never_identified_as_a_source()
    {
        var archive = ArchiveRoot();
        Directory.CreateDirectory(Path.Combine(archive, "LOG"));
        File.WriteAllText(Path.Combine(archive, "LOG", "card.txt"), "#ID:7\n");
        File.WriteAllText(Path.Combine(archive, "clip.mp4"), "archive contents");
        var device = new UsbDevice("archive-disk", null, "archive-uuid");
        using var model = new MainWindowViewModel(() => [device]);
        var released = Releases(model);
        var attempts = 0;
        Func<string, TimeSpan, CancellationToken, Mounted?> mount = (_, _, _) =>
        {
            attempts++;
            return new Mounted(archive, true);
        };
        typeof(MainWindowViewModel).GetField("_mountDevice", Private)!.SetValue(model, mount);

        for (var poll = 0; poll < 2; poll++)
        {
            model.Refresh();
            await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        }

        Assert.DoesNotContain(archive, Field<System.Collections.IDictionary>(model, "_identified").Keys.Cast<string>());
        Assert.Empty(Field<Dictionary<string, Task>>(model, "_workers"));
        Assert.False(Directory.Exists(Path.Combine(archive, "Device7")));
        Assert.Equal(1, attempts);
        Assert.Empty(released);
        Assert.All(model.Ports, port => Assert.Null(port.DeviceKey));
    }

    [AvaloniaFact]
    public async Task Archive_connection_is_logged_once_and_again_after_confirmed_disconnection()
    {
        var archive = ArchiveRoot();
        var device = new UsbDevice("archive-disk", archive, "archive-uuid");
        UsbDevice[] inventory = [device];
        using var model = new MainWindowViewModel(() => inventory);
        var released = Releases(model);
        var now = DateTime.UtcNow;
        typeof(MainWindowViewModel).GetField("_now", Private)!.SetValue(model, new Func<DateTime>(() => now));
        Field<Dictionary<string, Mounted>>(model, "_mounted")[device.Name] = new Mounted(archive, true);
        for (var poll = 0; poll < 2; poll++)
        {
            model.Refresh();
            await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        }
        Assert.Single(LogLines("Подключён диск архива"));

        now = now.AddSeconds(10);
        inventory = [];
        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        inventory = [device];
        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Equal(2, LogLines("Подключён диск архива").Length);
        Assert.Empty(released);
    }

    [AvaloniaFact]
    public async Task Selecting_another_archive_returns_the_previous_volume_to_normal_device_handling()
    {
        var archive = ArchiveRoot();
        var device = new UsbDevice("previous-archive", archive, "previous-uuid");
        using var model = new MainWindowViewModel(() => [device]);
        Releases(model);
        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement-archive")).FullName;
        var settings = Settings.Load();
        Assert.True(settings.SelectBackupRoot(replacement));
        Assert.True(settings.Save());
        Cache(model, archive, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(archive);

        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        var port = Assert.Single(model.Ports, port => port.MountPoint == archive);
        Assert.Equal(PortState.ChargeOnly, port.State);
        Assert.Contains(device.Name, Field<Dictionary<string, Mounted>>(model, "_mounted").Keys);
    }

    [AvaloniaFact]
    public async Task Applying_inventory_and_missing_devices_does_not_resolve_the_archive_on_the_ui_thread()
    {
        var archive = ArchiveRoot();
        UsbDevice[] inventory = [new("archive-disk", archive)];
        using var model = new MainWindowViewModel(() => inventory);
        var uiCalls = RecordArchiveResolution(model);
        Releases(model);

        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);
        inventory = [];
        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Empty(uiCalls);
    }

    [AvaloniaFact]
    public async Task Stopping_collection_resolves_the_archive_away_from_the_ui_thread()
    {
        var archive = ArchiveRoot();
        using var model = new MainWindowViewModel(() => []);
        var uiCalls = RecordArchiveResolution(model);
        Releases(model);
        Field<Dictionary<string, Mounted>>(model, "_mounted")["archive-disk"] = new Mounted(archive, true);

        await model.StopCollectionCommand.ExecuteAsync(null);

        Assert.Empty(uiCalls);
    }

    [AvaloniaFact]
    public async Task Exiting_resolves_the_archive_away_from_the_ui_thread()
    {
        var archive = ArchiveRoot();
        var model = new MainWindowViewModel(() => []);
        var uiCalls = RecordArchiveResolution(model);
        var released = Releases(model);
        var source = Directory.CreateDirectory(Path.Combine(_root, "source-for-exit")).FullName;
        Field<Dictionary<string, Mounted>>(model, "_mounted")["archive-disk"] = new Mounted(archive, true);
        Field<Dictionary<string, Mounted>>(model, "_mounted")["camera7"] = new Mounted(source, true);

        model.Dispose();
        await Until(() => !released.IsEmpty);

        Assert.Empty(uiCalls);
    }

    [AvaloniaFact]
    public async Task Classifying_a_completed_mount_does_not_resolve_the_archive_on_the_ui_thread()
    {
        var archive = ArchiveRoot();
        var device = new UsbDevice("archive-disk", null);
        using var model = new MainWindowViewModel(() => [device]);
        var uiCalls = RecordArchiveResolution(model);
        Releases(model);
        Func<string, TimeSpan, CancellationToken, Mounted?> mount = (_, _, _) => new Mounted(archive, true);
        typeof(MainWindowViewModel).GetField("_mountDevice", Private)!.SetValue(model, mount);

        model.Refresh();
        await Until(() => !Field<bool>(model, "_polling") && Field<HashSet<Task>>(model, "_mediaTasks").Count == 0);

        Assert.Empty(uiCalls);
    }

    private static System.Collections.Concurrent.ConcurrentBag<int> RecordArchiveResolution(MainWindowViewModel model)
    {
        var ui = Dispatcher.UIThread;
        var calls = new System.Collections.Concurrent.ConcurrentBag<int>();
        Func<Settings, string> resolve = settings =>
        {
            if (ui.CheckAccess()) calls.Add(Environment.CurrentManagedThreadId);
            return settings.ResolveBackupRoot();
        };
        typeof(MainWindowViewModel).GetField("_resolveArchiveRoot", Private)!.SetValue(model, resolve);
        return calls;
    }

    private string ArchiveRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "live-archive")).FullName;
        var settings = Settings.Load();
        Assert.True(settings.SelectBackupRoot(root));
        Assert.True(settings.Save());
        return root;
    }

    private static System.Collections.Concurrent.ConcurrentBag<Mounted> Releases(MainWindowViewModel model)
    {
        var released = new System.Collections.Concurrent.ConcurrentBag<Mounted>();
        Func<Mounted?, bool> release = mounted => { released.Add(mounted!); return true; };
        typeof(MainWindowViewModel).GetField("_releaseMount", Private)!.SetValue(model, release);
        return released;
    }

    private UsbDevice Card(long id)
    {
        var mount = Directory.CreateDirectory(Path.Combine(_root, "card" + id, "LOG")).Parent!.FullName;
        File.WriteAllText(Path.Combine(mount, "LOG", "card.txt"), $"#ID:{id}\n");
        File.WriteAllText(Path.Combine(mount, "clip.mp4"), "video");
        using var registry = new DeviceRegistry(AppPaths.Database);
        registry.ResolveByCard(mount, 1, "CAM", "camera" + id);
        return new UsbDevice("camera" + id, mount, "uuid" + id);
    }

    private static void Cache(MainWindowViewModel model, string mount, long id)
    {
        var cardType = typeof(MainWindowViewModel).Assembly.GetType("AstraUsb.ViewModels.CardInfo")!;
        Field<System.Collections.IDictionary>(model, "_identified")[mount] =
            Activator.CreateInstance(cardType, [id, id.ToString(), "", "", "", ""]);
    }

    private string[] LogLines(string text) => File.Exists(StationLog.FilePath)
        ? File.ReadAllLines(StationLog.FilePath).Where(line => line.Contains(text, StringComparison.Ordinal)).ToArray() : [];

    private MainWindowViewModel RecoverCard()
    {
        var original = Card(7);
        var model = new MainWindowViewModel(() => [original]);
        Cache(model, original.MountPoint!, 7);
        Field<HashSet<string>>(model, "_chargeOnly").Add(original.MountPoint!);
        Apply(model, original);
        Apply(model);
        Apply(model);
        var returned = Directory.CreateDirectory(Path.Combine(_root, "returned-card")).FullName;
        Cache(model, returned, 7);
        Apply(model, new UsbDevice("camera-returned", returned, "uuid7"));
        Apply(model, new UsbDevice("camera-returned", returned, "uuid7"));
        return model;
    }

    [AvaloniaFact]
    public void Confirmed_disconnection_is_written_to_the_log()
    {
        using var model = new MainWindowViewModel(() => []);
        var device = new UsbDevice("camera7", Path.Combine(_root, "card"), "uuid7");

        typeof(MainWindowViewModel).GetMethod("ForgetCard", Private)!.Invoke(model, ["uuid7", device]);

        Assert.True(File.Exists(StationLog.FilePath));
        Assert.Contains("camera7", File.ReadAllText(StationLog.FilePath));
        Assert.Contains("отключ", File.ReadAllText(StationLog.FilePath));
    }

    [AvaloniaFact]
    public void Archive_capacity_shows_free_space_and_warns_strictly_below_ten_percent()
    {
        using var model = new MainWindowViewModel(() => []);
        var method = typeof(MainWindowViewModel).GetMethod("UpdateStorage", Private)!;
        method.Invoke(model, [new StorageState(100_000_000_000, 10_000_000_000, "archive", true)]);
        Assert.Contains("свободно", model.StorageLabel);
        Assert.Contains("90%", model.StorageLabel);
        var normal = model.StorageBrush.ToString();

        method.Invoke(model, [new StorageState(100_000_000_000, 9_000_000_000, "archive", true)]);
        Assert.NotEqual(normal, model.StorageBrush.ToString());
    }

    private static T Field<T>(MainWindowViewModel model, string name) =>
        (T)typeof(MainWindowViewModel).GetField(name, Private)!.GetValue(model)!;

    private static void Apply(MainWindowViewModel model, params UsbDevice[] devices) =>
        typeof(MainWindowViewModel).GetMethod("Apply", Private)!.Invoke(model,
            [devices, StorageState.Unknown("archive")]);

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done(), "Фоновая операция не завершилась");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(_root, true); break; }
                catch (IOException) when (attempt < 100) { await Task.Delay(10); }
            }
        }
        finally { AppPaths.Root = _previous; }
    }
}
