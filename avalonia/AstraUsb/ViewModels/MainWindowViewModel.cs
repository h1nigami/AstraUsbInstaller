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
    /// <summary>Сколько окон сбора у станции по умолчанию, если в настройках не задано.</summary>
    public const int DefaultPortCount = 10;

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

    /// <summary>
    /// Карты, которые станция смонтировала сама. Рабочему столу это запрещено
    /// правилом udev, иначе один FAT писался бы с двух сторон.
    /// </summary>
    private readonly Dictionary<string, Mounted> _mounted = new(StringComparer.Ordinal);

    /// <summary>Носители, которые монтируются прямо сейчас.</summary>
    private readonly HashSet<string> _mounting = new(StringComparer.Ordinal);

    /// <summary>Чем прервать идущую выгрузку, если оператор попросил.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _cancels =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Носители, с которых оператор отменил загрузку. Регистратор остаётся в
    /// отсеке и заряжается, а файлы на нём сохраняются до следующего раза.
    /// </summary>
    private readonly HashSet<string> _chargeOnly = new(StringComparer.Ordinal);

    /// <summary>
    /// Отсек, который обслуживается вне очереди. Пока он не закончит, другие
    /// выгрузки не начинаются: полоса USB и диск делятся между всеми, и
    /// «первым» имеет смысл только при остановленных остальных.
    /// </summary>
    private string? _priority;

    /// <summary>
    /// Последний раз, когда носитель был виден, и каким он был. Опрос иногда
    /// не отдаёт устройство, которое никто не вынимал, поэтому извлечение
    /// подтверждается выдержкой, как требует задание.
    /// </summary>
    private readonly Dictionary<string, (UsbDevice Device, DateTime Seen)> _recent =
        new(StringComparer.Ordinal);

    /// <summary>Открыт ли доступ к закрытым разделам и до каких пор.</summary>
    private AccessGuard _access = new(0);

    /// <summary>Раздел, куда оператор шёл, когда его остановил пароль.</summary>
    private int _wantedTab;

    /// <summary>Пароль спрашивают перед выходом, а не перед разделом.</summary>
    private bool _askingForExit;

    /// <summary>Названия разделов для журнала: индекс совпадает с вкладкой.</summary>
    private static readonly string[] TabNames =
        ["Загрузка", "Поиск", "Устройства", "Сотрудники", "Журнал", "Настройки"];

    private readonly ActionLog _actions = new(AppPaths.Database);

    public ObservableCollection<PortViewModel> Ports { get; } = new();

    /// <summary>Вкладка «Устройства».</summary>
    public DevicesViewModel Devices { get; } = new();

    /// <summary>Вкладка «Настройки».</summary>
    public SettingsViewModel Settings { get; } = new();

    /// <summary>Вкладка «Поиск».</summary>
    public SearchViewModel Search { get; } = new();

    /// <summary>Вкладка «Сотрудники».</summary>
    public StaffViewModel Staff { get; } = new();

    /// <summary>Вкладка «Журнал».</summary>
    public LogViewModel Log { get; } = new();

    [ObservableProperty]
    private string _clockTime = "--:--:--";

    [ObservableProperty]
    private string _clockDate = "";

    [ObservableProperty]
    private string _version = "версия неизвестна";

    [ObservableProperty]
    private string _status = "мониторинг: запуск";

    /// <summary>
    /// Выбранная компоновка доски: сетка, список или схема стойки. Все три
    /// живут на одной кодовой базе и переключаются оператором.
    /// </summary>
    [ObservableProperty]
    private string _layout = "grid";

    public bool IsGridLayout => Layout == "grid";
    public bool IsListLayout => Layout == "list";
    public bool IsRackLayout => Layout == "rack";

    [ObservableProperty]
    private string _storageLabel = "хранилище недоступно";

    /// <summary>Ширина заполненной части полосы хранилища, в пикселях.</summary>
    [ObservableProperty]
    private double _storageWidth;

    [ObservableProperty]
    private IBrush _storageBrush = new SolidColorBrush(Color.Parse("#3F9BA6"));

    /// <summary>Показан ли запрос пароля поверх экрана.</summary>
    [ObservableProperty]
    private bool _passwordVisible;

    [ObservableProperty]
    private string _accountInput = "";

    [ObservableProperty]
    private string _passwordInput = "";

    /// <summary>Введённый пароль точками: подглядеть через плечо нечего.</summary>
    public string PasswordMask => new('●', PasswordInput.Length);

    [ObservableProperty]
    private string _passwordPrompt = "";

    [ObservableProperty]
    private string _passwordError = "";

    /// <summary>Открыта ли карточка отсека.</summary>
    [ObservableProperty]
    private bool _bayVisible;

    /// <summary>Отсек, чья карточка открыта.</summary>
    [ObservableProperty]
    private PortViewModel? _bay;

    /// <summary>Номер устройства в карточке.</summary>
    [ObservableProperty]
    private string _bayDevice = "";

    /// <summary>Подтверждение действия внутри карточки.</summary>
    [ObservableProperty]
    private string _bayConfirm = "";

    public event Action? ExitRequested;

    /// <summary>Пароль принят: можно открывать этот раздел.</summary>
    public event Action<int>? AccessGranted;

    /// <summary>Доступ закрылся по простою: раздел нужно покинуть.</summary>
    public event Action? AccessExpired;

    /// <summary>Открыт ли сейчас доступ к закрытым разделам.</summary>
    public bool AccessAllowed => _access.Check(DateTime.Now);

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

        // Число окон задаётся в настройках: у станций от шести до тридцати
        // отсеков, и окна должны совпадать с железом.
        var bays = Math.Clamp(_stationSettings.BayCount, 6, 30);
        for (var i = 0; i < bays; i++)
            Ports.Add(new PortViewModel { Slot = i });

        // Журнал за годы работы разрастается вместе с базой, поэтому при
        // запуске он подрезается до последних событий.
        _actions.Trim(20_000);

        // Архив по умолчанию лежит рядом с программой и всегда на месте,
        // поэтому метку ему станция ставит сама. Том, выбранный оператором,
        // помечается при выборе: там метка и нужна, чтобы отличить
        // несмонтированный диск от пустого.
        if (_stationSettings.BackupRoot == AppPaths.BackupsRoot)
            ArchiveGuard.Mark(_stationSettings.BackupRoot);

        TickClock();
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();

        Refresh();
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
    }

    /// <summary>
    /// Выход закрыт паролем. В киоске это единственный способ покинуть
    /// программу, поэтому спрашиваем пароль, а не закрываемся сразу.
    /// </summary>
    [RelayCommand]
    private void SetLayout(string? name) => Layout = name switch
    {
        "list" => "list",
        "rack" => "rack",
        _ => "grid",
    };

    partial void OnLayoutChanged(string value)
    {
        OnPropertyChanged(nameof(IsGridLayout));
        OnPropertyChanged(nameof(IsListLayout));
        OnPropertyChanged(nameof(IsRackLayout));
    }

    [RelayCommand]
    private void Exit() => Ask("Выход из программы", exit: true);

    /// <summary>Оператор пытается открыть закрытый раздел.</summary>
    public void AskForTab(int tabIndex)
    {
        _wantedTab = tabIndex;
        Ask("Доступ к разделу", exit: false);
    }

    private void Ask(string prompt, bool exit)
    {
        var settings = Services.Settings.Load();

        _askingForExit = exit;
        PasswordPrompt = prompt;
        AccountInput = string.IsNullOrWhiteSpace(settings.AdminAccount)
            ? PasswordGate.DefaultAccount
            : settings.AdminAccount;
        PasswordInput = "";
        PasswordError = "";
        PasswordVisible = true;
    }

    /// <summary>
    /// Клавиша экранной клавиатуры. Станция сенсорная, физической клавиатуры
    /// у неё нет, а пароль по заданию цифровой.
    /// </summary>
    [RelayCommand]
    private void PasswordKey(string? key)
    {
        PasswordError = "";

        PasswordInput = key switch
        {
            null or "" => PasswordInput,
            "C" => "",
            "<" => PasswordInput.Length > 0 ? PasswordInput[..^1] : "",
            _ => PasswordInput.Length < 32 ? PasswordInput + key : PasswordInput,
        };
    }

    partial void OnPasswordInputChanged(string value) => OnPropertyChanged(nameof(PasswordMask));

    [RelayCommand]
    private void ConfirmPassword()
    {
        // Настройки читаются заново: пароль могли сменить в этом же сеансе.
        var settings = Services.Settings.Load();

        if (!PasswordGate.AccountMatches(settings.AdminAccount, AccountInput)
            || !PasswordGate.Matches(settings.PasswordHash, PasswordInput))
        {
            PasswordInput = "";
            // Что именно не подошло, имя или пароль, не уточняем: это
            // подсказало бы подбирающему, какую половину он уже угадал.
            PasswordError = "учётная запись или пароль не подошли";
            _actions.Write(ActionLog.Access, _askingForExit
                ? $"отказ при выходе из программы, учётная запись «{AccountInput}»"
                : $"отказ при входе в раздел «{TabName(_wantedTab)}», "
                  + $"учётная запись «{AccountInput}»");
            return;
        }

        PasswordVisible = false;
        PasswordInput = "";

        if (_askingForExit)
        {
            _actions.Write(ActionLog.Exit, $"выход из программы, {AccountInput}");
            ExitRequested?.Invoke();
            return;
        }

        _access = new AccessGuard(settings.LockTimeoutMinutes);
        _access.Unlock(DateTime.Now);
        _actions.Write(ActionLog.Access,
            $"открыт раздел «{TabName(_wantedTab)}», {AccountInput}");
        AccessGranted?.Invoke(_wantedTab);
    }

    [RelayCommand]
    private void CancelPassword()
    {
        PasswordVisible = false;
        PasswordInput = "";
        PasswordError = "";
    }

    /// <summary>
    /// Открывает карточку отсека. Пароля она не требует: это работа сменного
    /// оператора, а не администратора.
    /// </summary>
    [RelayCommand]
    private void OpenBay(PortViewModel? port)
    {
        if (port is null || port.IsFree)
            return;

        Bay = port;
        BayDevice = port.CameraId;
        BayConfirm = "";
        BayVisible = true;
    }

    [RelayCommand]
    private void CloseBay()
    {
        BayVisible = false;
        Bay = null;
        BayConfirm = "";
    }

    /// <summary>
    /// Обслуживает этот отсек первым. Остальные выгрузки прерываются и
    /// продолжатся потом с недостающих файлов: копирование инкрементальное,
    /// поэтому прерывание ничего не теряет.
    /// </summary>
    [RelayCommand]
    private void PrioritizeBay()
    {
        if (Bay is not { } port || MountOf(port) is not { } mount)
            return;

        if (BayConfirm != "priority")
        {
            BayConfirm = "priority";
            return;
        }

        _priority = mount;
        _chargeOnly.Remove(mount);
        _finished.Remove(mount);

        foreach (var other in _cancels.Keys.Where(m => m != mount).ToArray())
            Cancel(other);

        _actions.Write(ActionLog.Backup, $"отсек {port.Slot + 1} обслуживается первым");
        CloseBay();
    }

    /// <summary>
    /// Отменяет загрузку: регистратор остаётся на зарядке, файлы на нём
    /// сохраняются и будут собраны при следующем подключении.
    /// </summary>
    [RelayCommand]
    private void ChargeOnlyBay()
    {
        if (Bay is not { } port || MountOf(port) is not { } mount)
            return;

        if (BayConfirm != "charge")
        {
            BayConfirm = "charge";
            return;
        }

        _chargeOnly.Add(mount);
        if (_priority == mount)
            _priority = null;

        Cancel(mount);
        port.Progress = 0;
        port.FilesLine = "";
        port.State = PortState.ChargeOnly;

        _actions.Write(ActionLog.Backup,
            $"отсек {port.Slot + 1}: загрузка отменена оператором, идёт только зарядка");
        CloseBay();
    }

    /// <summary>Возвращает отсек в обычную работу.</summary>
    [RelayCommand]
    private void ResumeBay()
    {
        if (Bay is not { } port || MountOf(port) is not { } mount)
            return;

        _chargeOnly.Remove(mount);
        _finished.Remove(mount);
        port.State = PortState.Detected;

        _actions.Write(ActionLog.Backup, $"отсек {port.Slot + 1}: загрузка возобновлена");
        CloseBay();
    }

    /// <summary>Точка монтирования, которой сейчас занят этот отсек.</summary>
    private string? MountOf(PortViewModel port) =>
        _identified.FirstOrDefault(pair => pair.Value.DeviceId != 0
                                           && pair.Value.CameraId == port.CameraId).Key;

    private void Cancel(string mount)
    {
        if (!_cancels.TryGetValue(mount, out var cts))
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Выгрузка уже завершилась сама: отменять нечего.
        }
    }

    /// <summary>Оператор работает: отсчёт простоя начинается заново.</summary>
    public void NoteActivity() => _access.Touch(DateTime.Now);

    private static string TabName(int index) =>
        index >= 0 && index < TabNames.Length ? TabNames[index] : $"раздел {index}";

    /// <summary>Часы станции: по ним сверяется время подключённых регистраторов.</summary>
    private void TickClock()
    {
        var now = DateTime.Now;
        ClockTime = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ClockDate = now.ToString("dd MMMM yyyy", Ru);

        // Раздел, забытый открытым, закрывается сам: станция стоит в общем
        // помещении.
        var wasUnlocked = _access.Unlocked;
        if (wasUnlocked && !_access.Check(now))
            AccessExpired?.Invoke();
    }

    /// <summary>Опрашивает носители и раскладывает их по гнёздам.</summary>
    public void Refresh()
    {
        var devices = HoldBriefly(_listDevices());

        // Диск, на котором лежит архив, источником не считается: иначе
        // станция принялась бы копировать архив сам в себя.
        devices = devices
            .Where(d => !ArchiveGuard.IsArchiveMedia(d.MountPoint, _stationSettings.BackupRoot))
            .ToList();

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
            var mount = MountPointFor(device);
            var cameraId = device.Name;
            var detail = mount is null ? "готовим носитель" : "опознаём камеру";
            var personnel = "";
            var employee = "";
            var department = "";

            if (mount is not null && Identify(device, mount) is { } card)
            {
                cameraId = card.CameraId;
                detail = card.Origin;
                personnel = card.PersonnelNo;
                employee = card.Employee;
                department = card.Department;
                StartBackup(port, card.DeviceId, mount);
            }

            port.CameraId = cameraId;
            port.PersonnelNo = personnel;
            port.Employee = employee;
            port.Department = department;

            // Пока выгрузка идёт или уже закончена, подпись и состояние
            // принадлежат ей: иначе опрос каждые две секунды сбрасывал бы
            // «загрузку данных» обратно в «подключена».
            var busy = mount is not null
                       && (_running.ContainsKey(mount) || _finished.ContainsKey(mount));
            if (!busy)
            {
                port.Detail = detail;
                port.FilesLine = "";
                port.State = PortState.Detected;
            }
        }

        // Камеру вынули, забываем итог, чтобы при следующем подключении
        // выгрузка началась заново.
        var present = devices
            .Select(d => MountPointFor(d))
            .Where(m => !string.IsNullOrEmpty(m))
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var gone in _finished.Keys.Where(m => !present.Contains(m)).ToArray())
            _finished.Remove(gone);

        foreach (var gone in _identified.Keys.Where(m => !present.Contains(m)).ToArray())
            _identified.Remove(gone);

        foreach (var gone in _chargeOnly.Where(m => !present.Contains(m)).ToArray())
            _chargeOnly.Remove(gone);

        if (_priority is { } waiting && !present.Contains(waiting))
            _priority = null;

        ReleaseGoneMedia(devices);

        Status = devices.Count == 0
            ? "носители не подключены"
            : $"носителей: {devices.Count}";

        UpdateStorage();
    }

    /// <summary>
    /// Держит недавно пропавшие носители в списке ещё полторы длительности
    /// опроса. Регистратор считается извлечённым только после этой выдержки:
    /// иначе один неудачный опрос сбрасывал бы ход выгрузки.
    /// </summary>
    private IReadOnlyList<UsbDevice> HoldBriefly(IReadOnlyList<UsbDevice> devices)
    {
        var now = DateTime.Now;
        var hold = _poll.Interval * 1.5;

        foreach (var device in devices)
            _recent[device.Name] = (device, now);

        var present = devices.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var result = devices.ToList();

        foreach (var (name, entry) in _recent.ToArray())
        {
            if (present.Contains(name))
                continue;

            if (now - entry.Seen <= hold)
                result.Add(entry.Device);
            else
                _recent.Remove(name);
        }

        return result;
    }

    /// <summary>
    /// Точка монтирования носителя. Если система его не смонтировала, станция
    /// монтирует сама, и делает это в стороне от интерфейса: ожидание в
    /// несколько секунд заморозило бы экран.
    /// </summary>
    private string? MountPointFor(UsbDevice device)
    {
        if (!string.IsNullOrEmpty(device.MountPoint))
            return device.MountPoint;

        if (_mounted.TryGetValue(device.Name, out var ours))
            return ours.Path;

        if (_mounting.Add(device.Name))
        {
            var name = device.Name;
            var grace = TimeSpan.FromSeconds(_stationSettings.MountGraceSeconds);

            _ = Task.Run(() =>
            {
                var mounted = MountManager.Ensure(name, grace);
                Dispatcher.UIThread.Post(() =>
                {
                    if (mounted is not null)
                        _mounted[name] = mounted;
                    _mounting.Remove(name);
                });
            });
        }

        return null;
    }

    /// <summary>
    /// Отпускает то, что смонтировали мы, когда носитель вынули. Чужие
    /// монтирования не трогаем: их сделал рабочий стол для человека.
    /// </summary>
    private void ReleaseGoneMedia(IReadOnlyList<UsbDevice> devices)
    {
        var connected = devices.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in _mounted.Keys.Where(n => !connected.Contains(n)).ToArray())
        {
            var mount = _mounted[name];
            _mounted.Remove(name);
            Task.Run(() => MountManager.Release(mount));
        }
    }

    /// <summary>
    /// Опознаёт камеру и подтягивает сотрудника. Результат кэшируется до
    /// извлечения носителя.
    /// </summary>
    private CardInfo? Identify(UsbDevice device, string mount)
    {
        if (_identified.TryGetValue(mount, out var cached))
            return cached;

        try
        {
            // Файл на карте и есть единственный источник истины. Нет файла,
            // станция выдаёт номер и обязательно записывает его. Номер
            // сотрудника из имён записей только показывается: на заводских
            // настройках он одинаков у всех камер, поэтому закрепляет камеру
            // за человеком оператор на вкладке «Устройства».
            var recording = RecordingName.FromCard(mount);
            var personnel = recording?.HasPersonnelNo == true ? recording.PersonnelNo : "";

            using var registry = new DeviceRegistry(AppPaths.Database);
            var id = registry.ResolveByCard(mount, _stationSettings.StationNumber,
                device.Name, device.Name);

            var number = registry.FirmwareIdOf(id) ?? "";
            var name = registry.GetDeviceName(id);

            var staff = new StaffDirectory(AppPaths.Database);
            var person = staff.EmployeeOfDevice(id);

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

        // Оператор отменил загрузку: регистратор только заряжается.
        if (_chargeOnly.Contains(mountPoint))
        {
            port.State = PortState.ChargeOnly;
            return;
        }

        // Другой отсек обслуживается вне очереди: ждём его.
        if (_priority is { } first && first != mountPoint)
        {
            port.State = PortState.Detected;
            port.Detail = "в очереди";
            return;
        }

        var cts = new CancellationTokenSource();
        _cancels[mountPoint] = cts;
        _running[mountPoint] = deviceId;
        port.State = PortState.Scanning;

        var progress = new Progress<BackupProgress>(report =>
        {
            port.Progress = report.Progress;
            port.Detail = report.Detail;
            port.FilesLine = report.Detail;
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
                await _backups.RunAsync(deviceId, mountPoint, progress, cts.Token);
            }
            finally
            {
                _running.Remove(mountPoint);
                _cancels.Remove(mountPoint);
                cts.Dispose();

                // Приоритетный отсек закончил: очередь снова общая.
                if (_priority == mountPoint)
                    _priority = null;
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
            // Считаем том архива, а не тот, где лежит программа: оператор
            // смотрит на эту полосу, чтобы понять, куда ещё влезут записи.
            var root = Path.GetPathRoot(Path.GetFullPath(_stationSettings.BackupRoot));
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return;

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            var ratio = (double)used / drive.TotalSize;

            StorageLabel = $"хранилище {Size(used)} из {Size(drive.TotalSize)}";
            StorageWidth = Math.Clamp(ratio, 0, 1) * 150;
            // Тревожного красного в палитре станции нет: заполнение растёт от
            // бирюзового к тёмно-синему, и это видно, не мешая остальному.
            StorageBrush = new SolidColorBrush(Color.Parse(ratio switch
            {
                >= 0.9 => "#143A61",
                >= 0.75 => "#2F77AD",
                _ => "#3F9BA6",
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

        // Свои монтирования отпускаем при выходе: иначе карта останется
        // смонтированной, и рабочий стол не сможет с ней работать.
        foreach (var mount in _mounted.Values)
            MountManager.Release(mount);
        _mounted.Clear();
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
