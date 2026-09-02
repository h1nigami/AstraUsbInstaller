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
    private readonly PortMap _portMap;
    private readonly Settings _stationSettings = Services.Settings.Load();
    private readonly BackupService _backups;

    /// <summary>Камеры, для которых выгрузка уже идёт: повторно не запускаем.</summary>
    private readonly Dictionary<string, long> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Камеры, которые уже выгружены в этом подключении. Опрос идёт каждые две
    /// секунды, и без этой памяти выгрузка запускалась бы по кругу. Память
    /// сбрасывается, когда камеру вынимают.
    /// </summary>
    private readonly Dictionary<string, BackupStage> _finished = new(StringComparer.Ordinal);

    /// <summary>
    /// Опознанные камеры. Разбор карты стоит дорого: читается файл номера и
    /// обходятся записи, а опрос идёт каждые две секунды. Поэтому результат
    /// держим до извлечения носителя.
    /// </summary>
    private readonly Dictionary<string, CardInfo> _identified = new(StringComparer.Ordinal);

    public ObservableCollection<PortViewModel> Ports { get; } = new();

    /// <summary>Вкладка «Устройства».</summary>
    public DevicesViewModel Devices { get; } = new();

    /// <summary>Вкладка «Настройки».</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Вкладка «Поиск».</summary>
    public SearchViewModel Search { get; } = new();

    [ObservableProperty]
    private string _clockTime = "--:--:--";

    [ObservableProperty]
    private string _clockDate = "";

    [ObservableProperty]
    private string _version = "версия неизвестна";

    [ObservableProperty]
    private string _status = "мониторинг: запуск";

    [ObservableProperty]
    private string _storageLabel = "хранилище недоступно";

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
    public MainWindowViewModel(Func<IReadOnlyList<UsbDevice>> listDevices, PortMap? portMap = null)
    {
        _listDevices = listDevices;
        AppPaths.EnsureCreated();
        _portMap = portMap ?? new PortMap(AppPaths.Database);
        _backups = new BackupService(AppPaths.Database, _stationSettings);
        Version = ReadVersion();

        for (var i = 0; i < PortCount; i++)
            Ports.Add(new PortViewModel { Slot = i });

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

        // Носители раскладываются по закреплённым гнёздам: камера из второго
        // разъёма занимает второе окно независимо от очерёдности подключения.
        var placed = _portMap.Arrange(devices, Ports.Count);

        for (var i = 0; i < Ports.Count; i++)
        {
            if (placed[i] is not { } device)
            {
                Ports[i].Clear();
                continue;
            }

            var port = Ports[i];
            var cameraId = device.Name;
            var detail = "опознаём камеру";
            var personnel = "";
            var employee = "";
            var department = "";

            if (Identify(device) is { } card)
            {
                cameraId = card.CameraId;
                detail = card.Origin;
                personnel = card.PersonnelNo;
                employee = card.Employee;
                department = card.Department;
                StartBackup(port, card.DeviceId, device.MountPoint!);
            }

            port.CameraId = cameraId;
            port.PersonnelNo = personnel;
            port.Employee = employee;
            port.Department = department;

            // Пока выгрузка идёт или уже закончена, подпись и состояние
            // принадлежат ей: иначе опрос каждые две секунды сбрасывал бы
            // «загрузку данных» обратно в «подключена».
            var busy = !string.IsNullOrEmpty(device.MountPoint)
                       && (_running.ContainsKey(device.MountPoint!)
                           || _finished.ContainsKey(device.MountPoint!));
            if (!busy)
            {
                port.Detail = detail;
                port.State = string.IsNullOrEmpty(device.MountPoint)
                    ? PortState.Free
                    : PortState.Detected;
            }
        }

        // Камеру вынули, забываем итог, чтобы при следующем подключении
        // выгрузка началась заново.
        var present = devices
            .Select(d => d.MountPoint)
            .Where(m => !string.IsNullOrEmpty(m))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var gone in _finished.Keys.Where(m => !present.Contains(m)).ToArray())
            _finished.Remove(gone);

        foreach (var gone in _identified.Keys.Where(m => !present.Contains(m)).ToArray())
            _identified.Remove(gone);

        Status = devices.Count == 0
            ? "носители не подключены"
            : $"носителей: {devices.Count}";

        UpdateStorage();
    }

    /// <summary>
    /// Опознаёт камеру и подтягивает сотрудника. Результат кэшируется до
    /// извлечения носителя.
    /// </summary>
    private CardInfo? Identify(UsbDevice device)
    {
        if (string.IsNullOrEmpty(device.MountPoint))
            return null;

        var mount = device.MountPoint;
        if (_identified.TryGetValue(mount, out var cached))
            return cached;

        try
        {
            // Файл на карте и есть источник истины. Нет файла, станция выдаёт
            // номер и обязательно записывает его: иначе при следующем
            // подключении камера будет опознана как новая. Имена записей
            // идут запасным признаком: по ним узнаётся аппарат, которому
            // поставили другую карту, и по ним же видно, у кого он на руках.
            var recording = RecordingName.FromCard(mount);
            var personnel = recording?.HasPersonnelNo == true ? recording.PersonnelNo : "";

            using var registry = new DeviceRegistry(AppPaths.Database);
            var id = registry.ResolveByCard(mount, _stationSettings.StationNumber,
                device.Name, device.Name, recording);

            var number = registry.FirmwareIdOf(id) ?? "";
            var name = registry.GetDeviceName(id);

            var staff = new StaffDirectory(AppPaths.Database);
            var person = staff.AssignByPersonnelNo(id, personnel);

            var info = new CardInfo(
                id,
                string.IsNullOrEmpty(name) ? number : name,
                Origin(number),
                personnel,
                person?.FullName ?? "",
                staff.DepartmentPath(person?.DepartmentId));

            _identified[mount] = info;
            return info;
        }
        catch (Exception)
        {
            // База занята другим действием, повторим на следующем опросе.
            return null;
        }
    }

    /// <summary>Откуда у камеры номер: от этой станции, от чужой или из самой камеры.</summary>
    private string Origin(string number) =>
        CardIdentity.StationOf(number) is { } station
            ? station == _stationSettings.StationNumber
                ? "номер выдан этой станцией"
                : $"номер станции {station:00}"
            : "номер задан в камере";

    /// <summary>
    /// Запускает выгрузку камеры, если она ещё не идёт. Плитка показывает ход:
    /// заливка растёт по мере копирования.
    /// </summary>
    private void StartBackup(PortViewModel port, long deviceId, string mountPoint)
    {
        if (_running.ContainsKey(mountPoint) || _finished.ContainsKey(mountPoint))
            return;

        _running[mountPoint] = deviceId;
        port.State = PortState.Scanning;

        var progress = new Progress<BackupProgress>(report =>
        {
            port.Progress = report.Progress;
            port.Detail = report.Detail;
            port.State = report.Stage switch
            {
                BackupStage.Scanning => PortState.Scanning,
                BackupStage.Copying => PortState.Copying,
                BackupStage.Done => PortState.Done,
                _ => PortState.Failed,
            };

            if (report.Stage is BackupStage.Done or BackupStage.Failed)
                _finished[mountPoint] = report.Stage;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _backups.RunAsync(deviceId, mountPoint, progress);
            }
            finally
            {
                _running.Remove(mountPoint);
            }
        });
    }

    /// <summary>Версия из файла VERSION рядом с программой.</summary>
    private static string ReadVersion()
    {
        try
        {
            var parts = File.ReadAllText(AppPaths.VersionFile).Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && DateTime.TryParse(parts[1], out var date))
                return $"версия {parts[0].TrimStart('v')} от {date:dd.MM.yy}";
        }
        catch (Exception)
        {
            // Файла нет или он испорчен, приложение из-за версии падать не должно.
        }
        return "версия неизвестна";
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
            // Диск недоступен, полоса остаётся в прежнем состоянии.
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

/// <summary>Что станция знает о подключённой камере.</summary>
/// <param name="Origin">Откуда у неё номер, для строки под плиткой.</param>
/// <param name="PersonnelNo">Номер сотрудника, прописанный в самой камере.</param>
internal sealed record CardInfo(
    long DeviceId,
    string CameraId,
    string Origin,
    string PersonnelNo,
    string Employee,
    string Department);
