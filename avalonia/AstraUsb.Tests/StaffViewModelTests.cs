using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Создание, редактирование и удаление карточек сотрудников.
/// </summary>
public sealed class StaffViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-staffvm-").FullName;
    private readonly string _db;

    public StaffViewModelTests()
    {
        _db = Path.Combine(_dir, "devices.db");
        using var registry = new DeviceRegistry(_db);
    }

    private StaffViewModel NewModel() => new(_db);

    [Fact]
    public void A_new_card_needs_a_number()
    {
        var model = NewModel();
        model.FullNameInput = "Смирнов С.С.";

        model.SaveEmployeeCommand.Execute(null);

        Assert.Empty(model.Employees);
        Assert.Contains("номер", model.Hint);
    }

    [Fact]
    public void Saving_adds_the_employee_to_the_list()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Смирнов С.С.";
        model.PhoneInput = "+7 900 000-00-00";

        model.SaveEmployeeCommand.Execute(null);

        var employee = Assert.Single(model.Employees);
        Assert.Equal("222222", employee.PersonnelNo);
        Assert.Equal("Смирнов С.С.", employee.FullName);
        Assert.Equal("+7 900 000-00-00", employee.Phone);
    }

    [Fact]
    public void A_card_without_a_name_is_labelled_by_its_number()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";

        model.SaveEmployeeCommand.Execute(null);

        Assert.Equal("222222", Assert.Single(model.Employees).FullName);
    }

    [Fact]
    public void An_occupied_number_is_not_taken_twice()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Смирнов С.С.";
        model.SaveEmployeeCommand.Execute(null);

        model.NewEmployeeCommand.Execute(null);
        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Петров П.П.";
        model.SaveEmployeeCommand.Execute(null);

        Assert.Single(model.Employees);
        Assert.Contains("уже занят", model.Hint);
    }

    [Fact]
    public void Selecting_an_employee_fills_the_fields_and_saving_updates_the_card()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";
        model.SaveEmployeeCommand.Execute(null);

        // Карточку завели номером, имя вписывают следующим шагом.
        model.Selected = model.Employees[0];
        Assert.Equal("222222", model.PersonnelNoInput);

        model.FullNameInput = "Смирнов С.С.";
        model.SaveEmployeeCommand.Execute(null);

        Assert.Equal("Смирнов С.С.", Assert.Single(model.Employees).FullName);
    }

    [Fact]
    public void A_deleted_employee_is_removed_from_the_list_and_database()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Смирнов С.С.";
        model.SaveEmployeeCommand.Execute(null);
        model.Selected = model.Employees[0];

        model.DeleteEmployeeCommand.Execute(null);

        Assert.Empty(model.Employees);
        Assert.Empty(new StaffDirectory(_db).Employees());
        Assert.Null(model.Selected);
        Assert.False(model.DeleteEmployeeCommand.CanExecute(null));
    }

    [Fact]
    public void Reload_picks_up_a_card_added_elsewhere()
    {
        var model = NewModel();
        Assert.Empty(model.Employees);

        new StaffDirectory(_db).AddEmployee("Петров П.П.", "333333");
        model.ReloadCommand.Execute(null);

        Assert.Equal("333333", Assert.Single(model.Employees).PersonnelNo);
    }

    [Fact]
    public void Editing_a_legacy_card_keeps_the_existing_department_and_role()
    {
        var staff = new StaffDirectory(_db);
        var department = staff.AddDepartment("Охрана");
        var employee = staff.AddEmployee("Иванов И.И.", "77", role: "инспектор", departmentId: department);
        var model = NewModel();
        model.Selected = model.Employees.Single(person => person.Id == employee);
        model.PhoneInput = "+7 900 123-45-67";

        model.SaveEmployeeCommand.Execute(null);

        var saved = Assert.Single(staff.Employees());
        Assert.Equal(department, saved.DepartmentId);
        Assert.Equal("инспектор", saved.Role);
        Assert.Equal("+7 900 123-45-67", saved.Phone);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Файл базы может ещё держаться, для временной папки это неважно.
        }
    }
}
