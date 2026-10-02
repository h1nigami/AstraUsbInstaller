using System.Collections.ObjectModel;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>
/// Вкладка «Настройки»: правила хранилища и справочник
/// сотрудников. На экране 1024x600 всё это не помещается в один столбец,
/// поэтому раскладывается в две колонки.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _dbPath;
    private readonly ActionLog _actions;
    private Settings _settings;
    public Func<int, bool>? CanChangeBayCount { get; set; }
    public event Action<int, int>? BaysChanged;

    public string[] StorageModes { get; } = ["предупреждать", "перезаписывать старые"];

    [ObservableProperty] private string _backupRoot = "";
    [ObservableProperty] private int _minFreeGb;
    [ObservableProperty] private int _stationNumber;
    [ObservableProperty] private int _storageModeIndex;
    [ObservableProperty] private bool _deleteVideoAfterCopy;
    [ObservableProperty] private int _keepDays;
    [ObservableProperty] private int _bayCount;
    [ObservableProperty] private int _baysPerRow;

    /// <summary>Число окон изменено: доска строится один раз при запуске.</summary>
    [ObservableProperty] private bool _restartNeeded;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _hint = "";

    /// <summary>Нажата кнопка заводского сброса, ждём подтверждения.</summary>
    [ObservableProperty] private bool _resetArmed;

    [ObservableProperty] private bool _resetRunning;

    /// <summary>
    /// Открытый подраздел настроек. Разделы разведены по боковому меню, как в
    /// прототипе: одной простынёй с прокруткой на экране станции не
    /// пользоваться, а искать в ней нужное поле.
    /// </summary>
    [ObservableProperty] private string _section = "station";

    public bool IsStationSection => Section == "station";
    public bool IsAccessSection => Section == "access";
    public bool IsFtpSection => Section == "ftp";
    public bool IsSqlSection => Section == "sql";
    public bool IsWebSection => Section == "web";
    public bool IsAboutSection => Section == "about";

    [RelayCommand]
    private void OpenSection(string? name) => Section = name switch
    {
        "access" => "access",
        "ftp" => "ftp",
        "sql" => "sql",
        "web" => "web",
        "about" => "about",
        _ => "station",
    };

    partial void OnSectionChanged(string value)
    {
        Hint = "";
        OnPropertyChanged(nameof(IsStationSection));
        OnPropertyChanged(nameof(IsAccessSection));
        OnPropertyChanged(nameof(IsFtpSection));
        OnPropertyChanged(nameof(IsSqlSection));
        OnPropertyChanged(nameof(IsWebSection));
        OnPropertyChanged(nameof(IsAboutSection));

        if (Section == "ftp")
            ShowQueueState();

        if (Section == "web")
        {
            if (WebEnabled && WebSsl)
                WebFingerprint = PanelCertificate.Fingerprint();

            ShowWebLinks();
        }
    }

    /// <summary>Название точки: идёт в шапку веб-панели рядом с моделью.</summary>
    [ObservableProperty] private string _stationPlace = "";

    [ObservableProperty] private int _lockTimeoutMinutes;

    /// <summary>Пароль остался таким, каким станция пришла с завода.</summary>
    [ObservableProperty] private bool _usingDefaultPassword;

    /// <summary>
    /// Архив лежит на системном разделе. Задание это запрещает: системный диск
    /// станции невелик, и записи его переполнят.
    /// </summary>
    [ObservableProperty] private bool _archiveOnSystemDrive;
    [ObservableProperty] private bool _alarmSound;
    [ObservableProperty] private bool _voiceHints;

    [ObservableProperty] private bool _webEnabled;
    [ObservableProperty] private int _webPort = 8080;
    [ObservableProperty] private bool _webSsl;
    [ObservableProperty] private string _webFingerprint = "";
    [ObservableProperty] private string _webState = "";

    [ObservableProperty] private bool _sqlEnabled;
    [ObservableProperty] private int _sqlKindIndex;
    [ObservableProperty] private string _sqlHost = "";
    [ObservableProperty] private int _sqlPort = 3306;
    [ObservableProperty] private string _sqlDatabase = "";
    [ObservableProperty] private string _sqlUser = "";
    [ObservableProperty] private string _sqlPassword = "";
    [ObservableProperty] private string _sqlState = "";
    [ObservableProperty] private bool _sqlTesting;

    public string[] SqlKinds { get; } = ["MySQL"];
    private string SelectedSqlKind => SqlKindIndex == 0 ? SqlKinds[0] : _settings.SqlKind;
    [ObservableProperty] private bool _ftpEnabled;
    [ObservableProperty] private string _ftpHost = "";
    [ObservableProperty] private int _ftpPort = 21;
    [ObservableProperty] private string _ftpUser = "";
    [ObservableProperty] private string _ftpPassword = "";
    [ObservableProperty] private string _ftpFolder = "";
    [ObservableProperty] private bool _ftpSsl;
    [ObservableProperty] private string _ftpState = "";
    [ObservableProperty] private bool _ftpTesting;

    [ObservableProperty] private string _adminAccount = "";
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _repeatPassword = "";


    public SettingsViewModel() : this(AppPaths.Database)
    {
    }

    public SettingsViewModel(string dbPath)
    {
        _dbPath = dbPath;
        _actions = new ActionLog(dbPath);
        _settings = Settings.Load();

        BackupRoot = _settings.BackupRoot;
        MinFreeGb = _settings.MinFreeGb;
        StationNumber = _settings.StationNumber;
        StationPlace = _settings.StationPlace;
        StorageModeIndex = _settings.StorageMode == StorageMode.Overwrite ? 1 : 0;
        DeleteVideoAfterCopy = _settings.DeleteVideoAfterCopy;
        KeepDays = _settings.KeepDays;
        BayCount = Math.Clamp(_settings.BayCount, 6, 30);
        BaysPerRow = Math.Clamp(_settings.BaysPerRow, 2, 6);
        LockTimeoutMinutes = _settings.LockTimeoutMinutes;
        UsingDefaultPassword = string.IsNullOrEmpty(_settings.PasswordHash);
        AdminAccount = string.IsNullOrWhiteSpace(_settings.AdminAccount)
            ? PasswordGate.DefaultAccount
            : _settings.AdminAccount;
        ArchiveOnSystemDrive = ArchiveGuard.OnSystemDrive(_settings.BackupRoot);

        AlarmSound = _settings.AlarmSound;
        VoiceHints = _settings.VoiceHints;

        WebEnabled = _settings.WebEnabled;
        WebPort = _settings.WebPort;
        WebSsl = _settings.WebSsl;
        SqlEnabled = _settings.SqlEnabled;
        SqlKindIndex = Array.IndexOf(SqlKinds, _settings.SqlKind);
        SqlState = ExternalDatabase.ProviderError(_settings.SqlKind) ?? "";
        SqlHost = _settings.SqlHost;
        SqlPort = _settings.SqlPort;
        SqlDatabase = _settings.SqlDatabase;
        SqlUser = _settings.SqlUser;
        SqlPassword = _settings.SqlPassword;
        FtpEnabled = _settings.FtpEnabled;
        FtpHost = _settings.FtpHost;
        FtpPort = _settings.FtpPort;
        FtpUser = _settings.FtpUser;
        FtpPassword = _settings.FtpPassword;
        FtpFolder = _settings.FtpFolder;
        FtpSsl = _settings.FtpSsl;

        if (_settings.Unreadable)
            Hint = "файл настроек испорчен: выгрузка остановлена. Выберите папку архива и сохраните хранилище";
    }

    [RelayCommand]
    private void SaveStorage()
    {
        var root = BackupRoot.Trim();
        if (!_settings.SelectBackupRoot(root))
        {
            Hint = "не удалось выбрать папку архива. Проверьте путь, подключение диска и права на запись";
            return;
        }

        _settings.MinFreeGb = Math.Max(1, MinFreeGb);
        _settings.StationNumber = Math.Clamp(StationNumber, 0, 99);
        _settings.StationPlace = StationPlace.Trim();
        StationPlace = _settings.StationPlace;
        _settings.StorageMode = StorageModeIndex == 1 ? StorageMode.Overwrite : StorageMode.Warn;
        _settings.DeleteVideoAfterCopy = DeleteVideoAfterCopy;
        _settings.KeepDays = Math.Clamp(KeepDays, 0, 3650);
        KeepDays = _settings.KeepDays;
        _settings.AlarmSound = AlarmSound;
        _settings.VoiceHints = VoiceHints;

        var stored = _settings.Save(replaceUnreadable: true);
        if (stored)
            _actions.Write(ActionLog.Settings,
                $"хранилище: {BackupRoot}, порог {MinFreeGb} ГБ, "
                + $"срок хранения {(KeepDays == 0 ? "бессрочно" : KeepDays + " дн")}, "
                + $"станция {StationNumber}");

        ArchiveOnSystemDrive = ArchiveGuard.OnSystemDrive(_settings.BackupRoot);

        Hint = !stored
            ? "не удалось записать настройки, проверьте права на папку data"
            : KeepDays == 0
                ? "настройки сохранены, записи хранятся бессрочно"
                : $"настройки сохранены, записи хранятся {KeepDays} дн";
    }

    /// <summary>
    /// Адреса, по которым станция отвечает панелью. Оператору взять их
    /// больше негде: панель живёт внутри программы, а адрес у станции тот,
    /// что ей выдала сеть.
    /// </summary>
    public ObservableCollection<string> WebLinks { get; } = new();

    /// <summary>Есть ли что показать в списке адресов.</summary>
    public bool HasWebLinks => WebLinks.Count > 0;

    /// <summary>
    /// Пересобирает список адресов. Вызывается и по таймеру, пока раздел
    /// открыт: адрес станции выдаёт сеть объекта, и он меняется без спроса.
    /// Список переписывается только когда он и правда стал другим, иначе
    /// строки мигали бы на каждом тике.
    /// </summary>
    public void ShowWebLinks()
    {
        var port = WebPort is > 0 and < 65536 ? WebPort : 8080;
        var links = WebAddress.Links(port, WebSsl);

        if (links.SequenceEqual(WebLinks))
            return;

        WebLinks.Clear();
        foreach (var link in links)
            WebLinks.Add(link);

        OnPropertyChanged(nameof(HasWebLinks));
    }

    /// <summary>
    /// Сохраняет параметры веб-панели. Порт нельзя переоткрыть на ходу,
    /// поэтому включение вступает в силу после перезапуска программы.
    /// </summary>
    [RelayCommand]
    private void SaveWeb()
    {
        var wanted = WebPort is > 0 and < 65536 ? WebPort : 8080;
        var changed = _settings.WebEnabled != WebEnabled
                      || _settings.WebPort != wanted
                      || _settings.WebSsl != WebSsl;

        _settings.WebEnabled = WebEnabled;
        _settings.WebPort = wanted;
        _settings.WebSsl = WebSsl;
        WebPort = wanted;

        if (!_settings.Save())
        {
            Hint = "не удалось записать настройки, проверьте права на папку data";
            return;
        }

        if (changed)
        {
            RestartNeeded = true;
            _actions.Write(ActionLog.Settings, WebEnabled
                ? $"веб-панель включена на порту {wanted}"
                : "веб-панель выключена");
        }

        ShowWebLinks();

        WebState = WebEnabled
            ? "после перезапуска панель откроется по адресам ниже"
            : "панель выключена";

        WebFingerprint = WebEnabled && WebSsl ? PanelCertificate.Fingerprint() : "";

        Hint = "параметры панели сохранены";
    }

    /// <summary>Сохраняет параметры внешнего сервера базы.</summary>
    [RelayCommand]
    private void SaveSql()
    {
        if (ExternalDatabase.ProviderError(SelectedSqlKind) is { } error)
        {
            SqlState = error;
            return;
        }

        _settings.SqlEnabled = SqlEnabled;
        _settings.SqlKind = SelectedSqlKind;
        _settings.SqlHost = SqlHost.Trim();
        _settings.SqlPort = SqlPort is > 0 and < 65536
            ? SqlPort
            : SqlProbe.DefaultPort(_settings.SqlKind);
        _settings.SqlDatabase = SqlDatabase.Trim();
        _settings.SqlUser = SqlUser.Trim();
        _settings.SqlPassword = SqlPassword;
        SqlPort = _settings.SqlPort;

        if (!_settings.Save())
        {
            Hint = "не удалось записать настройки, проверьте права на папку data";
            return;
        }

        _actions.Write(ActionLog.Settings, SqlEnabled
            ? $"внешняя база включена: {_settings.SqlKind} {_settings.SqlHost}:{_settings.SqlPort}"
            : "внешняя база выключена");

        Hint = "параметры внешней базы сохранены";
    }

    /// <summary>
    /// Проверяет базу: сначала отвечает ли сервер на порту, потом принимает ли
    /// он учётную запись и хватает ли прав на таблицу учёта. Узнать о нехватке
    /// прав лучше при настройке, чем в первую же смену.
    /// </summary>
    [RelayCommand]
    private async Task TestSql()
    {
        if (SqlTesting)
            return;

        if (ExternalDatabase.ProviderError(SelectedSqlKind) is { } error)
        {
            SqlState = error;
            return;
        }

        SqlTesting = true;
        SqlState = "проверяем";

        try
        {
            var reachable = await SqlProbe.CheckAsync(SqlHost, SqlPort, TimeSpan.FromSeconds(5));
            if (!reachable.StartsWith("сервер отвечает", StringComparison.Ordinal))
            {
                SqlState = reachable;
                return;
            }

            var probe = new Settings
            {
                SqlKind = SelectedSqlKind,
                SqlHost = SqlHost.Trim(),
                SqlPort = SqlPort,
                SqlDatabase = SqlDatabase.Trim(),
                SqlUser = SqlUser.Trim(),
                SqlPassword = SqlPassword,
            };

            var result = await new ExternalDatabase(probe).CheckAsync();
            SqlState = result.Ok
                ? $"{result.Message}, таблица учёта готова"
                : result.Message;
        }
        catch (Exception e)
        {
            SqlState = UserError.Report("Не удалось проверить подключение к базе данных", e);
        }
        finally
        {
            SqlTesting = false;
        }
    }

    /// <summary>
    /// Отправляет во внешнюю базу сведения о собранном за последний месяц.
    /// Локальный журнал остаётся источником истины: наружу уходит отражение.
    /// </summary>
    [RelayCommand]
    private async Task SyncSql()
    {
        if (SqlTesting)
            return;

        SqlTesting = true;
        SqlState = "отправляем";

        try
        {
            var files = new CollectionLog(_dbPath)
                .CollectedBetween(DateTime.Now.AddDays(-30), DateTime.Now);

            var station = $"BC-{Math.Clamp(_settings.StationNumber, 0, 99):00}";
            var result = await new ExternalDatabase(_settings).SendAsync(files, station);

            SqlState = result.Message;
            if (result.Ok && result.Sent > 0)
                _actions.Write(ActionLog.Export,
                    $"во внешнюю базу отправлено записей: {result.Sent}");
        }
        catch (Exception e)
        {
            SqlState = UserError.Report("Не удалось отправить записи в базу данных", e);
        }
        finally
        {
            SqlTesting = false;
        }
    }

    /// <summary>Сохраняет параметры отправки на сервер.</summary>

    /// <summary>Подставляет обычный порт выбранного сервера.</summary>
    partial void OnSqlKindIndexChanged(int value)
    {
        var kind = SqlKinds[Math.Clamp(value, 0, SqlKinds.Length - 1)];
        SqlPort = SqlProbe.DefaultPort(kind);
        SqlState = ExternalDatabase.ProviderError(SelectedSqlKind) ?? "";
    }

    /// <summary>Сохраняет параметры отправки на сервер.</summary>
    [RelayCommand]
    private void SaveFtp()
    {
        _settings.FtpEnabled = FtpEnabled;
        _settings.FtpHost = FtpHost.Trim();
        _settings.FtpPort = FtpPort is > 0 and < 65536 ? FtpPort : 21;
        _settings.FtpUser = FtpUser.Trim();
        _settings.FtpPassword = FtpPassword;
        _settings.FtpFolder = FtpFolder.Trim();
        _settings.FtpSsl = FtpSsl;
        FtpPort = _settings.FtpPort;

        if (!_settings.Save())
        {
            Hint = "не удалось записать настройки, проверьте права на папку data";
            return;
        }

        _actions.Write(ActionLog.Settings, FtpEnabled
            ? $"отправка на сервер включена: {_settings.FtpHost}:{_settings.FtpPort}"
            : "отправка на сервер выключена");

        Hint = FtpEnabled
            ? "параметры сохранены, отправка включена"
            : "параметры сохранены, отправка выключена";
    }

    /// <summary>
    /// Проверяет подключение к серверу. Проверка идёт в стороне от интерфейса:
    /// мёртвый адрес отвечает не сразу, а окно должно оставаться живым.
    /// </summary>
    [RelayCommand]
    private async Task TestFtp()
    {
        if (FtpTesting)
            return;

        var probe = new Settings
        {
            FtpHost = FtpHost.Trim(),
            FtpPort = FtpPort,
            FtpUser = FtpUser.Trim(),
            FtpPassword = FtpPassword,
            FtpFolder = FtpFolder.Trim(),
            FtpSsl = FtpSsl,
        };

        FtpTesting = true;
        FtpState = "проверяем";

        try
        {
            var result = await Task.Run(() => FtpSender.Test(probe));
            FtpState = result.Message;
        }
        catch (Exception e)
        {
            FtpState = UserError.Report("Не удалось проверить подключение к FTP", e);
        }
        finally
        {
            FtpTesting = false;
        }
    }

    /// <summary>Возвращает в работу файлы, отложенные после неудачных попыток.</summary>
    [RelayCommand]
    private void RetryFtp()
    {
        try
        {
            var queue = new FtpQueue(_dbPath);
            var revived = queue.Retry();
            FtpState = revived > 0
                ? $"возвращено в очередь: {revived}, всего ждёт {queue.Count()}"
                : $"отложенных нет, в очереди {queue.Count()}";
        }
        catch (Exception e)
        {
            FtpState = UserError.Report("Не удалось повторить отправку файлов", e);
        }
    }

    /// <summary>Показывает, сколько файлов ждёт отправки.</summary>
    private void ShowQueueState()
    {
        try
        {
            var queue = new FtpQueue(_dbPath);
            var stuck = queue.StuckCount();
            FtpState = stuck > 0
                ? $"в очереди {queue.Count()}, отложено после неудач {stuck}"
                : $"в очереди {queue.Count()}";
        }
        catch (Exception e)
        {
            FtpState = UserError.Report("Не удалось прочитать очередь отправки", e);
        }
    }

    /// <summary>
    /// Меняет пароль станции. Текущий спрашиваем прежде нового: станция стоит
    /// открытой, и без этой проверки пароль сменил бы любой, кто дошёл до
    /// раздела.
    /// </summary>
    [RelayCommand]
    private void ChangePassword()
    {
        if (!PasswordGate.Matches(_settings.PasswordHash, CurrentPassword))
        {
            Hint = "текущий пароль не подошёл";
            return;
        }

        if (NewPassword.Length < 4)
        {
            Hint = "новый пароль короче четырёх знаков";
            return;
        }

        if (string.IsNullOrWhiteSpace(AdminAccount))
        {
            Hint = "укажите имя учётной записи";
            return;
        }

        if (NewPassword != RepeatPassword)
        {
            Hint = "новый пароль и повтор не совпали";
            return;
        }

        var account = _settings.AdminAccount;
        var password = _settings.PasswordHash;
        _settings.AdminAccount = AdminAccount.Trim();
        _settings.PasswordHash = PasswordGate.Hash(NewPassword);
        var saved = _settings.Save();
        if (!saved)
        {
            _settings.AdminAccount = account;
            _settings.PasswordHash = password;
        }
        UsingDefaultPassword = string.IsNullOrEmpty(_settings.PasswordHash);
        if (saved)
            _actions.Write(ActionLog.Settings,
                $"учётная запись администратора изменена: {_settings.AdminAccount}");
        Hint = saved
            ? "пароль изменён"
            : "не удалось записать настройки, проверьте права на папку data";

        CurrentPassword = "";
        NewPassword = "";
        RepeatPassword = "";
    }

    /// <summary>Применяет число окон, если уменьшение не скроет занятые.</summary>
    [RelayCommand]
    private void SaveBayCount()
    {
        var wanted = Math.Clamp(BayCount, 6, 30);
        var perRow = Math.Clamp(BaysPerRow, 2, 6);
        var changed = wanted != _settings.BayCount || perRow != _settings.BaysPerRow;

        if (CanChangeBayCount?.Invoke(wanted) == false)
        {
            Hint = "нельзя скрыть занятые окна: сначала освободите их";
            return;
        }

        _settings.BayCount = wanted;
        _settings.BaysPerRow = perRow;
        BayCount = wanted;
        BaysPerRow = perRow;

        if (!_settings.Save())
        {
            Hint = "не удалось записать настройки, проверьте права на папку data";
            return;
        }

        if (changed)
        {
            _actions.Write(ActionLog.Settings, $"число окон сбора изменено на {wanted}");
        }
        BaysChanged?.Invoke(wanted, perRow);
        Hint = $"окон сбора: {wanted}";
    }

    /// <summary>Сохраняет время, после которого открытый раздел закрывается сам.</summary>
    [RelayCommand]
    private void SaveLockTimeout()
    {
        _settings.LockTimeoutMinutes = Math.Clamp(LockTimeoutMinutes, 0, 240);
        LockTimeoutMinutes = _settings.LockTimeoutMinutes;

        var stored = _settings.Save();
        if (stored)
            _actions.Write(ActionLog.Settings, LockTimeoutMinutes == 0
                ? "разделы больше не закрываются по простою"
                : $"разделы закрываются после {LockTimeoutMinutes} мин простоя");

        Hint = !stored
            ? "не удалось записать настройки, проверьте права на папку data"
            : LockTimeoutMinutes == 0
                ? "разделы больше не закрываются по простою"
                : $"разделы закроются после {LockTimeoutMinutes} мин простоя";
    }

    // --- Обновления ---------------------------------------------------------

    [ObservableProperty] private bool _updateChecking;

    /// <summary>
    /// Запускает внешнюю службу проверки обновлений. Сам киоск не ставит:
    /// установщик в конце перезапускает его службу.
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (UpdateChecking)
            return;
        if (BusyMarker.Busy())
        {
            Hint = "дождитесь конца сканирования или копирования";
            return;
        }

        UpdateChecking = true;
        Hint = "запуск проверки…";
        try
        {
            Hint = await Task.Run(() => Updater.StartService());
        }
        finally
        {
            UpdateChecking = false;
        }
    }

    // --- Заводской сброс ----------------------------------------------------

    [RelayCommand]
    private void ArmFactoryReset()
    {
        if (BusyMarker.Busy())
        {
            Hint = "дождитесь конца сканирования или копирования и повторите сброс";
            return;
        }
        ResetArmed = true;
    }

    [RelayCommand]
    private void CancelFactoryReset() => ResetArmed = false;

    [RelayCommand]
    private async Task RunFactoryReset()
    {
        if (ResetRunning)
            return;
        ResetArmed = false;
        ResetRunning = true;
        Hint = "сброс...";
        try
        {
            var root = _settings.BackupRoot;
            var result = await Task.Run(() => FactoryReset.Run(_dbPath, root));
            _actions.Write(ActionLog.Settings,
                $"заводской сброс: устройств {result.Devices}, выгрузок {result.Backups}, "
                + $"записей архива {result.Entries}");
            Hint = $"удалено: устройств {result.Devices}, выгрузок {result.Backups}, "
                   + $"записей архива {result.Entries}. Программа перезапускается";
            await Task.Delay(TimeSpan.FromSeconds(2));
            AppRestart.Now();
        }
        catch (Exception e)
        {
            ResetRunning = false;
            Hint = e is InvalidOperationException
                ? e.Message
                : UserError.Report("Не удалось выполнить заводской сброс", e);
        }
    }
}
