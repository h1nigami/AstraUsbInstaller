using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Справочник отделов и сотрудников. Главное здесь не потерять накопленное:
/// прежнее текстовое поле «человек» должно превратиться в карточки, а
/// перестановки в структуре не должны уносить с собой людей.
/// </summary>
public sealed class StaffDirectoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-staff-").FullName;
    private readonly string _db;

    public StaffDirectoryTests()
    {
        _db = Path.Combine(_dir, "devices.db");
        // Справочник надстраивается над существующей базой устройств.
        using var registry = new DeviceRegistry(_db);
    }

    private StaffDirectory NewDirectory() => new(_db);

    [Fact]
    public void Departments_form_a_tree()
    {
        var staff = NewDirectory();
        var guard = staff.AddDepartment("Охрана", "OHR");
        var shift = staff.AddDepartment("Смена 1", "S1", guard);

        Assert.Equal("Охрана / Смена 1", staff.DepartmentPath(shift));
    }

    [Fact]
    public void Deleting_a_department_lifts_its_children_instead_of_dropping_them()
    {
        var staff = NewDirectory();
        var guard = staff.AddDepartment("Охрана");
        var middle = staff.AddDepartment("Участок", parentId: guard);
        var shift = staff.AddDepartment("Смена 1", parentId: middle);
        var person = staff.AddEmployee("Иванов И.И.", departmentId: middle);

        staff.DeleteDepartment(middle);

        Assert.Equal("Охрана / Смена 1", staff.DepartmentPath(shift));
        var moved = staff.Employees().Single(e => e.Id == person);
        Assert.Equal(guard, moved.DepartmentId);
    }

    [Fact]
    public void Employee_card_keeps_every_field()
    {
        var staff = NewDirectory();
        var dep = staff.AddDepartment("Охрана");

        var id = staff.AddEmployee("Петров П.П.", "1024", "+7 900 000-00-00", "инспектор", dep);

        var saved = staff.Employees().Single(e => e.Id == id);
        Assert.Equal("1024", saved.PersonnelNo);
        Assert.Equal("Петров П.П.", saved.FullName);
        Assert.Equal("+7 900 000-00-00", saved.Phone);
        Assert.Equal("инспектор", saved.Role);
        Assert.Equal(dep, saved.DepartmentId);
        Assert.True(saved.Active);
    }

    [Fact]
    public void Personnel_number_is_unique()
    {
        var staff = NewDirectory();
        staff.AddEmployee("Первый", "1024");

        Assert.Throws<SqliteException>(() => staff.AddEmployee("Второй", "1024"));
    }

    [Fact]
    public void Dismissed_employee_is_deactivated_not_erased()
    {
        var staff = NewDirectory();
        var id = staff.AddEmployee("Сидоров С.С.");

        staff.Deactivate(id);

        Assert.DoesNotContain(staff.Employees(activeOnly: true), e => e.Id == id);
        var kept = staff.Employees().Single(e => e.Id == id);
        Assert.False(kept.Active);
        Assert.Equal("Сидоров С.С.", kept.FullName);
    }

    [Fact]
    public void Device_can_be_assigned_to_an_employee()
    {
        using var registry = new DeviceRegistry(_db);
        var mount = Path.Combine(_dir, "card");
        Directory.CreateDirectory(mount);
        var deviceId = registry.ResolveDeviceId(mount, "SER-1", "cam", "sdb1");

        var staff = NewDirectory();
        var person = staff.AddEmployee("Иванов И.И.", "77");
        staff.AssignDevice(deviceId, person);

        Assert.Equal("Иванов И.И.", staff.EmployeeOfDevice(deviceId)?.FullName);
    }

    [Fact]
    public void Old_person_field_becomes_a_card()
    {
        // База, накопленная прежней версией: человек записан строкой у устройства.
        using (var registry = new DeviceRegistry(_db))
        {
            var mount = Path.Combine(_dir, "legacy");
            Directory.CreateDirectory(mount);
            var deviceId = registry.ResolveDeviceId(mount, "SER-9", "cam", "sdc1");

            using var db = new SqliteConnection($"Data Source={_db}");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE devices SET person = 'Кузнецов К.К.' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.ExecuteNonQuery();
        }

        var staff = NewDirectory();

        Assert.Contains(staff.Employees(), e => e.FullName == "Кузнецов К.К.");
    }

    [Fact]
    public void Migration_does_not_duplicate_on_a_second_run()
    {
        using (var registry = new DeviceRegistry(_db))
        {
            var mount = Path.Combine(_dir, "legacy2");
            Directory.CreateDirectory(mount);
            var deviceId = registry.ResolveDeviceId(mount, "SER-8", "cam", "sdd1");

            using var db = new SqliteConnection($"Data Source={_db}");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE devices SET person = 'Смирнов С.С.' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.ExecuteNonQuery();
        }

        NewDirectory();
        var staff = NewDirectory();

        Assert.Single(staff.Employees(), e => e.FullName == "Смирнов С.С.");
    }

    [Fact]
    public void Personnel_number_from_the_camera_creates_a_card_and_binds_it()
    {
        using var registry = new DeviceRegistry(_db);
        var device = registry.ResolveByCard(null, 1, "BESTCAM", "sdb1");
        var staff = NewDirectory();

        var person = staff.AssignByPersonnelNo(device, "222222");

        Assert.NotNull(person);
        Assert.Equal("222222", person.PersonnelNo);
        Assert.Equal(person.Id, staff.EmployeeOfDevice(device)?.Id);
    }

    [Fact]
    public void The_same_number_does_not_create_a_second_card()
    {
        using var registry = new DeviceRegistry(_db);
        var first = registry.ResolveByCard(null, 1, "CAM1", "sdb1");
        var second = registry.ResolveByCard(null, 1, "CAM2", "sdc1");
        var staff = NewDirectory();

        staff.AssignByPersonnelNo(first, "222222");
        staff.AssignByPersonnelNo(second, "222222");

        Assert.Single(staff.Employees(), e => e.PersonnelNo == "222222");
    }

    [Fact]
    public void An_existing_card_is_reused_with_its_name_and_department()
    {
        using var registry = new DeviceRegistry(_db);
        var device = registry.ResolveByCard(null, 1, "BESTCAM", "sdb1");
        var staff = NewDirectory();
        var guard = staff.AddDepartment("Охрана");
        staff.AddEmployee("Смирнов С.С.", "222222", departmentId: guard);

        var person = staff.AssignByPersonnelNo(device, "222222");

        Assert.Equal("Смирнов С.С.", person?.FullName);
        Assert.Equal(guard, person?.DepartmentId);
        Assert.Single(staff.Employees());
    }

    [Fact]
    public void A_new_number_moves_the_camera_to_another_employee()
    {
        using var registry = new DeviceRegistry(_db);
        var device = registry.ResolveByCard(null, 1, "BESTCAM", "sdb1");
        var staff = NewDirectory();

        staff.AssignByPersonnelNo(device, "222222");
        var next = staff.AssignByPersonnelNo(device, "333333");

        // Камеру передали другому: в её записях уже стоит новый номер.
        Assert.Equal("333333", next?.PersonnelNo);
        Assert.Equal("333333", staff.EmployeeOfDevice(device)?.PersonnelNo);
    }

    [Fact]
    public void Without_a_number_the_previous_binding_survives()
    {
        using var registry = new DeviceRegistry(_db);
        var device = registry.ResolveByCard(null, 1, "BESTCAM", "sdb1");
        var staff = NewDirectory();
        var employee = staff.AddEmployee("Смирнов С.С.", "222222");
        staff.AssignDevice(device, employee);

        var person = staff.AssignByPersonnelNo(device, null);

        Assert.Equal(employee, person?.Id);
        Assert.Equal(employee, staff.EmployeeOfDevice(device)?.Id);
        Assert.Single(staff.Employees());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
