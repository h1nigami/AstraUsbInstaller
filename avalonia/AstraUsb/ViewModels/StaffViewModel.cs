using System.Collections.ObjectModel;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Отдел в списке: показывается полным путём, чтобы вложенность была видна.</summary>
public sealed class DepartmentRow
{
    public required long Id { get; init; }
    public required string Path { get; init; }
    public override string ToString() => Path;
}

/// <summary>
/// Карточки сотрудников с номером, именем и телефоном.
/// </summary>
public sealed partial class StaffViewModel : ObservableObject
{
    private readonly string _dbPath;

    public ObservableCollection<Employee> Employees { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    [NotifyCanExecuteChangedFor(nameof(DeleteEmployeeCommand))]
    private Employee? _selected;

    [ObservableProperty] private string _personnelNoInput = "";
    [ObservableProperty] private string _fullNameInput = "";
    [ObservableProperty] private string _phoneInput = "";
    [ObservableProperty] private string _hint = "";

    public string SaveLabel => Selected is null ? "Добавить сотрудника" : "Сохранить изменения";

    public StaffViewModel() : this(AppPaths.Database)
    {
    }

    public StaffViewModel(string dbPath)
    {
        _dbPath = dbPath;
        Reload();
    }

    /// <summary>Выбор в списке переносится в поля: карточка правится на месте.</summary>
    partial void OnSelectedChanged(Employee? value)
    {
        PersonnelNoInput = value?.PersonnelNo ?? "";
        FullNameInput = value?.FullName ?? "";
        PhoneInput = value?.Phone ?? "";
    }

    [RelayCommand]
    public void Reload() => Reload(null);

    private void Reload(string? confirmation)
    {
        var keepEmployee = Selected?.Id;

        // Выбор снимается до очистки: список, который смотрит на коллекцию,
        // при очистке ищет выбранный элемент по прежнему месту и падает.
        Selected = null;

        Employees.Clear();

        try
        {
            // Справочник читается на месте: это десятки строк, доли
            // миллисекунды, и уводить их в сторону значит только добавить
            // мигание списка на ровном месте.
            var staff = new StaffDirectory(_dbPath);

            foreach (var employee in staff.Employees())
                Employees.Add(employee);

            Selected = Employees.FirstOrDefault(e => e.Id == keepEmployee);
            Hint = confirmation ?? (Employees.Count == 0
                ? "Сотрудников пока нет. Введите номер и имя, затем нажмите «Добавить сотрудника»"
                : "");
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось прочитать справочник", e);
        }
    }

    /// <summary>
    /// Сохраняет карточку: выбранную правит, иначе заводит новую. Номер
    /// сотрудника обязателен, по нему станция узнаёт человека в записях камеры.
    /// </summary>
    [RelayCommand]
    private void SaveEmployee()
    {
        var number = PersonnelNoInput.Trim();
        var name = FullNameInput.Trim();

        if (number.Length == 0)
        {
            Hint = "укажите номер сотрудника, он прописан в самой камере";
            return;
        }

        try
        {
            var staff = new StaffDirectory(_dbPath);

            if (staff.FindByPersonnelNo(number) is { } existing && existing.Id != Selected?.Id)
            {
                Hint = $"номер {number} уже занят: {existing.FullName}";
                return;
            }

            long savedId;
            if (Selected is { } current)
            {
                savedId = current.Id;
                staff.UpdateEmployee(current with
                {
                    PersonnelNo = number,
                    FullName = name.Length == 0 ? number : name,
                    Phone = PhoneInput.Trim(),
                });
                Hint = $"карточка {number} сохранена";
            }
            else
            {
                savedId = staff.AddEmployee(name.Length == 0 ? number : name, number,
                    PhoneInput.Trim());
                Hint = $"сотрудник {number} добавлен";
            }

            Reload(Hint);
            Selected = Employees.FirstOrDefault(employee => employee.Id == savedId);
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось сохранить сотрудника", e);
        }
    }

    /// <summary>Готовит поля под новую карточку.</summary>
    [RelayCommand]
    private void NewEmployee()
    {
        Selected = null;
        PersonnelNoInput = "";
        FullNameInput = "";
        PhoneInput = "";
        Hint = "Введите номер и имя, затем нажмите «Добавить сотрудника»";
    }

    private bool CanDeleteEmployee() => Selected is not null;

    /// <summary>Удаляет выбранную карточку из справочника.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteEmployee))]
    private void DeleteEmployee()
    {
        if (Selected is not { } current)
        {
            Hint = "выберите сотрудника в списке";
            return;
        }

        try
        {
            new StaffDirectory(_dbPath).DeleteEmployee(current.Id);
            Hint = $"Сотрудник «{current.FullName}» удалён";
            Reload(Hint);
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось удалить сотрудника", e);
        }
    }
}
