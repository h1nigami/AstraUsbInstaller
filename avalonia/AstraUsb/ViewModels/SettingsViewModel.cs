using System.Collections.ObjectModel;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка разметки гнёзд.</summary>
public sealed partial class SlotRow : ObservableObject
{
    [ObservableProperty] private int _slot;
    [ObservableProperty] private string _portPath = "";

    public string SlotLabel => $"окно {Slot + 1}";
    public string PortLabel => string.IsNullOrEmpty(PortPath) ? "не размечено" : PortPath;
}

/// <summary>
/// Вкладка «Настройки»: правила хранилища, разметка гнёзд, справочник
/// сотрудников. На экране 1024x600 всё это не помещается в один столбец,
/// поэтому раскладывается в две колонки.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _dbPath;
    private readonly PortMap _portMap;
    private Settings _settings;

    public ObservableCollection<SlotRow> Slots { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();
    public ObservableCollection<Department> Departments { get; } = new();

    public string[] StorageModes { get; } = ["предупреждать", "перезаписывать старые"];

    [ObservableProperty] private string _backupRoot = "";
    [ObservableProperty] private int _minFreeGb;
    [ObservableProperty] private int _stationNumber;
    [ObservableProperty] private int _storageModeIndex;
    [ObservableProperty] private bool _deleteVideoAfterCopy;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _hint = "";

    [ObservableProperty] private string _employeeName = "";
    [ObservableProperty] private string _employeeNo = "";
    [ObservableProperty] private Department? _employeeDepartment;
    [ObservableProperty] private string _departmentName = "";

    public SettingsViewModel() : this(AppPaths.Database)
    {
    }

    public SettingsViewModel(string dbPath)
    {
        _dbPath = dbPath;
        _portMap = new PortMap(dbPath);
        _settings = Settings.Load();

        BackupRoot = _settings.BackupRoot;
        MinFreeGb = _settings.MinFreeGb;
        StationNumber = _settings.StationNumber;
        StorageModeIndex = _settings.StorageMode == StorageMode.Overwrite ? 1 : 0;
        DeleteVideoAfterCopy = _settings.DeleteVideoAfterCopy;

        ReloadSlots();
        ReloadStaff();
    }

    [RelayCommand]
    private void SaveStorage()
    {
        _settings.BackupRoot = BackupRoot.Trim();
        _settings.MinFreeGb = Math.Max(1, MinFreeGb);
        _settings.StationNumber = Math.Clamp(StationNumber, 0, 99);
        _settings.StorageMode = StorageModeIndex == 1 ? StorageMode.Overwrite : StorageMode.Warn;
        _settings.DeleteVideoAfterCopy = DeleteVideoAfterCopy;

        Hint = _settings.Save()
            ? "настройки хранилища сохранены"
            : "не удалось записать настройки — проверьте права на папку data";
    }

    // --- Разметка гнёзд -----------------------------------------------------

    [RelayCommand]
    public void ReloadSlots()
    {
        Slots.Clear();
        var assigned = _portMap.All();

        for (var slot = 0; slot < MainWindowViewModel.PortCount; slot++)
        {
            var port = assigned.FirstOrDefault(pair => pair.Value == slot).Key ?? "";
            Slots.Add(new SlotRow { Slot = slot, PortPath = port });
        }
    }

    /// <summary>
    /// Закрепляет за выбранным окном то гнездо, в котором сейчас стоит
    /// единственная подключённая камера. Так размечают станцию по инструкции:
    /// втыкают камеру в отсек и нажимают «сопоставить».
    /// </summary>
    [RelayCommand]
    private void MapSlot(SlotRow? row)
    {
        if (row is null)
            return;

        var connected = UsbWatcher.List()
            .Where(d => !string.IsNullOrEmpty(d.PortPath))
            .ToArray();

        if (connected.Length == 0)
        {
            Hint = "вставьте камеру в размечаемый отсек";
            return;
        }

        if (connected.Length > 1)
        {
            Hint = "для разметки оставьте подключённой одну камеру";
            return;
        }

        _portMap.Assign(connected[0].PortPath!, row.Slot);
        Hint = $"окно {row.Slot + 1} закреплено за гнездом {connected[0].PortPath}";
        ReloadSlots();
    }

    [RelayCommand]
    private void ClearSlots()
    {
        _portMap.Clear();
        Hint = "разметка снята, окна снова занимаются по порядку подключения";
        ReloadSlots();
    }

    // --- Справочник ---------------------------------------------------------

    [RelayCommand]
    public void ReloadStaff()
    {
        Employees.Clear();
        Departments.Clear();

        var staff = new StaffDirectory(_dbPath);
        foreach (var department in staff.Departments())
            Departments.Add(department);
        foreach (var employee in staff.Employees())
            Employees.Add(employee);
    }

    [RelayCommand]
    private void AddDepartment()
    {
        if (string.IsNullOrWhiteSpace(DepartmentName))
        {
            Hint = "введите название отдела";
            return;
        }

        new StaffDirectory(_dbPath).AddDepartment(DepartmentName.Trim());
        Hint = $"отдел «{DepartmentName.Trim()}» добавлен";
        DepartmentName = "";
        ReloadStaff();
    }

    [RelayCommand]
    private void AddEmployee()
    {
        if (string.IsNullOrWhiteSpace(EmployeeName))
        {
            Hint = "введите фамилию и инициалы";
            return;
        }

        try
        {
            new StaffDirectory(_dbPath).AddEmployee(
                EmployeeName.Trim(), EmployeeNo.Trim(), departmentId: EmployeeDepartment?.Id);
            Hint = $"сотрудник {EmployeeName.Trim()} добавлен";
            EmployeeName = "";
            EmployeeNo = "";
            ReloadStaff();
        }
        catch (Exception e)
        {
            Hint = e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                ? "такой персональный номер уже занят"
                : $"не удалось добавить: {e.Message}";
        }
    }
}
