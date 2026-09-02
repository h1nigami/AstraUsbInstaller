using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Вкладка «Сотрудники»: здесь оператор заводит людей и отделы. Терять при
/// этом никого нельзя: уволенный остаётся в базе, а удалённый отдел отдаёт
/// своих людей выше.
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
    public void A_dismissed_employee_stays_in_the_list()
    {
        var model = NewModel();
        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Смирнов С.С.";
        model.SaveEmployeeCommand.Execute(null);
        model.Selected = model.Employees[0];

        model.DeactivateCommand.Execute(null);

        Assert.False(Assert.Single(model.Employees).Active);
    }

    [Fact]
    public void A_department_can_be_nested_under_the_selected_one()
    {
        var model = NewModel();
        model.DepartmentNameInput = "Охрана";
        model.AddDepartmentCommand.Execute(null);

        model.SelectedDepartment = model.Departments.Single(d => d.Path == "Охрана");
        model.DepartmentNameInput = "Смена 1";
        model.AddDepartmentCommand.Execute(null);

        Assert.Contains(model.Departments, d => d.Path == "Охрана / Смена 1");
    }

    [Fact]
    public void Deleting_a_department_keeps_its_employees()
    {
        var model = NewModel();
        model.DepartmentNameInput = "Охрана";
        model.AddDepartmentCommand.Execute(null);

        model.PersonnelNoInput = "222222";
        model.FullNameInput = "Смирнов С.С.";
        model.DepartmentInput = model.Departments[0];
        model.SaveEmployeeCommand.Execute(null);

        model.SelectedDepartment = model.Departments[0];
        model.DeleteDepartmentCommand.Execute(null);

        Assert.Empty(model.Departments);
        Assert.Equal("Смирнов С.С.", Assert.Single(model.Employees).FullName);
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
