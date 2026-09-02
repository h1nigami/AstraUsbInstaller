using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AstraUsb.ViewModels;

/// <summary>Состояние гнезда, как его видит оператор.</summary>
public enum PortState
{
    Free,
    Detected,
    Scanning,
    Copying,
    Done,
    Failed,
}

/// <summary>
/// Одно гнездо на экране «Загрузка».
///
/// Раскладка повторяет штатную программу станции: номер отсека в углу, под ним
/// сведения о камере и сотруднике, внизу полоса хода выгрузки со счётчиком
/// файлов. Свободное гнездо подписано «Свободен», как там же.
/// </summary>
public sealed partial class PortViewModel : ObservableObject
{
    /// <summary>Высота плитки при сетке 4x3 на экране станции.</summary>
    public const double TileHeight = 148;

    /// <summary>Ширина полосы хода выгрузки.</summary>
    public const double BarWidth = 218;

    [ObservableProperty] private int _slot;
    [ObservableProperty] private string _cameraId = "";
    [ObservableProperty] private string _personnelNo = "";
    [ObservableProperty] private string _employee = "";
    [ObservableProperty] private string _department = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private PortState _state = PortState.Free;

    /// <summary>Доля скопированного, 0..1.</summary>
    [ObservableProperty] private double _progress;

    public string SlotLabel => $"{Slot + 1:00}";

    public bool IsFree => State == PortState.Free;
    public bool IsBusy => State != PortState.Free;

    public string StateText => State switch
    {
        PortState.Detected => "Подключена",
        PortState.Scanning => "Подсчёт объёма",
        PortState.Copying => "Загрузка данных",
        PortState.Done => "Загрузка завершена",
        PortState.Failed => "Ошибка загрузки",
        _ => "Свободен",
    };

    /// <summary>Подпись под свободным гнездом, как в штатной программе.</summary>
    public string IdleText => "Нет передачи данных";

    public string CameraLine => string.IsNullOrEmpty(CameraId) ? "" : $"ID устройства: {CameraId}";

    /// <summary>
    /// Номер сотрудника, прописанный в самой камере. Штатная станция
    /// показывает его на плитке рядом с номером устройства.
    /// </summary>
    public string PersonnelLine => string.IsNullOrEmpty(PersonnelNo)
        ? "ID пользователя: не задан"
        : $"ID пользователя: {PersonnelNo}";

    public string EmployeeLine => string.IsNullOrEmpty(Employee)
        ? "Сотрудник: не закреплён"
        : $"Сотрудник: {Employee}";

    public string DepartmentLine => string.IsNullOrEmpty(Department)
        ? "Отдел: не указан"
        : $"Отдел: {Department}";

    public string PercentText => State switch
    {
        PortState.Done => "100%",
        PortState.Copying or PortState.Scanning => $"{Math.Clamp(Progress, 0, 1) * 100:0}%",
        _ => "",
    };

    public IBrush Accent => new SolidColorBrush(Color.Parse(State switch
    {
        PortState.Detected or PortState.Scanning => "#2F6BFF",
        PortState.Copying => "#FFB020",
        PortState.Done => "#22C55E",
        PortState.Failed => "#FF3B5C",
        _ => "#1F2A44",
    }));

    /// <summary>Заполненная часть полосы, в пикселях.</summary>
    public double BarFill => State switch
    {
        PortState.Done => BarWidth,
        PortState.Copying or PortState.Scanning => BarWidth * Math.Clamp(Progress, 0, 1),
        _ => 0,
    };

    partial void OnStateChanged(PortState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Accent));
        OnPropertyChanged(nameof(BarFill));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(BarFill));
        OnPropertyChanged(nameof(PercentText));
    }

    partial void OnCameraIdChanged(string value) => OnPropertyChanged(nameof(CameraLine));
    partial void OnPersonnelNoChanged(string value) => OnPropertyChanged(nameof(PersonnelLine));
    partial void OnEmployeeChanged(string value) => OnPropertyChanged(nameof(EmployeeLine));
    partial void OnDepartmentChanged(string value) => OnPropertyChanged(nameof(DepartmentLine));
    partial void OnSlotChanged(int value) => OnPropertyChanged(nameof(SlotLabel));

    /// <summary>Возвращает плитку в состояние свободного гнезда.</summary>
    public void Clear()
    {
        CameraId = "";
        PersonnelNo = "";
        Employee = "";
        Department = "";
        Detail = "";
        Progress = 0;
        State = PortState.Free;
    }
}
