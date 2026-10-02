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

    /// <summary>Идёт ли опрос носителей: два сразу ни к чему.</summary>
    private bool _polling;

    /// <summary>Карты, которые сейчас опознаются в стороне от интерфейса.</summary>
    private readonly HashSet<string> _identifying = new(StringComparer.Ordinal);

    /// <summary>
    /// Диспетчер окна, запомненный при создании. Фоновые задачи отвечают в
    /// него, а не в Dispatcher.UIThread: статическое свойство создаёт
    /// диспетчер заново, если его сбросили (так делает сессия тестов
    /// Avalonia между тестами), и поздний ответ задачи мог подменить
    /// диспетчер следующего теста.
    /// </summary>
    private readonly Dispatcher _ui = Dispatcher.UIThread;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<IReadOnlyList<UsbDevice>> _listDevices;
    private Settings _stationSettings = Services.Settings.Load();
    private readonly BackupService _backups;
    private readonly Dictionary<string, PortViewModel> _extraPorts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _mediaTasks = new();
    private readonly Func<Mounted?, bool> _releaseMount = MountManager.Release;
    private CancellationTokenSource _mountStop = new();
    private bool _stopping;
    private bool _disposed;
    private readonly Func<DateTime> _now = () => DateTime.UtcNow;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CardRecovery> _recoveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _returnAttempts = new(StringComparer.Ordinal);
    private DateTime? _busGlitchSince;
    private int _stopGeneration;
    [ObservableProperty] private bool _safeRemovalActive;
    [ObservableProperty] private bool _safeRemovalReady;
    [ObservableProperty] private string _safeRemovalStatus = "Копирование разрешено";

    [RelayCommand]
    private async Task StopCollection()
    {
        if (_stopping) return;
        _stopping = true;
        _stopGeneration++;
        SafeRemovalActive = true;
        SafeRemovalReady = false;
        foreach (var port in Ports.Concat(_extraPorts.Values)) port.SafeToRemove = false;
        SafeRemovalStatus = "Останавливаем задания и освобождаем носители";
        _mountStop.Cancel();
        foreach (var mount in _cancels.Keys.ToArray()) Cancel(mount);
        try
        {
            var jobs = _workers.Values.Concat(_mediaTasks).ToArray();
            try { await Task.WhenAll(jobs); }
            catch (Exception error) { CrashLog.Write("остановка сбора", error); }
            // Ответы монтирования уже поставлены в очередь диспетчера.
            await _ui.InvokeAsync(() => { });
            using var operation = OperationGuard.Acquire(exclusive: true);
            var mounts = _mounted.ToArray();
            var results = await Task.Run(() => mounts.Select(entry =>
                (entry.Key, entry.Value, Ok: _releaseMount(entry.Value))).ToArray());
            foreach (var result in results.Where(r => r.Ok))
                _mounted.Remove(result.Key);
            UpdateRemovalStatus(operationHeld: true);
        }
        catch (StationBusyException) { UpdateRemovalStatus(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SafeRemovalStatus = UserError.Report("Не удалось остановить сбор для извлечения", error);
        }
        finally { _stopping = false; }
    }

    [RelayCommand]
    private void ResumeCollection()
    {
        if (_stopping || !SafeRemovalActive) return;
        _mountStop.Dispose();
        _mountStop = new CancellationTokenSource();
        SafeRemovalActive = false;
        SafeRemovalReady = false;
        SafeRemovalStatus = "Копирование разрешено";
        _finished.Clear();
        _recoveries.Clear();
        _returnAttempts.Clear();
        foreach (var port in Ports.Concat(_extraPorts.Values)) port.SafeToRemove = false;
        Refresh();
    }

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
    private readonly Dictionary<long, string> _astraOwners = new();

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

    /// <summary>Имена устройств из последнего опроса: подпись на плитке вместо номера.</summary>
    private IReadOnlyDictionary<long, string> _deviceNames = new Dictionary<long, string>();

    /// <summary>Открыт ли доступ к закрытым разделам и до каких пор.</summary>
    private AccessGuard _access = new(0);

    /// <summary>Раздел, куда оператор шёл, когда его остановил пароль.</summary>
    private int _wantedTab;

    /// <summary>Пароль спрашивают перед выходом, а не перед разделом.</summary>
    private bool _askingForExit;
    private string? _bayMount;
    private string? _bayKey;

    /// <summary>Названия разделов для журнала: индекс совпадает с вкладкой.</summary>
    // Имена совпадают с надписями на вкладках: по журналу разбирают, куда
    // именно заходили, и другое имя там пришлось бы угадывать.
    private static readonly string[] TabNames =
        ["Сбор данных", "Запрос данных", "Устройства", "Сотрудники", "Журнал", "Настройки"];

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorVisible))]
    private string _errorMessage = "";

    public bool ErrorVisible => ErrorMessage.Length > 0;

    public void ShowError(Exception error) =>
        ErrorMessage = UserError.Report("Не удалось выполнить действие", error);

    [RelayCommand]
    private void DismissError() => ErrorMessage = "";

    // --- Обновление с флешки ------------------------------------------------

    /// <summary>
    /// Станция ждёт флешку с архивом обновления. Новые носители в это время
    /// не считаются камерами: их монтируем и ищем в корне архив релиза.
    /// </summary>
    [ObservableProperty]
    private bool _offlineWaiting;

    [ObservableProperty]
    private string _offlineStatus = "";

    /// <summary>Носители, подключённые до начала ожидания: с ними всё как обычно.</summary>
    private HashSet<string>? _offlineKnown;

    /// <summary>Носители, которые не выгружаются как камеры, пока их не вынут.</summary>
    private readonly HashSet<string> _offlineMedia = new(StringComparer.Ordinal);

    private bool _offlineChecking;

    [RelayCommand]
    private void StartOfflineUpdate()
    {
        if (!OperatingSystem.IsLinux())
        {
            Settings.Hint = "на этой платформе программа обновляется вручную";
            return;
        }
        if (_running.Count > 0 || BusyMarker.Busy())
        {
            Settings.Hint = "дождитесь конца сканирования или копирования";
            return;
        }

        _offlineKnown = _recent.Values.Select(entry => entry.Device.Name).ToHashSet(StringComparer.Ordinal);
        OfflineStatus = "Вставьте флешку с файлом обновления…";
        OfflineWaiting = true;
    }

    [RelayCommand]
    private void CancelOfflineUpdate() => StopOfflineWaiting(keep: null);

    private void StopOfflineWaiting(string? keep)
    {
        OfflineWaiting = false;
        _offlineKnown = null;
        // Флешка с обновлением так и остаётся не камерой, пока её не вынут;
        // остальные носители, пришедшие во время ожидания, выгружаются как обычно.
        _offlineMedia.RemoveWhere(name => name != keep);
    }

    /// <summary>
    /// Отделяет носители, пришедшие во время ожидания обновления, от камер.
    /// Возвращает то, что раскладывается по окнам как обычно.
    /// </summary>
    private IReadOnlyList<UsbDevice> SplitOfflineMedia(IReadOnlyList<UsbDevice> devices)
    {
        var names = devices.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        _offlineMedia.RemoveWhere(name => !names.Contains(name));

        if (OfflineWaiting && _offlineKnown is not null)
            foreach (var device in devices)
                if (!_offlineKnown.Contains(device.Name))
                    _offlineMedia.Add(device.Name);

        if (OfflineWaiting)
            WatchOfflineMedia(devices.Where(d => _offlineMedia.Contains(d.Name)).ToList());

        return _offlineMedia.Count == 0
            ? devices
            : devices.Where(d => !_offlineMedia.Contains(d.Name)).ToList();
    }

    private void WatchOfflineMedia(IReadOnlyList<UsbDevice> media)
    {
        if (_offlineChecking)
            return;

        if (media.Count == 0)
        {
            OfflineStatus = "Вставьте флешку с файлом обновления…";
            return;
        }

        var targets = media
            .Select(device => (device.Name, Mount: MountPointFor(device)))
            .Where(target => target.Mount is not null)
            .Select(target => (target.Name, Mount: target.Mount!))
            .ToList();
        if (targets.Count == 0)
        {
            OfflineStatus = "Флешка подключена, открываем…";
            return;
        }

        _offlineChecking = true;
        _ = Task.Run(() => CheckOfflineMedia(targets, Updater.InstalledTag(), Updater.Platform()))
            .ContinueWith(task => _ui.Post(() =>
            {
                _offlineChecking = false;
                var (done, status, tag, name, kick) = task.IsCompletedSuccessfully
                    ? task.Result
                    : (false, "Флешка не читается", "", "", "");

                if (!done)
                {
                    if (OfflineWaiting)
                        OfflineStatus = status;
                    return;
                }

                StopOfflineWaiting(keep: name);
                if (kick == Updater.UpdateStarted)
                {
                    _actions.Write(ActionLog.Settings, $"обновление {tag} с флешки передано установщику");
                    Settings.Hint = $"обновление {tag} передано установщику, программа скоро перезапустится";
                }
                else
                {
                    Updater.ClearOfflineSpool();
                    Settings.Hint = kick;
                }
            }));
    }

    /// <summary>Ищет архив на флешках и готовит его для службы обновления. Идёт в стороне от интерфейса.</summary>
    private static (bool Done, string Status, string Tag, string Name, string Kick) CheckOfflineMedia(
        IReadOnlyList<(string Name, string Mount)> targets, string installed, string platform)
    {
        var status = "На подключённой флешке обновление не найдено";
        foreach (var (name, mount) in targets)
        {
            var found = Updater.FindOfflineArchives(mount, platform);
            if (found.Count == 0)
                continue;

            var (tag, archive) = found[0];
            if (!Updater.NeedsUpdate(installed, tag))
            {
                status = $"Версия {tag} уже установлена";
                continue;
            }

            var (staged, error) = Updater.StageOffline(archive, platform);
            if (error is not null)
            {
                status = $"Архив {tag}: {error}";
                continue;
            }

            // Сеть здесь не нужна: архив уже на станции.
            return (true, "", staged!, name, Updater.StartService(checkNetwork: false));
        }
        return (false, status, "", "", "");
    }

    /// <summary>
    /// Сводка по отсекам в шапке: сколько копируется, сколько готово, сколько
    /// в ошибке и сколько окон свободно. Оператор смотрит на неё, не обходя
    /// доску глазами.
    /// </summary>
    [ObservableProperty]
    private string _summary = "";

    /// <summary>Состояние сети: по нему видно, дойдут ли файлы до сервера.</summary>
    [ObservableProperty]
    private bool _networkUp;

    [ObservableProperty]
    private string _networkLabel = "сеть не проверена";

    /// <summary>Состояние отправки на сервер: включена ли и сколько ждёт очередь.</summary>
    [ObservableProperty]
    private bool _ftpEnabled;

    [ObservableProperty]
    private string _ftpLabel = "отправка выключена";

    /// <summary>Отправка идёт прямо сейчас: второй раз запускать не нужно.</summary>
    private bool _sending;

    /// <summary>О чём станция уже сообщила: повторять одно и то же незачем.</summary>
    private string _lastTrouble = "";

    /// <summary>Идёт показ состояний: опрос носителей в это время не мешает.</summary>
    private bool _demonstrating;

    /// <summary>Веб-панель, если она включена в настройках.</summary>
    private readonly WebPanel? _web;

    /// <summary>
    /// Выбранная компоновка доски: сетка, список или схема стойки. Все три
    /// живут на одной кодовой базе и переключаются оператором.
    /// </summary>
    [ObservableProperty]
    private string _layout = "grid";

    /// <summary>Сколько окон в строке: задаётся в настройках.</summary>
    [ObservableProperty]
    private int _baysPerRow = 3;

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
    public bool AccessAllowed => !Services.Settings.Load().Unreadable && _access.Check(DateTime.Now);

    public MainWindowViewModel() : this(UsbWatcher.List)
    {
    }

    /// <summary>Опрос носителей передаётся снаружи: так экран можно проверить без железа.</summary>
    public MainWindowViewModel(Func<IReadOnlyList<UsbDevice>> listDevices)
    {
        _listDevices = listDevices;
        AppPaths.EnsureCreated();
        _backups = new BackupService(AppPaths.Database);
        Version = VersionInfo.Label();

        // Число окон задаётся в настройках: у станций от шести до тридцати
        // отсеков, и окна должны совпадать с железом.
        BaysPerRow = Math.Clamp(_stationSettings.BaysPerRow, 2, 6);
        var bays = Math.Clamp(_stationSettings.BayCount, 6, 30);
        for (var i = 0; i < bays; i++)
            Ports.Add(new PortViewModel { Slot = i });
        Settings.CanChangeBayCount = count => !Ports.Skip(count).Any(p => p.IsBusy);
        Settings.BaysChanged += ResizePorts;

        // Журнал за годы работы разрастается вместе с базой, поэтому при
        // запуске он подрезается до последних событий.
        _actions.Trim(20_000);

        // Архив по умолчанию лежит рядом с программой и всегда на месте,
        // поэтому метку ему станция ставит сама. Том, выбранный оператором,
        // помечается при выборе: там метка и нужна, чтобы отличить
        // несмонтированный диск от пустого.
        if (_stationSettings.BackupRoot == AppPaths.BackupsRoot)
            ArchiveGuard.Mark(_stationSettings.BackupRoot);

        // Панель поднимается один раз при запуске: порт нельзя переоткрыть на
        // ходу, поэтому её включение вступает в силу после перезапуска.
        if (_stationSettings.WebEnabled)
        {
            _web = new WebPanel(AppPaths.Database);
            if (_web.Start(_stationSettings))
                _actions.Write(ActionLog.Settings,
                    $"веб-панель открыта на порту {WebPanel.Port(_stationSettings)}");
        }

        UpdateNetwork();

        // При запуске том архива ещё не опрошен: доска покажет нули, а первый
        // же опрос через две секунды подставит настоящие числа.
        UpdateSummary(StorageState.Unknown(_stationSettings.BackupRoot));
        FtpEnabled = _stationSettings.FtpEnabled;
        FtpLabel = _stationSettings.FtpEnabled ? "отправка включена" : "отправка выключена";

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
    /// <summary>
    /// Показывает все состояния окон по очереди. Задание требует, чтобы каждое
    /// состояние было воспроизводимо для приёмки, а ошибку копирования и
    /// недоступный архив на живой станции по заказу не устроить.
    /// </summary>
    [RelayCommand]
    private async Task Demonstrate()
    {
        if (_demonstrating)
            return;

        _demonstrating = true;
        _actions.Write(ActionLog.Settings, "показ состояний окон для приёмки");

        var states = new[]
        {
            PortState.Detected, PortState.Scanning, PortState.Copying,
            PortState.Done, PortState.Failed, PortState.ChargeOnly, PortState.Idle,
        };

        try
        {
            foreach (var state in states)
            {
                for (var i = 0; i < Ports.Count; i++)
                {
                    var port = Ports[i];
                    port.CameraId = state == PortState.Idle ? "" : $"{1234567 + i}";
                    port.Employee = state == PortState.Idle ? "" : "Показ состояний";
                    port.Department = state == PortState.Idle ? "" : "приёмка";
                    port.PersonnelNo = state == PortState.Idle ? "" : "000000";
                    port.Progress = state switch
                    {
                        PortState.Copying => 0.35 + i * 0.05,
                        PortState.Done or PortState.Failed => 1,
                        _ => 0,
                    };
                    port.FilesLine = state switch
                    {
                        PortState.Copying => $"{12 + i} из 40",
                        PortState.Done => "40 файлов, 1.2 ГБ",
                        PortState.Failed => "не скопировано: 3 файла",
                        _ => "",
                    };
                    port.State = state;
                }

                Status = $"показ состояний: {Ports[0].StateText}";
                await Task.Delay(2500);
            }

            Status = "показ состояний закончен";
        }
        finally
        {
            _demonstrating = false;
        }
    }

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
    /// Раскладка экранной клавиатуры. Станция сенсорная, физической
    /// клавиатуры у неё нет, а пароль администратора буквенный не реже
    /// цифрового, поэтому одних цифр мало.
    /// </summary>
    private static readonly string[] KeyLayout =
    [
        "1234567890",
        "qwertyuiop",
        "asdfghjkl",
        "zxcvbnm.-_",
    ];

    [ObservableProperty] private bool _keysUpper;

    /// <summary>Клавиши по рядам, в текущем регистре.</summary>
    public IReadOnlyList<IReadOnlyList<string>> KeyRows => KeyLayout
        .Select(row => (IReadOnlyList<string>)row
            .Select(key => KeysUpper ? char.ToUpperInvariant(key).ToString() : key.ToString())
            .ToArray())
        .ToArray();

    partial void OnKeysUpperChanged(bool value) => OnPropertyChanged(nameof(KeyRows));

    [RelayCommand]
    private void ToggleKeysCase() => KeysUpper = !KeysUpper;

    /// <summary>
    /// Куда идёт нажатие: в учётную запись или в пароль. Ставится по тому,
    /// в каком поле стоит курсор.
    /// </summary>
    [ObservableProperty] private bool _editingAccount;

    /// <summary>Нажатие на экранной клавиатуре.</summary>
    [RelayCommand]
    private void PasswordKey(string? key)
    {
        PasswordError = "";

        if (EditingAccount)
            AccountInput = Typed(AccountInput, key);
        else
            PasswordInput = Typed(PasswordInput, key);
    }

    private static string Typed(string text, string? key) => key switch
    {
        null or "" => text,
        "clear" => "",
        "<" => text.Length > 0 ? text[..^1] : "",
        _ => text.Length < 32 ? text + key : text,
    };

    partial void OnPasswordInputChanged(string value) => OnPropertyChanged(nameof(PasswordMask));

    [RelayCommand]
    private void ConfirmPassword()
    {
        // Настройки читаются заново: пароль могли сменить в этом же сеансе.
        var settings = Services.Settings.Load();

        if (settings.Unreadable)
        {
            PasswordInput = "";
            PasswordError = "настройки станции не читаются: доступ закрыт";
            return;
        }

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
        _bayMount = port.MountPoint;
        _bayKey = port.DeviceKey;
        BayDevice = port.CameraId;
        BayConfirm = "";
        BayVisible = true;
    }

    [RelayCommand]
    private void CloseBay()
    {
        BayVisible = false;
        Bay = null;
        _bayMount = null;
        _bayKey = null;
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
        if (Bay is not { } port || MountOf(port) is not { } mount
            || HasAnotherOwner(_identified[mount].DeviceId, mount))
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
        (!BayVisible || port != Bay || port.MountPoint == _bayMount && port.DeviceKey == _bayKey)
            && port.MountPoint is { } mount && _identified.TryGetValue(mount, out var card)
            && card.DeviceId > 0 ? mount : null;

    private bool HasAnotherOwner(long deviceId, string mount) =>
        _astraOwners.TryGetValue(deviceId, out var owner) && owner != mount;

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
        // День без ведущего нуля: по-русски пишут «2 сентября», а не «02».
        ClockDate = now.ToString("d MMMM yyyy", Ru);

        // Сеть опрашиваем раз в пять секунд: чаще незачем, а состояние в
        // строке разделов должно быть свежим.
        if (now.Second % 5 == 0)
            UpdateNetwork();

        // Очередь отправки разбирается раз в пятнадцать секунд: файлы уже в
        // архиве, спешить некуда, а частые попытки при мёртвой сети только
        // жгут счётчик неудач.
        if (now.Second % 15 == 0)
            PumpServerQueue();

        // Раздел, забытый открытым, закрывается сам: станция стоит в общем
        // помещении.
        var wasUnlocked = _access.Unlocked;
        if (wasUnlocked && !_access.Check(now))
            AccessExpired?.Invoke();
    }

    /// <summary>Опрашивает носители и раскладывает их по гнёздам.</summary>
    public void Refresh()
    {
        // Во время показа состояний доска занята: опрос перерисовал бы её.
        if (_demonstrating)
            return;

        // Адрес станции выдаёт сеть объекта и меняет без спроса, поэтому
        // список адресов панели пересчитывается, пока раздел открыт.
        if (Settings.IsWebSection)
            Settings.ShowWebLinks();

        // Опрос носителей это запуск lsblk и чтение с самих карт: на станции
        // это десятки миллисекунд, а с задумавшейся картой и куда больше.
        // В потоке интерфейса такое видно как рывки, поэтому чтение идёт в
        // стороне, а на доску попадает уже готовый ответ.
        if (_polling)
            return;

        _polling = true;
        var settings = Services.Settings.Load();
        var generation = _stopGeneration;

        _ = Task.Run(() =>
        {
            var archiveRoot = settings.ResolveBackupRoot();
            IReadOnlyList<UsbDevice> found;
            try
            {
                // Диск, на котором лежит архив, источником не считается: иначе
                // станция принялась бы копировать архив сам в себя.
                found = _listDevices()
                    .Where(d => !ArchiveGuard.IsArchiveMedia(d.MountPoint, archiveRoot))
                    .ToList();
            }
            catch (Exception e)
            {
                CrashLog.Write("опрос носителей", e);
                found = [];
            }

            var storage = StorageState.Read(archiveRoot);
            var names = DeviceRegistry.ReadNames(AppPaths.Database);

            _ui.Post(() =>
            {
                _polling = false;
                _stationSettings = settings;
                _deviceNames = names;

                if (!_disposed && !_demonstrating && generation == _stopGeneration)
                    Apply(found, storage);
            });
        });
    }

    /// <summary>
    /// Раскладывает опрошенные носители по гнёздам. Работает в потоке
    /// интерфейса и ничего с дисков не читает: всё нужное уже прочитано.
    /// </summary>
    private void Apply(IReadOnlyList<UsbDevice> found, StorageState storage)
    {
        if (SafeRemovalActive && !_stopping)
            foreach (var entry in _mounted.ToArray())
                if (found.FirstOrDefault(d => d.Name == entry.Key) is { MountPoint: null }
                    || !entry.Value.Ours && found.All(d => d.Name != entry.Key))
                    _mounted.Remove(entry.Key);
        var all = HoldBriefly(found);
        var devices = SplitOfflineMedia(all);
        var present = devices.Where(d => !_missing.Contains(DeviceKey(d))).Select(MountPointFor).Where(m => !string.IsNullOrEmpty(m))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in _astraOwners.Keys.Where(id => !present.Contains(_astraOwners[id])
                     && !_recoveries.Values.Any(r => r.DeviceId == id)).ToArray())
            _astraOwners.Remove(id);

        var deviceKeys = devices.Select(DeviceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var port in Ports.Where(p => p.DeviceKey is { } key && !deviceKeys.Contains(key)))
            port.Clear();
        foreach (var key in _extraPorts.Keys.Where(k => !deviceKeys.Contains(k)).ToArray())
            _extraPorts.Remove(key);

        foreach (var device in devices)
        {
            var key = DeviceKey(device);
            var port = Ports.FirstOrDefault(p => p.DeviceKey == key);
            if (port is null)
            {
                var free = Ports.FirstOrDefault(p => p.DeviceKey is null);
                if (_extraPorts.TryGetValue(key, out port))
                {
                    if (free is not null)
                    {
                        port.Slot = free.Slot;
                        Ports[free.Slot] = port;
                        _extraPorts.Remove(key);
                    }
                }
                else if (free is not null) port = free;
                else _extraPorts[key] = port = new PortViewModel { Slot = -1 };
                if (port.DeviceKey is null) port.Clear();
                port.DeviceKey = key;
            }
            if (_missing.Contains(key))
            {
                port.MountPoint = null;
                port.State = PortState.Waiting;
                port.Detail = _busGlitchSince is not null
                    ? "Сбой USB-шины, ждём возврата карты"
                    : "Устройство переподключается, ждём до 120 секунд";
                continue;
            }
            var mount = MountPointFor(device);
            port.MountPoint = mount;
            var cameraId = "";
            var detail = mount is null ? "Подготовка носителя" : "Определение ID";
            var personnel = "";
            var employee = "";
            var department = "";

            if (SafeRemovalActive)
            {
                if (mount is not null)
                    _mounted[device.Name] = new Mounted(mount, MountManager.IsOurs(mount));
                port.State = PortState.Stopped;
                port.SafeToRemove = SafeRemovalReady;
                continue;
            }

            if (mount is not null && Identify(device, mount) is { } card)
            {
                if (_recoveries.TryGetValue(key, out var recovery))
                {
                    if (card.DeviceId <= 0 && _now() < recovery.Until)
                    {
                        _identified.Remove(mount);
                        port.State = PortState.Waiting;
                        port.Detail = "Карта пока не читается, ждём возвращения";
                        continue;
                    }
                    if (card.DeviceId != recovery.DeviceId || _returnAttempts.GetValueOrDefault(key) > 3)
                    {
                        port.State = PortState.Failed;
                        port.Detail = card.DeviceId != recovery.DeviceId
                            ? "ID вернувшегося носителя изменился, продолжение запрещено"
                            : "Карта отключилась повторно: три попытки исчерпаны";
                        port.FilesLine = port.Detail;
                        _finished[mount] = BackupStage.Failed;
                        continue;
                    }
                    if (_running.ContainsKey(recovery.MountPoint))
                    {
                        port.State = PortState.Waiting;
                        port.Detail = "Карта вернулась, ожидаем остановки прежнего задания";
                        continue;
                    }
                    if (_chargeOnly.Remove(recovery.MountPoint)) _chargeOnly.Add(mount);
                    _finished.Remove(recovery.MountPoint);
                    _finished.Remove(mount);
                    if (_astraOwners.GetValueOrDefault(card.DeviceId) == recovery.MountPoint)
                        _astraOwners[card.DeviceId] = mount;
                    _recoveries.Remove(key);
                }
                // Имя, если оператор его задал, иначе номер, как в Python-версии.
                cameraId = _deviceNames.TryGetValue(card.DeviceId, out var named) ? named : card.CameraId;
                detail = card.Origin;
                personnel = card.PersonnelNo;
                employee = card.Employee;
                department = card.Department;
                var failure = card.DeviceId <= 0 ? detail
                    : HasAnotherOwner(card.DeviceId, mount)
                        ? $"Дубликат ID устройства {card.DeviceId}"
                    : null;
                if (failure is not null)
                {
                    port.CameraId = cameraId;
                    port.State = PortState.Failed;
                    port.Detail = failure;
                    port.FilesLine = port.Detail;
                    port.Progress = 0;
                    continue;
                }
                _astraOwners[card.DeviceId] = mount;
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
            if (mount is not null && _chargeOnly.Contains(mount))
            {
                port.Progress = 0;
                port.FilesLine = "";
                port.State = PortState.ChargeOnly;
            }
            else if (!busy)
            {
                port.Detail = detail;
                port.FilesLine = "";
                port.State = PortState.Detected;
            }
            else if (mount is not null && _finished.TryGetValue(mount, out var finished))
                port.State = finished == BackupStage.Done ? PortState.Done : PortState.Failed;
        }

        // Камеру вынули, забываем итог, чтобы при следующем подключении
        // выгрузка началась заново.
        foreach (var gone in _finished.Keys.Where(m => !present.Contains(m)).ToArray())
            _finished.Remove(gone);

        foreach (var gone in _identified.Keys.Where(m => !present.Contains(m)).ToArray())
            if (!_recoveries.Values.Any(r => r.MountPoint == gone)) _identified.Remove(gone);

        foreach (var gone in _chargeOnly.Where(m => !present.Contains(m)).ToArray())
            if (!_recoveries.Values.Any(r => r.MountPoint == gone)) _chargeOnly.Remove(gone);

        if (_priority is { } waiting && !present.Contains(waiting))
            _priority = null;

        // Флешка с обновлением остаётся смонтированной, пока её не вынут.
        ReleaseGoneMedia(all);
        if (SafeRemovalActive && !_stopping) UpdateRemovalStatus();

        Status = devices.Count == 0
            ? "носители не подключены"
            : $"носителей: {devices.Count}";

        UpdateStorage(storage);
        UpdateSummary(storage);
        if (_extraPorts.Count > 0)
            Status += $". не показано носителей: {_extraPorts.Count}; сбор продолжается";
        ApplyRemoteCommands();
    }

    private static string DeviceKey(UsbDevice device) => string.IsNullOrEmpty(device.FileSystemUuid)
        ? device.Name : "uuid:" + device.FileSystemUuid;

    private void UpdateRemovalStatus(bool operationHeld = false)
    {
        SafeRemovalReady = false;
        foreach (var port in Ports.Concat(_extraPorts.Values)) port.SafeToRemove = false;
        try
        {
            using var operation = operationHeld ? null : OperationGuard.Acquire(exclusive: true);
            SafeRemovalReady = _mounted.Count == 0
                && !_workers.Values.Concat(_mediaTasks).Any(task => !task.IsCompleted);
            SafeRemovalStatus = SafeRemovalReady
                ? "Задания остановлены. Можно извлекать регистраторы"
                : _mounted.Values.Any(m => !m.Ours)
                    ? "Копирование остановлено. Выполните безопасное извлечение в системе"
                    : "Не удалось размонтировать носитель. Извлекать его пока нельзя";
        }
        catch (StationBusyException)
        {
            SafeRemovalReady = false;
            SafeRemovalStatus = "Идёт выгрузка или обслуживание. Дождитесь и нажмите «Стоп» снова";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SafeRemovalStatus = UserError.Report("Не удалось проверить безопасность извлечения", error);
        }
        foreach (var port in Ports.Concat(_extraPorts.Values)) port.SafeToRemove = SafeRemovalReady;
    }

    private void ResizePorts(int count, int perRow)
    {
        while (Ports.Count > count && Ports[^1].IsFree)
            Ports.RemoveAt(Ports.Count - 1);
        while (Ports.Count < count)
            Ports.Add(new PortViewModel { Slot = Ports.Count });
        BaysPerRow = perRow;
        Refresh();
    }

    /// <summary>
    /// Держит недавно пропавшие носители в списке ещё полторы длительности
    /// опроса. Регистратор считается извлечённым только после этой выдержки:
    /// иначе один неудачный опрос сбрасывал бы ход выгрузки.
    /// </summary>
    private IReadOnlyList<UsbDevice> HoldBriefly(IReadOnlyList<UsbDevice> devices)
    {
        var now = _now();
        var hold = _poll.Interval * 1.5;
        var present = devices.Select(DeviceKey).ToHashSet(StringComparer.Ordinal);
        _missing.Clear();
        if (_recent.Count > 1 && !_recent.Keys.Any(present.Contains))
            _busGlitchSince ??= now;
        else
            _busGlitchSince = null;
        foreach (var device in devices)
        {
            var key = DeviceKey(device);
            foreach (var old in _recent.Where(entry => entry.Value.Device.Name == device.Name && entry.Key != key).ToArray())
            {
                ForgetCard(old.Key, old.Value.Device);
                _recent.Remove(old.Key);
            }
            if (_recent.TryGetValue(key, out var previous)
                && (previous.Device.Name != device.Name || previous.Device.MountPoint != device.MountPoint))
                BeginRecovery(key, previous.Device, previous.Seen.AddSeconds(120));
            _recent[key] = (device, now);
        }
        var result = devices.ToList();
        foreach (var (key, entry) in _recent.ToArray())
        {
            if (present.Contains(key))
                continue;
            BeginRecovery(key, entry.Device, entry.Seen.AddSeconds(120));
            var waiting = _recoveries.TryGetValue(key, out var recovery)
                ? now < recovery.Until : now - entry.Seen < hold;
            var busHold = _busGlitchSince is { } since && now - since < TimeSpan.FromSeconds(20);
            if (waiting || busHold)
            {
                _missing.Add(key);
                result.Add(entry.Device);
            }
            else
            {
                ForgetCard(key, entry.Device);
                _recent.Remove(key);
            }
        }
        return result;
    }

    private void BeginRecovery(string key, UsbDevice device, DateTime until)
    {
        if (SafeRemovalActive || _disposed) return;
        if (_recoveries.ContainsKey(key)) return;
        var mount = device.MountPoint ?? _mounted.GetValueOrDefault(device.Name)?.Path;
        if (mount is null) return;
        Cancel(mount);
        if (string.IsNullOrEmpty(device.FileSystemUuid)
            || !_identified.TryGetValue(mount, out var info) || info.DeviceId <= 0) return;
        _recoveries[key] = new CardRecovery(info.DeviceId, mount, until);
        _returnAttempts[key] = _returnAttempts.GetValueOrDefault(key) + 1;
        _identified.Remove(mount);
    }

    private void ForgetCard(string key, UsbDevice device)
    {
        var mount = device.MountPoint ?? _mounted.GetValueOrDefault(device.Name)?.Path;
        if (mount is not null)
        {
            Cancel(mount);
            _finished.Remove(mount);
            _identified.Remove(mount);
            _chargeOnly.Remove(mount);
            foreach (var id in _astraOwners.Keys.Where(id => _astraOwners[id] == mount).ToArray())
                _astraOwners.Remove(id);
        }
        _recoveries.Remove(key);
        _returnAttempts.Remove(key);
    }

    /// <summary>
    /// Точка монтирования носителя. Если система его не смонтировала, станция
    /// монтирует сама, и делает это в стороне от интерфейса: ожидание в
    /// несколько секунд заморозило бы экран.
    /// </summary>
    private string? MountPointFor(UsbDevice device)
    {
        if (SafeRemovalActive || _disposed)
            return device.MountPoint;
        if (!string.IsNullOrEmpty(device.MountPoint))
        {
            _mounted[device.Name] = new Mounted(device.MountPoint, MountManager.IsOurs(device.MountPoint));
            return device.MountPoint;
        }

        if (_mounted.TryGetValue(device.Name, out var ours))
            return ours.Path;

        if (_mounting.Add(device.Name))
        {
            var name = device.Name;
            var grace = TimeSpan.FromSeconds(_stationSettings.MountGraceSeconds);

            var token = _mountStop.Token;
            var task = Task.Run(() =>
            {
                var mounted = MountManager.Ensure(name, grace, token);
                _ui.Post(() =>
                {
                    if (_disposed)
                    {
                        if (mounted is not null) _ = Task.Run(() => _releaseMount(mounted));
                        _mounting.Remove(name);
                        return;
                    }
                    if (mounted is not null)
                        _mounted[name] = mounted;
                    _mounting.Remove(name);
                });
            });
            TrackMediaTask(task);
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
            if (_running.ContainsKey(mount.Path) || _identifying.Contains(mount.Path)) continue;
            if (!mount.Ours)
            {
                _mounted.Remove(name);
                continue;
            }
            var task = Task.Run(() => _releaseMount(mount));
            TrackMediaTask(task);
            _ = task.ContinueWith(result => _ui.Post(() =>
            {
                if (result.IsCompletedSuccessfully && result.Result
                    && _mounted.GetValueOrDefault(name) == mount)
                    _mounted.Remove(name);
                if (SafeRemovalActive && !_stopping) UpdateRemovalStatus();
            }));
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
        if (SafeRemovalActive || _disposed) return null;

        // Чтение носителя и базы не блокирует интерфейс при подключении.
        if (_identifying.Add(mount))
        {
            var name = device.Name;

            var task = Task.Run(() =>
            {
                var info = ReadCard(name, mount);
                var connected = _listDevices().Any(current => current.Name == name
                    && DeviceKey(current) == DeviceKey(device));

                _ui.Post(() =>
                {
                    if (!_disposed && !SafeRemovalActive && info is not null && connected
                        && _recent.TryGetValue(DeviceKey(device), out var latest) && latest.Device == device)
                        _identified[mount] = info;

                    _identifying.Remove(mount);
                });
            });
            TrackMediaTask(task);
        }

        return null;
    }

    /// <summary>
    /// Читает карту и базу. Работает в стороне от интерфейса, поэтому берёт
    /// только то, что можно прочитать без доски.
    /// </summary>
    private CardInfo? ReadCard(string deviceName, string mount)
    {
        try
        {
            // ID берётся из данных регистратора, без записи на карту.
            // Номер сотрудника остаётся дополнительной информацией.
            var recording = RecordingName.FromCard(mount);
            var personnel = recording?.HasPersonnelNo == true ? recording.PersonnelNo : "";

            using var registry = new DeviceRegistry(AppPaths.Database);
            var id = registry.ResolveByCard(mount, _stationSettings.StationNumber,
                deviceName, deviceName, connected: () => StillConnected(deviceName));

            var staff = new StaffDirectory(AppPaths.Database);
            var person = staff.EmployeeOfDevice(id);

            var info = new CardInfo(
                id,
                id.ToString(),
                $"Папка {DeviceRegistry.DeviceDirPrefix}{id}",
                personnel,
                person?.FullName ?? "",
                staff.DepartmentPath(person?.DepartmentId));

            return info;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new CardInfo(0, "", error.Message, "", "", "");
        }
        catch (Exception)
        {
            // База занята другим действием, повторим на следующем опросе.
            return null;
        }
    }

    private bool StillConnected(string deviceName) =>
        _listDevices().Any(device => device.Name == deviceName);

    /// <summary>
    /// Запускает выгрузку камеры, если она ещё не идёт. Плитка показывает ход:
    /// заливка растёт по мере копирования.
    /// </summary>
    private void StartBackup(PortViewModel port, long deviceId, string mountPoint)
    {
        if (SafeRemovalActive || _disposed) return;
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
        var key = port.DeviceKey;
        _cancels[mountPoint] = cts;
        _running[mountPoint] = deviceId;
        port.State = PortState.Scanning;

        var progress = new Progress<BackupProgress>(report =>
        {
            if (_disposed || SafeRemovalActive || cts.IsCancellationRequested || _chargeOnly.Contains(mountPoint))
                return;

            var current = Ports.Concat(_extraPorts.Values).FirstOrDefault(p =>
                p.DeviceKey == key && p.MountPoint == mountPoint);
            if (current is null || !_identified.TryGetValue(mountPoint, out var card) || card.DeviceId != deviceId)
                return;
            current.Progress = report.Progress;
            current.Detail = report.Detail;
            current.FilesLine = report.Detail;
            current.State = report.Stage switch
            {
                BackupStage.Scanning => PortState.Scanning,
                BackupStage.Copying => PortState.Copying,
                BackupStage.Done => PortState.Done,
                _ => PortState.Failed,
            };

            if (report.Stage is BackupStage.Done or BackupStage.Failed)
            {
                _finished[mountPoint] = report.Stage;

                if (_stationSettings.VoiceHints && current.Slot >= 0)
                    Voice.Say(report.Stage == BackupStage.Done
                        ? $"Отсек {current.Slot + 1}, копирование завершено"
                        : $"Отсек {current.Slot + 1}, ошибка загрузки", DateTime.Now);
            }
        });

        _workers[mountPoint] = Task.Run(async () =>
        {
            try
            {
                await _backups.RunAsync(deviceId, mountPoint, progress, cts.Token);
            }
            catch (StationBusyException)
            {
                _ui.Post(() =>
                {
                    var current = Ports.Concat(_extraPorts.Values).FirstOrDefault(p =>
                        p.DeviceKey == key && p.MountPoint == mountPoint);
                    if (current is not null)
                    {
                        current.State = PortState.Detected;
                        current.Detail = "станция обновляется, ожидаем освобождения";
                    }
                    _finished.Remove(mountPoint);
                });
            }
            catch (DeviceLostException)
            {
                _ui.Post(() =>
                {
                    if (_disposed || SafeRemovalActive || key is null) return;
                    var device = _recent.GetValueOrDefault(key).Device;
                    if (device is not null) BeginRecovery(key, device, _now().AddSeconds(120));
                    var current = Ports.Concat(_extraPorts.Values).FirstOrDefault(p => p.DeviceKey == key);
                    if (current is not null)
                    {
                        current.State = PortState.Waiting;
                        current.Detail = "Карта перестала отвечать, ждём возвращения";
                    }
                });
            }
            finally
            {
                _ui.Post(() =>
                {
                    _running.Remove(mountPoint);
                    _workers.Remove(mountPoint);
                    _cancels.Remove(mountPoint);
                    cts.Dispose();

                    if (_priority == mountPoint)
                        _priority = null;
                });
            }
        });
    }

    private void TrackMediaTask(Task task)
    {
        _mediaTasks.Add(task);
        _ = task.ContinueWith(_ => _ui.Post(() => _mediaTasks.Remove(task)));
    }

    /// <summary>
    /// Отправляет из очереди то, что накопилось. Обрыв сети не теряет записи:
    /// они остаются в архиве и в очереди до следующей попытки.
    /// </summary>
    private void PumpServerQueue()
    {
        var settings = Services.Settings.Load();
        FtpEnabled = settings.FtpEnabled;

        if (!settings.FtpEnabled)
        {
            FtpLabel = "отправка выключена";
            return;
        }

        if (_sending)
            return;

        if (!NetworkUp)
        {
            FtpLabel = "сети нет, очередь ждёт";
            return;
        }

        _sending = true;

        _ = Task.Run(() =>
        {
            var sent = 0;
            var failed = 0;
            var waiting = 0;

            try
            {
                var queue = new FtpQueue(AppPaths.Database);
                queue.Prune();

                foreach (var item in queue.Next(10))
                {
                    var result = FtpSender.Send(settings, item.Path);
                    if (result.Ok)
                    {
                        queue.Done(item.Id);
                        sent++;
                    }
                    else
                    {
                        queue.Failed(item.Id, result.Message);
                        failed++;

                        // Первая же неудача обычно означает, что сервер или
                        // сеть недоступны целиком: остальные попытки только
                        // израсходуют счётчик.
                        break;
                    }
                }

                waiting = queue.Count();

                if (sent > 0)
                    _actions.Write(ActionLog.Export,
                        $"на сервер отправлено {Numerals.Plural(sent, "файл", "файла", "файлов")}");
            }
            catch (Exception e)
            {
                CrashLog.Write("отправка на сервер", e);
            }
            finally
            {
                var label = failed > 0
                    ? $"сервер не принял, в очереди {waiting}"
                    : waiting > 0
                        ? $"в очереди {waiting}"
                        : "очередь пуста";

                _ui.Post(() =>
                {
                    FtpLabel = label;
                    _sending = false;
                });
            }
        });
    }

    /// <summary>
    /// Сообщает о происшествии: пишет в журнал и подаёт звук, если он включён.
    /// Об одном и том же станция говорит один раз, пока положение не изменится.
    /// </summary>
    private void Trouble(string what)
    {
        Status = what;
        if (_lastTrouble == what)
            return;

        _lastTrouble = what;

        // Запись в журнал и звук идут в стороне: и то и другое трогает диск,
        // а происшествие показывается сразу.
        var sound = _stationSettings.AlarmSound;
        _ = Task.Run(() =>
        {
            _actions.Write(ActionLog.Cleanup, what);

            if (sound)
                Alarm.Sound(DateTime.Now);
        });
    }

    /// <summary>
    /// Выполняет то, о чём просила панель. Веб-запрос идёт в своём потоке и
    /// доску сбора трогать не может, поэтому просьбы разбираются здесь.
    /// </summary>
    private void ApplyRemoteCommands()
    {
        foreach (var command in StationCommands.Take())
        {
            if (command.Action == StationAction.Restart)
            {
                _actions.Write(ActionLog.Settings, "перезапуск по просьбе панели");

                // Ненулевой код завершения службе виден как сбой, и она
                // поднимает станцию заново; парольный выход даёт ноль и
                // останавливает её намеренно.
                Environment.Exit(3);
                return;
            }

            var port = Ports.FirstOrDefault(p => p.Slot == command.Slot);
            if (port is null || MountOf(port) is not { } mount)
                continue;

            switch (command.Action)
            {
                case StationAction.Prioritize:
                    if (HasAnotherOwner(_identified[mount].DeviceId, mount))
                        break;
                    _priority = mount;
                    _chargeOnly.Remove(mount);
                    _finished.Remove(mount);
                    foreach (var other in _cancels.Keys.Where(m => m != mount).ToArray())
                        Cancel(other);
                    _actions.Write(ActionLog.Backup,
                        $"отсек {port.Slot + 1} обслуживается первым по просьбе панели");
                    break;

                case StationAction.ChargeOnly:
                    _chargeOnly.Add(mount);
                    if (_priority == mount)
                        _priority = null;
                    Cancel(mount);
                    port.Progress = 0;
                    port.FilesLine = "";
                    port.State = PortState.ChargeOnly;
                    _actions.Write(ActionLog.Backup,
                        $"отсек {port.Slot + 1}: загрузка отменена по просьбе панели");
                    break;

                case StationAction.Resume:
                    _chargeOnly.Remove(mount);
                    _finished.Remove(mount);
                    port.State = PortState.Detected;
                    _actions.Write(ActionLog.Backup,
                        $"отсек {port.Slot + 1}: загрузка возобновлена по просьбе панели");
                    break;
            }
        }
    }

    /// <summary>Сводка по отсекам, как в прототипе станции.</summary>
    private void UpdateSummary(StorageState storage)
    {
        var allPorts = Ports.Concat(_extraPorts.Values).ToArray();
        var copying = allPorts.Count(p => p.State is PortState.Copying or PortState.Scanning
                                       or PortState.Detected);
        var done = allPorts.Count(p => p.State == PortState.Done);
        var failed = allPorts.Count(p => p.State == PortState.Failed);
        var free = Ports.Count(p => p.IsFree);

        Summary = $"копирование {copying} · готово {done} · ошибки {failed} · свободно {free}";

        // Отметка занятости для службы обновления: пока идёт чтение списка
        // или запись, подменять файлы программы нельзя.
        if (copying > 0 || _running.Count > 0 || _identifying.Count > 0 || _mounting.Count > 0)
            BusyMarker.Touch();

        PublishSnapshot(copying, done, failed, free, storage);
    }

    /// <summary>
    /// Кладёт состояние туда, откуда его читает веб-панель. Доска принадлежит
    /// потоку интерфейса, и обращаться к ней из веб-запроса нельзя.
    /// </summary>
    private void PublishSnapshot(int copying, int done, int failed, int free,
        StorageState storage)
    {
        StationSnapshot.Publish(new StationState(
            DateTime.Now,
            StationTitle.Compose(StationTitle.Model, _stationSettings.StationPlace),
            StationTitle.System(),
            Version,
            copying,
            done,
            failed,
            free,
            NetworkUp,
            FtpEnabled,
            FtpLabel,
            storage.Label,
            storage.Total,
            storage.Free,
            _lastTrouble,
            Ports.Select(p => new BaySnapshot(
                p.Slot,
                p.StateText,
                p.CameraId,
                p.Employee,
                p.Department,
                p.FilesLine,
                (int)Math.Round(Math.Clamp(p.Progress, 0, 1) * 100))).ToList()));
    }

    /// <summary>
    /// Проверяет сеть. Задание требует показывать её состояние постоянно:
    /// при выключенной сети отправка на сервер молча копилась бы в очереди.
    /// </summary>
    private void UpdateNetwork()
    {
        try
        {
            var up = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            NetworkUp = up;
            NetworkLabel = up ? "сеть доступна" : "сети нет";
        }
        catch (Exception)
        {
            NetworkUp = false;
            NetworkLabel = "сеть не проверена";
        }
    }


    /// <summary>Показывает заполнение хранилища и краснеет, когда места мало.</summary>
    private void UpdateStorage(StorageState storage)
    {
        // Том архива, а не тот, где лежит программа: оператор смотрит на эту
        // полосу, чтобы понять, куда ещё влезут записи.
        if (!storage.Available || storage.Total <= 0)
        {
            StorageLabel = "хранилище недоступно";
            StorageWidth = 0;
            Trouble("том архива не смонтирован");
            return;
        }

        var used = storage.Total - storage.Free;
        var ratio = (double)used / storage.Total;

        // Тревога по заданию: место кончается или архив недоступен.
        if (storage.Free < _stationSettings.MinFreeBytes)
            Trouble("места в архиве почти нет");
        else if (!NetworkUp && _stationSettings.FtpEnabled)
            Trouble("сети нет, отправка на сервер ждёт");
        else
            _lastTrouble = "";

        StorageLabel = $"хранилище {Size(used)} из {Size(storage.Total)}";
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

    private static string Size(long bytes)
    {
        var tb = bytes / 1024d / 1024 / 1024 / 1024;
        if (tb >= 1)
            return $"{tb.ToString("0.0", Ru)} ТБ";
        return $"{(bytes / 1024d / 1024 / 1024).ToString("0", Ru)} ГБ";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Search.CancelSearch();
        SafeRemovalActive = true;
        _mountStop.Cancel();
        foreach (var mount in _cancels.Keys.ToArray()) Cancel(mount);
        _poll.Stop();
        _clock.Stop();

        _web?.Dispose();

        // Свои монтирования отпускаем при выходе: иначе карта останется
        // смонтированной, и рабочий стол не сможет с ней работать.
        var mounts = _mounted.Values.ToArray();
        _ = Task.WhenAll(_workers.Values.Concat(_mediaTasks)).ContinueWith(_ =>
        {
            foreach (var mount in mounts) _releaseMount(mount);
        });
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

internal sealed record CardRecovery(long DeviceId, string MountPoint, DateTime Until);
