using System.Collections.ObjectModel;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка списка камер.</summary>
public sealed partial class DeviceRow : ObservableObject
{
    [ObservableProperty] private long _id;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _employee = "";
    [ObservableProperty] private string _department = "";
    [ObservableProperty] private string _firstSeen = "";
    [ObservableProperty] private string _lastSeen = "";

    /// <summary>
    /// Как камера подписана на экране: имя, если оператор его задал, иначе
    /// номер с карты, тот же, что показывает плитка.
    /// </summary>
    public string Display => !string.IsNullOrEmpty(Name) ? Name
        : !string.IsNullOrEmpty(Number) ? Number
        : Id.ToString();
}

/// <summary>
/// Вкладка «Устройства»: список камер, присвоение имени и закрепление за
/// сотрудником. Номер камеры не редактируется: он приходит от аппарата.
/// </summary>
public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly string _dbPath;

    public ObservableCollection<DeviceRow> Devices { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();

    [ObservableProperty]
    private DeviceRow? _selected;

    [ObservableProperty]
    private string _nameInput = "";

    [ObservableProperty]
    private Employee? _employeeInput;

    [ObservableProperty]
    private string _hint = "";

    public DevicesViewModel() : this(AppPaths.Database)
    {
    }

    public DevicesViewModel(string dbPath)
    {
        _dbPath = dbPath;
        _ = Reload();
    }

    partial void OnSelectedChanged(DeviceRow? value)
    {
        NameInput = value?.Name ?? "";
        EmployeeInput = Employees.FirstOrDefault(e => e.FullName == value?.Employee);
    }

    [RelayCommand]
    public async Task Reload()
    {
        // Выбор снимается до очистки: иначе список, который на неё смотрит,
        // ищет выбранный элемент по прежнему месту и падает.
        var kept = Selected?.Id;
        Selected = null;
        EmployeeInput = null;

        Devices.Clear();
        Employees.Clear();

        try
        {
            // Списки читаются в стороне: при переходе в раздел интерфейс не
            // должен ждать базу.
            var staff = new StaffDirectory(_dbPath);
            var employees = await Task.Run(() => staff.Employees(activeOnly: true));
            foreach (var employee in employees)
                Employees.Add(employee);

            using var registry = new DeviceRegistry(_dbPath);
            var devices = await Task.Run(() => registry.ListDevices());
            foreach (var device in devices)
            {
                Devices.Add(new DeviceRow
                {
                    Id = device.Id,
                    Name = device.Name,
                    Number = device.FirmwareId,
                    Label = device.Label,
                    Employee = device.EmployeeName,
                    Department = staff.DepartmentPath(device.DepartmentId),
                    FirstSeen = Short(device.FirstSeen),
                    LastSeen = Short(device.LastSeen),
                });
            }

            Selected = Devices.FirstOrDefault(d => d.Id == kept);
            Hint = Devices.Count == 0 ? "камеры ещё не подключались" : "";
        }
        catch (Exception e)
        {
            Hint = $"не удалось прочитать список: {e.Message}";
        }
    }

    /// <summary>Присваивает камере имя. Папка её копий при этом не переименовывается.</summary>
    [RelayCommand]
    private void Rename()
    {
        if (Selected is not { } row)
        {
            Hint = "выберите камеру в списке";
            return;
        }

        try
        {
            using var registry = new DeviceRegistry(_dbPath);
            registry.Rename(row.Id, NameInput.Trim());
            Hint = string.IsNullOrWhiteSpace(NameInput)
                ? $"имя снято, камера снова показывается номером {row.Id}"
                : $"камера {row.Id} теперь «{NameInput.Trim()}»";
            _ = Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось переименовать: {e.Message}";
        }
    }

    /// <summary>Закрепляет камеру за сотрудником.</summary>
    [RelayCommand]
    private void Assign()
    {
        if (Selected is not { } row)
        {
            Hint = "выберите камеру в списке";
            return;
        }

        try
        {
            var staff = new StaffDirectory(_dbPath);
            staff.AssignDevice(row.Id, EmployeeInput?.Id);
            Hint = EmployeeInput is null
                ? $"камера {row.Id} больше ни за кем не закреплена"
                : $"камера {row.Id} закреплена за {EmployeeInput.FullName}";
            _ = Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось закрепить: {e.Message}";
        }
    }

    /// <summary>Даты в базе хранятся с микросекундами, оператору нужны минуты.</summary>
    private static string Short(string stamp) =>
        DateTime.TryParse(stamp, out var moment) ? moment.ToString("dd.MM.yy HH:mm") : stamp;
}
