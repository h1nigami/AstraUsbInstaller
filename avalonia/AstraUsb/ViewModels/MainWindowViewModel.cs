using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Главный экран станции: гнёзда, часы, состояние хранилища.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Столько гнёзд у станции; в старой программе их 6–30, у нас десять.</summary>
    public const int PortCount = 10;

    private static readonly CultureInfo Ru = new("ru-RU");

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<IReadOnlyList<UsbDevice>> _listDevices;

    public ObservableCollection<PortViewModel> Ports { get; } = new();

    [ObservableProperty]
    private string _clockTime = "--:--:--";

    [ObservableProperty]
    private string _clockDate = "";

    [ObservableProperty]
    private string _version = "версия —";

    [ObservableProperty]
    private string _status = "мониторинг: запуск";

    [ObservableProperty]
    private string _storageLabel = "хранилище —";

    /// <summary>Ширина заполненной части полосы хранилища, в пикселях.</summary>
    [ObservableProperty]
    private double _storageWidth;

    [ObservableProperty]
    private IBrush _storageBrush = new SolidColorBrush(Color.Parse("#22D3EE"));

    public event Action? ExitRequested;

    public MainWindowViewModel() : this(UsbWatcher.List)
    {
    }

    /// <summary>Опрос носителей передаётся снаружи: так экран можно проверить без железа.</summary>
    public MainWindowViewModel(Func<IReadOnlyList<UsbDevice>> listDevices)
    {
        _listDevices = listDevices;

        for (var i = 0; i < PortCount; i++)
            Ports.Add(new PortViewModel());

        TickClock();
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();

        Refresh();
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
    }

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    /// <summary>Часы станции: по ним сверяется время подключённых регистраторов.</summary>
    private void TickClock()
    {
        var now = DateTime.Now;
        ClockTime = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ClockDate = now.ToString("dd MMMM yyyy", Ru);
    }

    /// <summary>Опрашивает носители и раскладывает их по гнёздам.</summary>
    public void Refresh()
    {
        var devices = _listDevices();

        for (var i = 0; i < Ports.Count; i++)
        {
            if (i >= devices.Count)
            {
                Ports[i].Clear();
                continue;
            }

            var device = devices[i];
            var identity = DeviceIdentifier.Resolve(device.MountPoint);
            var port = Ports[i];

            port.Title = identity.IsKnown ? identity.Value : device.Name;
            port.Detail = identity.Kind switch
            {
                IdentityKind.FirmwareId => "номер камеры",
                IdentityKind.CardMarker => "номер с карты",
                _ => device.MountPoint ?? "не смонтировано",
            };
            port.State = string.IsNullOrEmpty(device.MountPoint)
                ? PortState.Free
                : PortState.Detected;
        }

        Status = devices.Count == 0
            ? "носители не подключены"
            : $"носителей: {devices.Count}";

        UpdateStorage();
    }

    /// <summary>Показывает заполнение хранилища и краснеет, когда места мало.</summary>
    private void UpdateStorage()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return;

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            var ratio = (double)used / drive.TotalSize;

            StorageLabel = $"хранилище {Size(used)} из {Size(drive.TotalSize)}";
            StorageWidth = Math.Clamp(ratio, 0, 1) * 150;
            StorageBrush = new SolidColorBrush(Color.Parse(ratio switch
            {
                >= 0.9 => "#FF3B5C",
                >= 0.75 => "#FFB020",
                _ => "#22D3EE",
            }));
        }
        catch (Exception)
        {
            // Диск недоступен — полоса остаётся в прежнем состоянии.
        }
    }

    private static string Size(long bytes)
    {
        var tb = bytes / 1024d / 1024 / 1024 / 1024;
        if (tb >= 1)
            return $"{tb.ToString("0.0", Ru)} ТБ";
        return $"{(bytes / 1024d / 1024 / 1024).ToString("0", Ru)} ГБ";
    }

    public void Dispose()
    {
        _poll.Stop();
        _clock.Stop();
    }
}
