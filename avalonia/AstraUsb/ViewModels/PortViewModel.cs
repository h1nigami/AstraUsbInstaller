using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AstraUsb.ViewModels;

/// <summary>Состояние отсека, как его видит оператор.</summary>
public enum PortState
{
    Idle,
    Detected,
    Scanning,
    Copying,
    Done,
    Failed,

    /// <summary>Оператор отменил загрузку: регистратор только заряжается.</summary>
    ChargeOnly,
}

/// <summary>
/// Одно окно на экране сбора данных.
///
/// Состояние кодируется тремя способами сразу: фоном, подписью и полосой. Одним
/// цветом кодировать нельзя, потому что экран станции смотрят при боковом
/// освещении и с угла, и разница светлого с тёмным читается надёжнее оттенка.
/// Поэтому ошибка получает не красный цвет, которого в палитре нет вовсе, а
/// тёмный инверсный фон.
/// </summary>
public sealed partial class PortViewModel : ObservableObject
{
    /// <summary>Ширина полосы хода выгрузки в окне.</summary>
    public const double BarWidth = 168;

    [ObservableProperty] private int _slot;
    [ObservableProperty] private string _cameraId = "";
    [ObservableProperty] private string _personnelNo = "";
    [ObservableProperty] private string _employee = "";
    [ObservableProperty] private string _department = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _filesLine = "";
    [ObservableProperty] private PortState _state = PortState.Idle;

    /// <summary>Доля скопированного, 0..1.</summary>
    [ObservableProperty] private double _progress;

    public string SlotLabel => $"{Slot + 1}";

    public bool IsFree => State == PortState.Idle && string.IsNullOrEmpty(CameraId);
    public bool IsBusy => !IsFree;

    /// <summary>Состояние словом, как в прототипе станции.</summary>
    public string StateText => State switch
    {
        PortState.Detected or PortState.Scanning => "Сканирование",
        PortState.Copying => "Копирование",
        PortState.Done => "Готово",
        PortState.Failed => "Ошибка",
        PortState.ChargeOnly => "Только зарядка",
        _ => "Простой",
    };

    /// <summary>Подпись под состоянием: что оператору делать или не делать.</summary>
    public string StateHint => State switch
    {
        PortState.Detected or PortState.Scanning => "Чтение списка файлов",
        PortState.Copying => "Не извлекайте регистратор",
        PortState.Done => "Можно забирать регистратор",
        PortState.Failed => "Часть файлов не скопирована",
        PortState.ChargeOnly => "Загрузка отменена оператором",
        _ => IsFree ? "Вставьте регистратор" : "Нет передачи данных",
    };

    public string CameraLine => string.IsNullOrEmpty(CameraId) ? "Отсек свободен" : CameraId;

    public string PersonnelLine => string.IsNullOrEmpty(PersonnelNo) ? "" : $"№ {PersonnelNo}";

    public string EmployeeLine => Employee;

    public string DepartmentLine => Department;

    public string PercentText => State switch
    {
        PortState.Done => "100%",
        PortState.Copying or PortState.Scanning => $"{Math.Clamp(Progress, 0, 1) * 100:0}%",
        _ => "",
    };

    /// <summary>Фон окна.</summary>
    public IBrush Fill => Brush(State switch
    {
        PortState.Detected or PortState.Scanning => "#E6F6F8",
        PortState.Copying => "#EAF3FC",
        PortState.Done => "#C8EAEE",
        PortState.Failed => "#143A61",
        PortState.ChargeOnly => "#E9F0F7",
        _ => "#F7FAFD",
    });

    /// <summary>Обводка окна: она же несёт состояние на схеме отсеков.</summary>
    public IBrush Edge => Brush(State switch
    {
        PortState.Detected or PortState.Scanning => "#9DD7DE",
        PortState.Copying => "#72A9DE",
        PortState.Done => "#3F9BA6",
        PortState.Failed => "#143A61",
        PortState.ChargeOnly => "#A7CAEE",
        _ => "#D3DEE9",
    });

    /// <summary>Номер отсека и заполненная часть полосы.</summary>
    public IBrush Mark => Brush(State switch
    {
        PortState.Detected or PortState.Scanning => "#3F9BA6",
        PortState.Copying => "#3F84C5",
        PortState.Done => "#2C7D88",
        PortState.Failed or PortState.ChargeOnly => "#A7CAEE",
        _ => "#D3DEE9",
    });

    /// <summary>Цвет текста: на тёмном фоне ошибки он светлый.</summary>
    public IBrush Ink => Brush(State switch
    {
        PortState.Detected or PortState.Scanning or PortState.Done => "#0F2F34",
        PortState.Copying => "#0D2740",
        PortState.Failed => "#F7FAFD",
        _ => "#16202C",
    });

    /// <summary>Приглушённый текст того же окна.</summary>
    public IBrush InkMuted => Brush(State switch
    {
        PortState.Failed => "#A7CAEE",
        PortState.Detected or PortState.Scanning or PortState.Done => "#16454C",
        PortState.Copying => "#1D4F83",
        _ => "#56677E",
    });

    /// <summary>Незаполненная часть полосы.</summary>
    public IBrush Track => Brush(State switch
    {
        PortState.Detected or PortState.Scanning => "#C8EAEE",
        PortState.Copying => "#D0E4F8",
        PortState.Done => "#9DD7DE",
        PortState.Failed => "#1D4F83",
        _ => "#E9F0F7",
    });

    /// <summary>Номер отсека читается на любом фоне окна.</summary>
    public IBrush SlotInk => Brush(State switch
    {
        PortState.Failed or PortState.ChargeOnly => "#0D2740",
        PortState.Detected or PortState.Scanning or PortState.Done => "#F7FAFD",
        PortState.Copying => "#F7FAFD",
        _ => "#3A495C",
    });

    /// <summary>Заполненная часть полосы, в точках.</summary>
    public double BarFill => State switch
    {
        PortState.Done or PortState.Failed => BarWidth,
        PortState.Copying or PortState.Scanning => BarWidth * Math.Clamp(Progress, 0, 1),
        _ => 0,
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    partial void OnStateChanged(PortState value)
    {
        foreach (var name in new[]
                 {
                     nameof(StateText), nameof(StateHint), nameof(PercentText), nameof(BarFill),
                     nameof(Fill), nameof(Edge), nameof(Mark), nameof(Ink), nameof(InkMuted),
                     nameof(Track), nameof(SlotInk), nameof(IsFree), nameof(IsBusy),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(BarFill));
        OnPropertyChanged(nameof(PercentText));
    }

    partial void OnCameraIdChanged(string value)
    {
        OnPropertyChanged(nameof(CameraLine));
        OnPropertyChanged(nameof(StateHint));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(IsBusy));
    }

    partial void OnPersonnelNoChanged(string value) => OnPropertyChanged(nameof(PersonnelLine));
    partial void OnEmployeeChanged(string value) => OnPropertyChanged(nameof(EmployeeLine));
    partial void OnDepartmentChanged(string value) => OnPropertyChanged(nameof(DepartmentLine));
    partial void OnSlotChanged(int value) => OnPropertyChanged(nameof(SlotLabel));

    /// <summary>Возвращает окно в состояние свободного отсека.</summary>
    public void Clear()
    {
        CameraId = "";
        PersonnelNo = "";
        Employee = "";
        Department = "";
        Detail = "";
        FilesLine = "";
        Progress = 0;
        State = PortState.Idle;
    }
}
