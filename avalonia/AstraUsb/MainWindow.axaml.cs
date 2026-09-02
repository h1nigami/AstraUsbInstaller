using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

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
    private readonly ObservableCollection<PortTile> _ports = new();

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

        ShowSampleState();
    }

    /// <summary>Прототип: показывает, как выглядят состояния, пока движок не подключён.</summary>
    private void ShowSampleState()
    {
        var idle = new SolidColorBrush(Color.Parse("#1E293B"));
        var scan = new SolidColorBrush(Color.Parse("#2563EB"));
        var copy = new SolidColorBrush(Color.Parse("#D97706"));
        var done = new SolidColorBrush(Color.Parse("#16A34A"));

        _ports.Add(new PortTile { Title = "3", State = "Копирование", Detail = "412 из 980 файлов", Fill = copy, FillHeight = 132 * 0.42 });
        _ports.Add(new PortTile { Title = "7", State = "Сканирование", Detail = "считаем объём", Fill = scan, FillHeight = 132 * 0.15 });
        _ports.Add(new PortTile { Title = "1", State = "Готово", Detail = "1,2 ГБ за 3 мин", Fill = done, FillHeight = 132 });
        _ports.Add(new PortTile { Title = "—", State = "Свободно", Detail = "", Fill = idle, FillHeight = 0 });

        for (var i = 0; i < 6; i++)
            _ports.Add(new PortTile { Title = "—", State = "Свободно", Detail = "", Fill = idle, FillHeight = 0 });
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        // Выход под паролем появится вместе с настройками; пока прототип.
        Close();
    }
}
