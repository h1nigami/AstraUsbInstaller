using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class UIReviewFixTests : IDisposable
{
    private readonly string _previous = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-ui-review-").FullName;
    public UIReviewFixTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    private static void Expire(MainWindowViewModel model)
    {
        var access = new AccessGuard(1);
        access.Unlock(DateTime.Now.AddMinutes(-2));
        typeof(MainWindowViewModel).GetField("_access", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, access);
        typeof(MainWindowViewModel).GetMethod("TickClock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
    }

    private static void Unlock(MainWindowViewModel model)
    {
        model.AskForTab(1);
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
    }

    [AvaloniaFact]
    public void Expired_access_closes_and_clears_the_viewer()
    {
        using var model = new MainWindowViewModel(() => []);
        Unlock(model);
        model.Search.ViewerVisible = true;
        model.Search.ViewerText = "закрытая запись";
        model.Search.ViewerLength = 30;
        Expire(model);
        Assert.False(model.Search.ViewerVisible);
        Assert.Empty(model.Search.ViewerText);
        Assert.Equal(0, model.Search.ViewerLength);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_open_result_cannot_restore_content_after_access_expires(bool unlockAgain)
    {
        using var model = new MainWindowViewModel(() => []);
        Unlock(model);
        var path = Path.Combine(AppPaths.BackupsRoot, "Device1", "LOG.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "закрытая запись");
        SeedDevice(1, "Камера");
        new CollectionLog(AppPaths.Database).Record([new CollectedFile(1, path, 20, null, DateTime.Now)]);
        await model.Search.SearchCommand.ExecuteAsync(null);
        model.Search.Current = Assert.Single(model.Search.Results);
        var opening = model.Search.PlayCommand.ExecuteAsync(null);
        Expire(model);
        if (unlockAgain) Unlock(model);
        await opening;
        Assert.False(model.Search.ViewerVisible);
        Assert.Empty(model.Search.ViewerText);
    }

    [AvaloniaFact]
    public void Locked_access_disables_viewer_commands()
    {
        using var model = new MainWindowViewModel(() => []);
        Assert.False(model.Search.PlayCommand.CanExecute(null));
        Assert.False(model.Search.OpenExternallyCommand.CanExecute(null));
        Assert.False(model.Search.StepForwardCommand.CanExecute(null));
        Assert.False(model.Search.StepBackCommand.CanExecute(null));
    }

    private static void SeedDevice(long id, string name, string person = "", string? firmware = null)
    {
        using (var registry = new DeviceRegistry(AppPaths.Database)) { }
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={AppPaths.Database}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO devices(id,serial,name,person,firmware_id,first_seen,last_seen) VALUES($id,$serial,$name,$person,$firmware,'2026-10-20','2026-10-20')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$serial", "serial" + id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$person", person);
        cmd.Parameters.AddWithValue("$firmware", (object?)firmware ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [AvaloniaTheory]
    [InlineData("7", 7)]
    [InlineData("700", 7)]
    [InlineData("Пост", -1)]
    public async Task Exact_camera_numbers_win_and_ambiguous_names_do_not_pick_a_camera(string query, long expected)
    {
        SeedDevice(1, "Пост 7 и 700");
        SeedDevice(7, "Пост охраны", firmware: "700");
        foreach (var id in new long[] { 1, 7 })
        {
            var path = Path.Combine(AppPaths.BackupsRoot, $"Device{id}", "record.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "запись");
            new CollectionLog(AppPaths.Database).Record([new CollectedFile(id, path, 10, null, DateTime.Now)]);
        }
        ArchiveGuard.Mark(AppPaths.BackupsRoot);
        var model = new SearchViewModel(AppPaths.Database, AppPaths.BackupsRoot) { Camera = query };
        await model.SearchCommand.ExecuteAsync(null);
        if (expected < 0) Assert.Empty(model.Results);
        else Assert.Equal(expected, Assert.Single(model.Results).Row.File.DeviceId);
    }

    [AvaloniaTheory]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(10, 1, false)]
    public void Saving_the_lock_timeout_changes_the_open_session(int previous, int updated, bool allowed)
    {
        new Settings { AlarmSound = false, LockTimeoutMinutes = previous }.Save();
        using var model = new MainWindowViewModel(() => []);
        Unlock(model);
        model.Settings.LockTimeoutMinutes = updated;
        model.Settings.SaveLockTimeoutCommand.Execute(null);
        var access = (AccessGuard)typeof(MainWindowViewModel).GetField("_access", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        Assert.Equal(updated, Settings.Load().LockTimeoutMinutes);
        Assert.Equal(allowed, access.Check(DateTime.Now.AddMinutes(2)));
    }

    [Fact]
    public void Removing_a_legacy_employee_assignment_survives_reopening_the_directory()
    {
        SeedDevice(1, "Камера", person: "Иванов");
        var staff = new StaffDirectory(AppPaths.Database);
        Assert.Equal("Иванов", staff.EmployeeOfDevice(1)!.FullName);
        staff.AssignDevice(1, null);
        Assert.Null(new StaffDirectory(AppPaths.Database).EmployeeOfDevice(1));
        Assert.Equal("Иванов", Assert.Single(staff.Employees()).FullName);
    }

    [AvaloniaFact]
    public async Task Overlapping_device_reloads_do_not_duplicate_rows()
    {
        SeedDevice(1, "Камера");
        var model = new DevicesViewModel(AppPaths.Database);
        await Task.WhenAll(model.Reload(), model.Reload());
        Assert.Single(model.Devices);
    }

    [AvaloniaFact]
    public async Task Overlapping_department_reloads_do_not_duplicate_rows()
    {
        SeedDevice(1, "Камера");
        new StaffDirectory(AppPaths.Database).AddDepartment("Охрана");
        var model = new SearchViewModel(AppPaths.Database);
        await Task.WhenAll(model.ReloadDepartments(), model.ReloadDepartments());
        Assert.Equal(2, model.Departments.Count);
    }

    [AvaloniaFact]
    public async Task Overlapping_log_reloads_do_not_duplicate_rows()
    {
        new ActionLog(AppPaths.Database).Write(ActionLog.Settings, "проверка");
        var model = new LogViewModel(AppPaths.Database);
        await Task.WhenAll(model.Reload(), model.Reload());
        Assert.Single(model.Entries);
    }

    [AvaloniaFact]
    public async Task Cancelling_an_offline_check_does_not_handoff_its_late_result()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var stick = UpdateStick("v2.99");
        var spool = Path.Combine(_dir, "spool");
        var handoff = Path.Combine(_dir, "handoff");
        SetField(model, "_stageOffline", new Func<string, string, string, (string? Tag, string? Error)>(
            (archive, platform, pending) => Updater.StageOffline(archive, platform, pending)));
        SetField(model, "_offlineSpoolDir", spool);
        SetField(model, "_startUpdateService", new Func<string>(() =>
        {
            File.WriteAllText(handoff, "передано");
            return Updater.UpdateStarted;
        }));
        model.Settings.Hint = "отмена проверяется";
        Watch(model, stick);
        model.CancelOfflineUpdateCommand.Execute(null);
        await WaitForOfflineCheck(model);
        Assert.Equal("отмена проверяется", model.Settings.Hint);
        Assert.False(model.OfflineWaiting);
        Assert.False(Directory.Exists(spool));
        Assert.False(File.Exists(handoff));
    }

    [AvaloniaFact]
    public async Task A_late_log_query_cannot_replace_a_new_invalid_period()
    {
        new ActionLog(AppPaths.Database).Write(ActionLog.Settings, "проверка");
        var model = new LogViewModel(AppPaths.Database);
        await model.Reload();
        var pending = model.Reload();
        model.From = "не дата";
        await model.Reload();
        await pending;
        Assert.Empty(model.Entries);
        Assert.Contains("дата", model.Hint);
    }

    private static void SetField(MainWindowViewModel model, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, value);

    private static void Watch(MainWindowViewModel model, string stick) =>
        typeof(MainWindowViewModel).GetMethod("WatchOfflineMedia", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, [new UsbDevice[] { new("update-stick", stick) }]);

    private static async Task WaitForOfflineCheck(MainWindowViewModel model)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while ((bool)typeof(MainWindowViewModel).GetField("_offlineChecking", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!)
        {
            Assert.True(DateTime.UtcNow < limit);
            await Task.Delay(10);
        }
    }

    private string UpdateStick(string tag)
    {
        var stick = Directory.CreateDirectory(Path.Combine(_dir, tag)).FullName;
        var archive = Path.Combine(stick, $"bestcam-station-{tag}-{Updater.Platform()}.tar.gz");
        File.WriteAllText(archive, "пакет");
        File.WriteAllText(archive + ".sha256", Updater.Sha256(archive));
        return stick;
    }

    [AvaloniaFact]
    public async Task Cancelling_after_staging_cleans_only_the_old_package_before_the_next_check()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var first = UpdateStick("v2.99");
        var second = UpdateStick("v2.100");
        var spool = Path.Combine(_dir, "spool");
        var handoff = Path.Combine(_dir, "handoff");
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        SetField(model, "_stageOffline", new Func<string, string, string, (string? Tag, string? Error)>((archive, platform, pending) =>
        {
            var result = Updater.StageOffline(archive, platform, pending);
            if (archive.Contains("v2.99"))
            {
                staged.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            return result;
        }));
        SetField(model, "_offlineSpoolDir", spool);
        SetField(model, "_startUpdateService", new Func<string>(() =>
        {
            File.WriteAllText(handoff, Updater.TakeOfflineSpool(spool)!.Value.Tag);
            return Updater.UpdateStarted;
        }));
        try
        {
            Watch(model, first);
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.CancelOfflineUpdateCommand.Execute(null);
            model.OfflineWaiting = true;
            Watch(model, second);
            Assert.Null(Updater.TakeOfflineSpool(spool));
            release.Set();
            await WaitForOfflineCheck(model);
            Assert.True(model.OfflineWaiting);
            Assert.Null(Updater.TakeOfflineSpool(spool));
            Assert.False(File.Exists(handoff));

            Watch(model, second);
            await WaitForOfflineCheck(model);
            Assert.Equal("v2.100", File.ReadAllText(handoff));
            Assert.Equal("v2.100", Updater.TakeOfflineSpool(spool)!.Value.Tag);
        }
        finally { release.Set(); }
    }

    [AvaloniaFact]
    public async Task Cancelling_pending_cleanup_preserves_another_published_package()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var first = UpdateStick("v2.99");
        var second = UpdateStick("v2.100");
        var spool = Path.Combine(_dir, "spool");
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        SetField(model, "_stageOffline", new Func<string, string, string, (string? Tag, string? Error)>((archive, platform, pending) =>
        {
            var result = Updater.StageOffline(archive, platform, pending);
            staged.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return result;
        }));
        SetField(model, "_offlineSpoolDir", spool);
        SetField(model, "_startUpdateService", new Func<string>(() => throw new InvalidOperationException("Отменённое обновление передано")));
        try
        {
            Watch(model, first);
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            model.CancelOfflineUpdateCommand.Execute(null);
            var package = Assert.Single(Updater.FindOfflineArchives(second, Updater.Platform()));
            Assert.Null(Updater.StageOffline(package.Archive, Updater.Platform(), spool).Error);
            release.Set();
            await WaitForOfflineCheck(model);
            Assert.Equal("v2.100", Updater.TakeOfflineSpool(spool)?.Tag);
        }
        finally { release.Set(); }
    }

    [AvaloniaFact]
    public async Task A_pending_check_does_not_replace_a_package_owned_by_the_updater()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var first = UpdateStick("v2.99");
        var second = UpdateStick("v2.100");
        var spool = Path.Combine(_dir, "spool");
        var package = Assert.Single(Updater.FindOfflineArchives(first, Updater.Platform()));
        Assert.Null(Updater.StageOffline(package.Archive, Updater.Platform(), spool).Error);
        SetField(model, "_offlineSpoolDir", spool);
        SetField(model, "_startUpdateService", new Func<string>(() => throw new InvalidOperationException("Пакет заменён")));

        Watch(model, second);
        await WaitForOfflineCheck(model);

        Assert.Equal("v2.99", Updater.TakeOfflineSpool(spool)?.Tag);
        Assert.True(model.OfflineWaiting);
        Assert.Empty(Directory.GetDirectories(_dir, "spool.pending-*"));
    }

    [AvaloniaFact]
    public async Task Published_offline_package_belongs_to_the_updater_after_waiting_ends()
    {
        using var model = new MainWindowViewModel(() => []) { OfflineWaiting = true };
        var stick = UpdateStick("v2.99");
        var spool = Path.Combine(_dir, "spool");
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        SetField(model, "_offlineSpoolDir", spool);
        SetField(model, "_startUpdateService", new Func<string>(() =>
        {
            starting.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return Updater.UpdateStarted;
        }));
        try
        {
            Watch(model, stick);
            await starting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(model.OfflineWaiting);
            model.CancelOfflineUpdateCommand.Execute(null);
            Assert.Equal("v2.99", Updater.TakeOfflineSpool(spool)?.Tag);
            release.Set();
            await WaitForOfflineCheck(model);
            Assert.Equal("v2.99", Updater.TakeOfflineSpool(spool)?.Tag);
            Assert.Empty(Directory.GetDirectories(_dir, "spool.pending-*"));
        }
        finally { release.Set(); }
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
