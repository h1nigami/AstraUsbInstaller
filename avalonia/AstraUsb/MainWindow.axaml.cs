using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace AstraUsb;

/// <summary>Плитка порта. Заполненность показывает долю скопированного.</summary>
public sealed class PortTile
{
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string Detail { get; init; } = "";
    public IBrush Fill { get; init; } = Brushes.Transparent;

    /// <summary>Высота заливки в пикселях при высоте плитки 132.</summary>
    public double FillHeight { get; init; }
}

public partial class MainWindow : Window
{
    private const int PortCount = 10;
    private const double TileHeight = 132;

    private static readonly IBrush IdleFill = new SolidColorBrush(Color.Parse("#1E293B"));
    private static readonly IBrush ScanFill = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush CopyFill = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush DoneFill = new SolidColorBrush(Color.Parse("#16A34A"));

    private readonly ObservableCollection<PortTile> _ports = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // На киоске окно занимает весь экран. На машине разработчика экран
        // крупнее целевого, поэтому ASTRA_WINDOWED открывает окно ровно в
        // размер станции — иначе вид на 1024x600 не оценить.
        if (Environment.GetEnvironmentVariable("ASTRA_WINDOWED") == "1")
        {
            WindowState = WindowState.Normal;
            Width = 1024;
            Height = 600;
        }

        var host = this.FindControl<ItemsControl>("PortsHost");
        if (host is not null)
            host.ItemsSource = _ports;

        if (Environment.GetEnvironmentVariable("ASTRA_DEMO") == "1")
        {
            ShowSampleState();
            SetStatus("Демонстрация состояний");
            return;
        }

        Refresh();
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
    }

    /// <summary>Опрашивает носители и перестраивает плитки.</summary>
    private void Refresh()
    {
        var devices = UsbWatcher.List();

        _ports.Clear();
        foreach (var device in devices.Take(PortCount))
        {
            var mounted = !string.IsNullOrEmpty(device.MountPoint);
            _ports.Add(new PortTile
            {
                Title = device.Name,
                State = mounted ? "Подключено" : "Не смонтировано",
                Detail = device.MountPoint ?? "",
                Fill = mounted ? ScanFill : IdleFill,
                FillHeight = mounted ? TileHeight * 0.08 : 0,
            });
        }

        for (var i = _ports.Count; i < PortCount; i++)
            _ports.Add(FreeTile());

        SetStatus(devices.Count == 0
            ? "Носители не подключены"
            : $"Носителей: {devices.Count}");
    }

    private static PortTile FreeTile() => new()
    {
        Title = "—",
        State = "Свободно",
        Detail = "",
        Fill = IdleFill,
        FillHeight = 0,
    };

    private void SetStatus(string text)
    {
        var label = this.FindControl<TextBlock>("StatusText");
        if (label is not null)
            label.Text = text;
    }

    /// <summary>Показывает состояния копирования, пока движок не подключён.</summary>
    private void ShowSampleState()
    {
        _ports.Add(new PortTile { Title = "3", State = "Копирование", Detail = "412 из 980 файлов", Fill = CopyFill, FillHeight = TileHeight * 0.42 });
        _ports.Add(new PortTile { Title = "7", State = "Сканирование", Detail = "считаем объём", Fill = ScanFill, FillHeight = TileHeight * 0.08 });
        _ports.Add(new PortTile { Title = "1", State = "Готово", Detail = "1,2 ГБ за 3 мин", Fill = DoneFill, FillHeight = TileHeight });
        for (var i = _ports.Count; i < PortCount; i++)
            _ports.Add(FreeTile());
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        // Выход под паролем появится вместе с настройками; пока прототип.
        _poll.Stop();
        Close();
    }
}
