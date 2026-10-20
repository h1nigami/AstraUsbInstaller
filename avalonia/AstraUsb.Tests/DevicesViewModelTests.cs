using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class DevicesViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-device-actions-").FullName;
    private readonly string _previous = AppPaths.Root;

    public DevicesViewModelTests() => AppPaths.Root = _root;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renaming_or_clearing_a_name_records_the_camera_id_and_new_name(bool clear)
    {
        var model = await Model();
        model.NameInput = clear ? " " : " Патруль 7 ";

        await model.RenameCommand.ExecuteAsync(null);

        using var registry = new DeviceRegistry(AppPaths.Database);
        Assert.Equal(clear ? "" : "Патруль 7", Assert.Single(registry.ListDevices()).Name);
        var action = Assert.Single(new ActionLog(AppPaths.Database).Between(DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(ActionLog.Settings, action.Kind);
        Assert.Contains("1234567", action.Message);
        Assert.Contains(clear ? "имя снято" : "Патруль 7", action.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assigning_or_clearing_an_employee_records_the_camera_id_and_employee(bool clear)
    {
        var model = await Model();
        model.EmployeeInput = clear ? null : Assert.Single(model.Employees);

        await model.AssignCommand.ExecuteAsync(null);

        using var registry = new DeviceRegistry(AppPaths.Database);
        Assert.Equal(clear ? null : Assert.Single(model.Employees).Id, Assert.Single(registry.ListDevices()).EmployeeId);
        var action = Assert.Single(new ActionLog(AppPaths.Database).Between(DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(ActionLog.Settings, action.Kind);
        Assert.Contains("1234567", action.Message);
        Assert.Contains(clear ? "больше ни за кем" : "Иванов Иван", action.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_device_edit_records_the_camera_id_and_cause(bool rename)
    {
        var model = await Model();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.WriteAllText(AppPaths.Database, "не база данных");

        if (rename) await model.RenameCommand.ExecuteAsync(null);
        else await model.AssignCommand.ExecuteAsync(null);

        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("1234567", log);
        Assert.Contains("SqliteException", log);
        Assert.DoesNotContain("SQLite", model.Hint);
    }

    private async Task<DevicesViewModel> Model()
    {
        var log = Directory.CreateDirectory(Path.Combine(_root, "card", "LOG")).FullName;
        File.WriteAllText(Path.Combine(log, "log.txt"), "#ID:1234567\n");
        using (var registry = new DeviceRegistry(AppPaths.Database))
        {
            var id = registry.ResolveByCard(Path.GetDirectoryName(log), 1, "CAM", "CAM");
            registry.Rename(id, "Старое имя");
        }
        new StaffDirectory(AppPaths.Database).AddEmployee("Иванов Иван", "111111");
        var model = new DevicesViewModel(AppPaths.Database);
        for (var attempt = 0; model.Devices.Count == 0 && attempt < 100; attempt++)
            await Task.Delay(10);
        model.Selected = Assert.Single(model.Devices);
        return model;
    }

    [Fact]
    public async Task Selecting_a_camera_keeps_its_employee_when_two_people_have_the_same_name()
    {
        var directory = Directory.CreateTempSubdirectory("astra-devicesvm-").FullName;
        try
        {
            var db = Path.Combine(directory, "devices.db");
            using var registry = new DeviceRegistry(db);
            var card = Path.Combine(directory, "card", "DCIM");
            Directory.CreateDirectory(card);
            File.WriteAllText(Path.Combine(card, "A11_1234567_222222_20260915120000_0001.mp4"), "video");
            var camera = registry.ResolveByCard(Path.GetDirectoryName(card), 1, "CAM", "CAM");
            var staff = new StaffDirectory(db);
            staff.AddEmployee("Иванов Иван", "111111");
            var owner = staff.AddEmployee("Иванов Иван", "222222");
            staff.AssignDevice(camera, owner);
            var model = new DevicesViewModel(db);
            for (var attempt = 0; model.Devices.Count == 0 && attempt < 100; attempt++)
                await Task.Delay(20);

            model.Selected = Assert.Single(model.Devices);

            Assert.Equal(owner, model.EmployeeInput?.Id);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
