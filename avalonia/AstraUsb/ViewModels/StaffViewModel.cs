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
/// Вкладка «Сотрудники»: отделы и карточки людей.
///
/// Номер сотрудника прописан в самой камере и виден на плитке, но человека
/// за этим номером знает только оператор. Здесь он заводит карточку с именем,
/// телефоном и отделом, а закрепляет её за камерой на вкладке «Устройства».
/// </summary>
public sealed partial class StaffViewModel : ObservableObject
{
    private readonly string _dbPath;

    public ObservableCollection<DepartmentRow> Departments { get; } = new();
    public ObservableCollection<Employee> Employees { get; } = new();

    [ObservableProperty] private Employee? _selected;
    [ObservableProperty] private DepartmentRow? _selectedDepartment;

    [ObservableProperty] private string _personnelNoInput = "";
    [ObservableProperty] private string _fullNameInput = "";
    [ObservableProperty] private string _phoneInput = "";
    [ObservableProperty] private DepartmentRow? _departmentInput;
    [ObservableProperty] private string _departmentNameInput = "";
    [ObservableProperty] private string _hint = "";

    /// <summary>Отделов ещё нет: список показывает подсказку вместо пустоты.</summary>
    public bool NoDepartments => Departments.Count == 0;

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
        DepartmentInput = Departments.FirstOrDefault(d => d.Id == value?.DepartmentId);
    }

    [RelayCommand]
    public void Reload()
    {
        var keepEmployee = Selected?.Id;
        Departments.Clear();
        Employees.Clear();

        try
        {
            var staff = new StaffDirectory(_dbPath);

            foreach (var department in staff.Departments())
                Departments.Add(new DepartmentRow
                {
                    Id = department.Id,
                    Path = staff.DepartmentPath(department.Id),
                });

            foreach (var employee in staff.Employees())
                Employees.Add(employee);

            Selected = Employees.FirstOrDefault(e => e.Id == keepEmployee);
            OnPropertyChanged(nameof(NoDepartments));
            Hint = Employees.Count == 0
                ? "сотрудников пока нет; заполните номер и имя, затем нажмите «Сохранить»"
                : "";
        }
        catch (Exception e)
        {
            Hint = $"не удалось прочитать справочник: {e.Message}";
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

            if (Selected is { } current)
            {
                staff.UpdateEmployee(current with
                {
                    PersonnelNo = number,
                    FullName = name.Length == 0 ? number : name,
                    Phone = PhoneInput.Trim(),
                    DepartmentId = DepartmentInput?.Id,
                });
                Hint = $"карточка {number} сохранена";
            }
            else if (staff.FindByPersonnelNo(number) is { } existing)
            {
                Hint = $"номер {number} уже занят: {existing.FullName}";
                return;
            }
            else
            {
                staff.AddEmployee(name.Length == 0 ? number : name, number,
                    PhoneInput.Trim(), departmentId: DepartmentInput?.Id);
                Hint = $"сотрудник {number} добавлен";
            }

            Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось сохранить: {e.Message}";
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
        DepartmentInput = null;
        Hint = "заполните номер и имя, затем нажмите «Сохранить»";
    }

    /// <summary>
    /// Уволенный сотрудник помечается неактивным, а не удаляется: за ним
    /// числятся прошлые записи, и они должны остаться подписанными.
    /// </summary>
    [RelayCommand]
    private void Deactivate()
    {
        if (Selected is not { } current)
        {
            Hint = "выберите сотрудника в списке";
            return;
        }

        try
        {
            new StaffDirectory(_dbPath).Deactivate(current.Id);
            Hint = $"{current.FullName} отмечен как уволенный, прошлые записи остались за ним";
            Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось изменить состояние: {e.Message}";
        }
    }

    /// <summary>
    /// Добавляет отдел. Если в списке выбран отдел, новый становится
    /// подчинённым ему.
    /// </summary>
    [RelayCommand]
    private void AddDepartment()
    {
        var name = DepartmentNameInput.Trim();
        if (name.Length == 0)
        {
            Hint = "введите название отдела";
            return;
        }

        try
        {
            new StaffDirectory(_dbPath).AddDepartment(name, parentId: SelectedDepartment?.Id);
            Hint = SelectedDepartment is { } parent
                ? $"«{name}» добавлен внутрь «{parent.Path}»"
                : $"отдел «{name}» добавлен";
            DepartmentNameInput = "";
            Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось добавить отдел: {e.Message}";
        }
    }

    /// <summary>
    /// Удаляет отдел. Подчинённые отделы и сотрудники поднимаются к родителю
    /// удалённого, чтобы никто не потерялся.
    /// </summary>
    [RelayCommand]
    private void DeleteDepartment()
    {
        if (SelectedDepartment is not { } department)
        {
            Hint = "выберите отдел в списке";
            return;
        }

        try
        {
            if (!new StaffDirectory(_dbPath).DeleteDepartment(department.Id))
            {
                Hint = $"в отделе «{department.Path}» есть сотрудники, сначала переведите их";
                return;
            }

            Hint = $"отдел «{department.Path}» удалён";
            Reload();
        }
        catch (Exception e)
        {
            Hint = $"не удалось удалить отдел: {e.Message}";
        }
    }
}
