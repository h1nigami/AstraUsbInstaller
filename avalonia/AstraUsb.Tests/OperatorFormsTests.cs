using AstraUsb.Services;
using AstraUsb.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

public sealed class OperatorFormsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-forms-").FullName;
    private string Db => Path.Combine(_dir, "devices.db");

    [Fact]
    public void Employee_save_and_department_actions_keep_their_confirmation()
    {
        var model = new StaffViewModel(Db);
        model.PersonnelNoInput = "111";
        model.FullNameInput = "Иван";
        model.SaveEmployeeCommand.Execute(null);
        Assert.Contains("добавлен", model.Hint);
        model.Selected = Assert.Single(model.Employees);
        model.FullNameInput = "Иван Иванов";
        model.SaveEmployeeCommand.Execute(null);
        Assert.Contains("сохранена", model.Hint);
        Assert.Equal("Иван Иванов", Assert.Single(model.Employees).FullName);
        model.DeactivateCommand.Execute(null);
        Assert.Contains("уволенный", model.Hint);
        Assert.False(Assert.Single(model.Employees).Active);
        model.DepartmentNameInput = "Охрана";
        model.AddDepartmentCommand.Execute(null);
        Assert.Contains("добавлен", model.Hint);
        model.SelectedDepartment = Assert.Single(model.Departments);
        model.DeleteDepartmentCommand.Execute(null);
        Assert.Contains("удалён", model.Hint);
        Assert.Empty(model.Departments);
    }

    [Fact]
    public void Editing_an_employee_to_an_existing_number_explains_the_conflict()
    {
        var staff = new StaffDirectory(Db);
        staff.AddEmployee("Иван", "111");
        var other = staff.AddEmployee("Пётр", "222");
        var model = new StaffViewModel(Db);
        model.Selected = model.Employees.Single(e => e.Id == other);
        model.PersonnelNoInput = "111";
        model.SaveEmployeeCommand.Execute(null);
        Assert.Contains("уже занят", model.Hint);
        Assert.DoesNotContain("UNIQUE", model.Hint);
        Assert.Equal("222", staff.Employees().Single(e => e.Id == other).PersonnelNo);
    }

    [Fact]
    public async Task Camera_name_and_assignment_keep_their_confirmation_after_reload()
    {
        using var registry = new DeviceRegistry(Db);
        var camera = registry.ResolveByCard(null, 1, "CAM", "CAM");
        var staff = new StaffDirectory(Db);
        staff.AddEmployee("Иван", "111");
        var model = new DevicesViewModel(Db);
        await Wait(() => model.Devices.Count > 0);
        model.Selected = Assert.Single(model.Devices);
        model.NameInput = "Проходная";
        await model.RenameCommand.ExecuteAsync(null);
        Assert.Contains("Проходная", model.Hint);
        Assert.Equal("Проходная", registry.GetDeviceName(camera));
        model.EmployeeInput = Assert.Single(model.Employees);
        await model.AssignCommand.ExecuteAsync(null);
        Assert.Contains("закреплена за Иван", model.Hint);
        Assert.Equal("Иван", staff.EmployeeOfDevice(camera)?.FullName);
    }

    [Theory]
    [InlineData("31.12.9999", "31.12.9999")]
    [InlineData("05.09.2026", "05.09.2026")]
    public async Task Log_includes_the_last_fractional_second_even_at_the_maximum_date(string from, string to)
    {
        var model = new LogViewModel(Db);
        await Wait(() => model.Hint.Length > 0);
        using var db = new SqliteConnection($"Data Source={Db}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO action_log(at, kind, message) VALUES ($at, '', 'событие')";
        command.Parameters.AddWithValue("$at", from == "31.12.9999"
            ? "9999-12-31T23:59:59.999999" : "2026-09-05T23:59:59.500000");
        command.ExecuteNonQuery();
        model.From = from;
        model.To = to;
        await model.Reload();
        Assert.Single(model.Entries);
    }

    [Fact]
    public async Task Log_rejects_a_reversed_period()
    {
        var model = new LogViewModel(Db);
        await Wait(() => model.Hint.Length > 0);
        model.From = "06.09.2026";
        model.To = "05.09.2026";
        await model.Reload();
        Assert.Contains("начала", model.Hint);
    }

    private static async Task Wait(Func<bool> ready)
    {
        for (var i = 0; i < 200 && !ready(); i++)
            await Task.Delay(10);
        Assert.True(ready());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
