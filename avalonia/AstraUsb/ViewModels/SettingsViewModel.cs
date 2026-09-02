using System.Collections.ObjectModel;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка разметки гнёзд.</summary>
public sealed partial class SlotRow : ObservableObject
{
    [ObservableProperty] private int _slot;
    [ObservableProperty] private string _portPath = "";

    public string SlotLabel => $"окно {Slot + 1}";
    public string PortLabel => string.IsNullOrEmpty(PortPath) ? "не размечено" : PortPath;
}

/// <summary>
/// Вкладка «Настройки»: правила хранилища, разметка гнёзд, справочник
/// сотрудников. На экране 1024x600 всё это не помещается в один столбец,
/// поэтому раскладывается в две колонки.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _dbPath;
    private readonly PortMap _portMap;
    private readonly ActionLog _actions;
    private Settings _settings;

    public ObservableCollection<SlotRow> Slots { get; } = new();

    public string[] StorageModes { get; } = ["предупреждать", "перезаписывать старые"];

    [ObservableProperty] private string _backupRoot = "";
    [ObservableProperty] private int _minFreeGb;
    [ObservableProperty] private int _stationNumber;
    [ObservableProperty] private int _storageModeIndex;
    [ObservableProperty] private bool _deleteVideoAfterCopy;
    [ObservableProperty] private int _keepDays;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _hint = "";

    [ObservableProperty] private int _lockTimeoutMinutes;

    /// <summary>Пароль остался таким, каким станция пришла с завода.</summary>
    [ObservableProperty] private bool _usingDefaultPassword;
    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _repeatPassword = "";


    public SettingsViewModel() : this(AppPaths.Database)
    {
    }

    public SettingsViewModel(string dbPath)
    {
        _dbPath = dbPath;
        _portMap = new PortMap(dbPath);
        _actions = new ActionLog(dbPath);
        _settings = Settings.Load();

        BackupRoot = _settings.BackupRoot;
        MinFreeGb = _settings.MinFreeGb;
        StationNumber = _settings.StationNumber;
        StorageModeIndex = _settings.StorageMode == StorageMode.Overwrite ? 1 : 0;
        DeleteVideoAfterCopy = _settings.DeleteVideoAfterCopy;
        KeepDays = _settings.KeepDays;
        LockTimeoutMinutes = _settings.LockTimeoutMinutes;
        UsingDefaultPassword = string.IsNullOrEmpty(_settings.PasswordHash);

        ReloadSlots();
    }

    [RelayCommand]
    private void SaveStorage()
    {
        _settings.BackupRoot = BackupRoot.Trim();
        _settings.MinFreeGb = Math.Max(1, MinFreeGb);
        _settings.StationNumber = Math.Clamp(StationNumber, 0, 99);
        _settings.StorageMode = StorageModeIndex == 1 ? StorageMode.Overwrite : StorageMode.Warn;
        _settings.DeleteVideoAfterCopy = DeleteVideoAfterCopy;
        _settings.KeepDays = Math.Clamp(KeepDays, 0, 3650);
        KeepDays = _settings.KeepDays;

        var stored = _settings.Save();
        if (stored)
            _actions.Write(ActionLog.Settings,
                $"хранилище: {BackupRoot}, порог {MinFreeGb} ГБ, "
                + $"срок хранения {(KeepDays == 0 ? "бессрочно" : KeepDays + " дн")}, "
                + $"станция {StationNumber}");

        Hint = !stored
            ? "не удалось записать настройки, проверьте права на папку data"
            : KeepDays == 0
                ? "настройки сохранены, записи хранятся бессрочно"
                : $"настройки сохранены, записи хранятся {KeepDays} дн";
    }

    // --- Разметка гнёзд -----------------------------------------------------

    [RelayCommand]
    public void ReloadSlots()
    {
        Slots.Clear();
        var assigned = _portMap.All();

        for (var slot = 0; slot < MainWindowViewModel.PortCount; slot++)
        {
            var port = assigned.FirstOrDefault(pair => pair.Value == slot).Key ?? "";
            Slots.Add(new SlotRow { Slot = slot, PortPath = port });
        }
    }

    /// <summary>
    /// Закрепляет за выбранным окном то гнездо, в котором сейчас стоит
    /// единственная подключённая камера. Так размечают станцию по инструкции:
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

        if (NewPassword != RepeatPassword)
        {
            Hint = "новый пароль и повтор не совпали";
            return;
        }

        _settings.PasswordHash = PasswordGate.Hash(NewPassword);
        var saved = _settings.Save();
        UsingDefaultPassword = !saved;
        if (saved)
            _actions.Write(ActionLog.Settings, "пароль станции изменён");
        Hint = saved
            ? "пароль изменён"
            : "не удалось записать настройки, проверьте права на папку data";

        CurrentPassword = "";
        NewPassword = "";
        RepeatPassword = "";
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

    /// втыкают камеру в отсек и нажимают «сопоставить».
    /// </summary>
    [RelayCommand]
    private void MapSlot(SlotRow? row)
    {
        if (row is null)
            return;

        var connected = UsbWatcher.List()
            .Where(d => !string.IsNullOrEmpty(d.PortPath))
            .ToArray();

        if (connected.Length == 0)
        {
            Hint = "вставьте камеру в размечаемый отсек";
            return;
        }

        if (connected.Length > 1)
        {
            Hint = "для разметки оставьте подключённой одну камеру";
            return;
        }

        _portMap.Assign(connected[0].PortPath!, row.Slot);
        Hint = $"окно {row.Slot + 1} закреплено за гнездом {connected[0].PortPath}";
        ReloadSlots();
    }

    [RelayCommand]
    private void ClearSlots()
    {
        _portMap.Clear();
        Hint = "разметка снята, окна снова занимаются по порядку подключения";
        ReloadSlots();
    }
}
