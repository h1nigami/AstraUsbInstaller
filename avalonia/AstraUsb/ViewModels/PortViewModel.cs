using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AstraUsb.ViewModels;

/// <summary>Состояние порта, как его видит оператор.</summary>
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
/// Одно гнездо на экране «Загрузка». Плитка одновременно служит индикатором:
/// заливка снизу вверх показывает долю скопированного и читается с расстояния,
/// в отличие от процентов мелким шрифтом.
/// </summary>
public sealed partial class PortViewModel : ObservableObject
{
    /// <summary>Высота плитки при сетке 5x2 на экране станции.</summary>
    public const double TileHeight = 150;

    [ObservableProperty]
    private string _title = "—";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private PortState _state = PortState.Free;

    /// <summary>Доля скопированного, 0..1.</summary>
    [ObservableProperty]
    private double _progress;

    public string StateText => State switch
    {
        PortState.Detected => "ПОДКЛЮЧЕНО",
        PortState.Scanning => "СКАНИРОВАНИЕ",
        PortState.Copying => "КОПИРОВАНИЕ",
        PortState.Done => "ГОТОВО",
        PortState.Failed => "ОШИБКА",
        _ => "СВОБОДНО",
    };

    public IBrush Fill => new SolidColorBrush(Color.Parse(State switch
    {
        PortState.Detected or PortState.Scanning => "#2F6BFF",
        PortState.Copying => "#FFB020",
        PortState.Done => "#22C55E",
        PortState.Failed => "#FF3B5C",
        _ => "#0F1524",
    }));

    /// <summary>Полоса сверху и рамка: занятый порт заметен и без заливки.</summary>
    public IBrush Edge => new SolidColorBrush(Color.Parse(State switch
    {
        PortState.Detected or PortState.Scanning => "#22D3EE",
        PortState.Copying => "#FFB020",
        PortState.Done => "#22C55E",
        PortState.Failed => "#FF3B5C",
        _ => "#1F2A44",
    }));

    public double FillHeight => State switch
    {
        PortState.Free => 0,
        PortState.Detected => TileHeight * 0.06,
        PortState.Done => TileHeight,
        _ => TileHeight * Math.Clamp(Progress, 0.06, 1),
    };

    partial void OnStateChanged(PortState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Edge));
        OnPropertyChanged(nameof(FillHeight));
    }

    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(FillHeight));

    /// <summary>Возвращает плитку в состояние свободного гнезда.</summary>
    public void Clear()
    {
        Title = "—";
        Detail = "";
        Progress = 0;
        State = PortState.Free;
    }
}
