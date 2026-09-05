using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

public sealed class DevicesViewModelTests
{
    [Fact]
    public async Task Selecting_a_camera_keeps_its_employee_when_two_people_have_the_same_name()
    {
        var directory = Directory.CreateTempSubdirectory("astra-devicesvm-").FullName;
        try
        {
            var db = Path.Combine(directory, "devices.db");
            using var registry = new DeviceRegistry(db);
            var camera = registry.ResolveByCard(null, 1, "CAM", "CAM");
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
}
