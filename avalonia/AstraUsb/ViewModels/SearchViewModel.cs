using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка результата запроса.</summary>
public sealed partial class FoundFile : ObservableObject
{
    public required ArchiveRow Row { get; init; }

    public required string Camera { get; init; }
    public required string FileName { get; init; }
    public required string Size { get; init; }
    public required string CollectedAt { get; init; }

    /// <summary>Время съёмки или пометка, что часам регистратора доверять нельзя.</summary>
    public required string ShotAt { get; init; }

    public required string Employee { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }

    /// <summary>Отобрана ли строка для действия над несколькими записями.</summary>
    [ObservableProperty] private bool _selected;

    /// <summary>Запись защищена от уборки и от удаления.</summary>
    [ObservableProperty] private bool _important;

    [ObservableProperty] private string _note = "";

    public string Mark => Important ? "защищено" : "";

    partial void OnImportantChanged(bool value) => OnPropertyChanged(nameof(Mark));
}

/// <summary>
/// Вкладка «Запрос данных».
///
/// Опора отбора это время загрузки в станцию: его ставит станция, поэтому оно
/// достоверно. Время съёмки ставит регистратор, и на сбитых часах оно уводит
/// поиск в сторону, поэтому идёт отдельным условием и помечается, когда ему
/// нельзя верить.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly string _dbPath;

    /// <summary>Поколение запроса: ответ прежнего не должен перебить новый.</summary>
    private int _generation;

    public ObservableCollection<FoundFile> Results { get; } = new();
    public ObservableCollection<DepartmentRow> Departments { get; } = new();

    public string[] Kinds { get; } = ["Все", "Видео", "Аудио", "Фото", "Журнал"];

    [ObservableProperty] private string _from = DateTime.Now.AddDays(-7).ToString("dd.MM.yyyy");
    [ObservableProperty] private string _to = DateTime.Now.ToString("dd.MM.yyyy");
    [ObservableProperty] private string _shotFrom = "";
    [ObservableProperty] private string _shotTo = "";
    [ObservableProperty] private string _camera = "";
    [ObservableProperty] private string _personnelNo = "";
    [ObservableProperty] private string _employeeName = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private bool _protectedOnly;
    [ObservableProperty] private DepartmentRow? _department;

    [ObservableProperty] private string _hint = "укажите условия и нажмите «Запрос»";

    /// <summary>
    /// Развёрнуты ли дополнительные условия. На экране станции все условия
    /// сразу занимают больше половины высоты и не оставляют места найденному,
    /// поэтому обычно видны период и род файла, а остальное по требованию.
    /// </summary>
    [ObservableProperty] private bool _filtersExpanded;

    public string FiltersLabel => FiltersExpanded ? "Свернуть условия" : "Ещё условия";

    partial void OnFiltersExpandedChanged(bool value) => OnPropertyChanged(nameof(FiltersLabel));
    [ObservableProperty] private bool _searching;
    [ObservableProperty] private bool _exporting;

    [ObservableProperty] private string _exportTarget = "";
    [ObservableProperty] private string _noteInput = "";
    [ObservableProperty] private FoundFile? _current;

    /// <summary>Формат, в который переводить выбранную запись.</summary>
    [ObservableProperty] private string _format = "";

    /// <summary>Форматы, доступные для выбранной записи.</summary>
    public ObservableCollection<string> Formats { get; } = new();

    /// <summary>Преобразование идёт: второй раз запускать не нужно.</summary>
    [ObservableProperty] private bool _converting;

    /// <summary>Сколько строк отобрано: число выносится в подписи действий.</summary>
    public int SelectedCount => Results.Count(r => r.Selected);

    public string ExportLabel => SelectedCount > 0 ? $"Выгрузить ({SelectedCount})" : "Выгрузить";
    public string DeleteLabel => SelectedCount > 0 ? $"Удалить ({SelectedCount})" : "Удалить";

    public SearchViewModel() : this(AppPaths.Database)
    {
    }

    public SearchViewModel(string dbPath)
    {
        _dbPath = dbPath;
        ReloadDepartments();
    }

    /// <summary>Список отделов для отбора. Пополняется, пока станция работает.</summary>
    public void ReloadDepartments()
    {
        var kept = Department?.Id ?? 0;

        // Выбор снимается до очистки: список, который смотрит на коллекцию,
        // при очистке пытается прочитать выбранный элемент по его прежнему
        // месту и падает с выходом за границы.
        Department = null;
        Departments.Clear();
        Departments.Add(new DepartmentRow { Id = 0, Path = "Все отделы" });

        try
        {
            var staff = new StaffDirectory(_dbPath);
            foreach (var department in staff.Departments())
                Departments.Add(new DepartmentRow
                {
                    Id = department.Id,
                    Path = staff.DepartmentPath(department.Id),
                });
        }
        catch (Exception)
        {
            // Справочник ещё не заведён: отбор по отделу просто недоступен.
        }

        Department = Departments.FirstOrDefault(d => d.Id == kept) ?? Departments[0];
    }

    partial void OnCurrentChanged(FoundFile? value)
    {
        NoteInput = value?.Note ?? "";

        // Форматы зависят от рода записи: видео не переводят в звук.
        Format = "";
        Formats.Clear();
        if (value is not null)
            foreach (var format in MediaTools.FormatsFor(value.Row.Kind))
                Formats.Add(format);

        Format = Formats.FirstOrDefault() ?? "";
    }

    /// <summary>
    /// Открывает запись тем, чем система открывает такие файлы. Своего
    /// проигрывателя у станции нет: он потребовал бы кодеки в сборке.
    /// </summary>
    [RelayCommand]
    private void Play()
    {
        if (Current is not { } file)
        {
            Hint = "выберите запись в списке";
            return;
        }

        var result = MediaTools.Open(file.Path);
        Hint = result.Message;

        if (result.Ok)
            new ActionLog(_dbPath).Write(ActionLog.Export,
                $"просмотр записи {Path.GetFileName(file.Path)}");
    }

    /// <summary>
    /// Переводит запись в другой формат рядом с исходной. Исходная остаётся:
    /// она собрана с регистратора, и рисковать ею ради копии нельзя.
    /// </summary>
    [RelayCommand]
    private async Task ConvertFile()
    {
        if (Converting)
            return;

        if (Current is not { } file)
        {
            Hint = "выберите запись в списке";
            return;
        }

        if (Format.Length == 0)
        {
            Hint = "выберите формат";
            return;
        }

        var path = file.Path;
        var format = Format;

        Converting = true;
        Hint = $"переводим в {format}";

        try
        {
            var result = await Task.Run(() => MediaTools.Convert(path, format));
            Hint = result.Ok
                ? $"готова копия: {result.Message}"
                : result.Message;

            if (result.Ok)
                new ActionLog(_dbPath).Write(ActionLog.Export,
                    $"запись {Path.GetFileName(path)} переведена в {format}");
        }
        catch (Exception e)
        {
            Hint = $"преобразование не удалось: {e.Message}";
        }
        finally
        {
            Converting = false;
        }
    }

    /// <summary>
    /// Ищет записи. Запрос уходит в сторону от интерфейса: по журналу за год он
    /// идёт заметное время, а окно должно оставаться живым. Ответ прежнего
    /// запроса отбрасывается, если оператор успел запросить заново.
    /// </summary>
    [RelayCommand]
    private async Task Search()
    {
        if (!TryDate(From, out var from) || !TryDate(To, out var to))
        {
            Hint = "дата пишется как 02.09.2026";
            return;
        }

        DateTime? shotFrom = TryDate(ShotFrom, out var sf) ? sf.Date : null;
        DateTime? shotTo = TryDate(ShotTo, out var st) ? st.Date.AddDays(1).AddSeconds(-1) : null;

        var filter = new ArchiveFilter
        {
            CollectedFrom = from.Date,
            CollectedTo = to.Date.AddDays(1).AddSeconds(-1),
            ShotFrom = shotFrom,
            ShotTo = shotTo,
            DepartmentId = Department is { Id: > 0 } dep ? dep.Id : null,
            DeviceId = CameraId(),
            PersonnelNo = PersonnelNo.Trim(),
            EmployeeName = EmployeeName.Trim(),
            FileName = FileName.Trim(),
            Kind = (MediaKind)Math.Clamp(KindIndex, 0, 4),
            ProtectedOnly = ProtectedOnly,
        };

        var generation = ++_generation;
        Searching = true;
        Hint = "ищем";

        try
        {
            var found = await Task.Run(() => new ArchiveSearch(_dbPath).Find(filter));

            if (generation != _generation)
                return;

            Current = null;
            Results.Clear();
            foreach (var row in found)
                Results.Add(ToRow(row));

            Refresh();

            Hint = found.Count switch
            {
                0 => "по этим условиям ничего не найдено",
                ArchiveSearch.Limit => $"показаны первые {ArchiveSearch.Limit} записей",
                _ => $"найдено записей: {found.Count}",
            };
        }
        catch (Exception e)
        {
            Hint = $"запрос не удался: {e.Message}";
        }
        finally
        {
            if (generation == _generation)
                Searching = false;
        }
    }

    [RelayCommand]
    private void ToggleFilters() => FiltersExpanded = !FiltersExpanded;

    [RelayCommand]
    private void Reset()
    {
        From = DateTime.Now.AddDays(-7).ToString("dd.MM.yyyy");
        To = DateTime.Now.ToString("dd.MM.yyyy");
        ShotFrom = "";
        ShotTo = "";
        Camera = "";
        PersonnelNo = "";
        EmployeeName = "";
        FileName = "";
        KindIndex = 0;
        ProtectedOnly = false;
        Department = Departments.Count > 0 ? Departments[0] : null;
        Current = null;
        Results.Clear();
        NoteInput = "";
        Current = null;
        Hint = "укажите условия и нажмите «Запрос»";
        Refresh();
    }

    /// <summary>Отбирает или снимает отбор со всех найденных строк.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        var select = SelectedCount < Results.Count;
        foreach (var row in Results)
            row.Selected = select;

        Refresh();
        Hint = select ? $"отобрано записей: {Results.Count}" : "отбор снят";
    }

    /// <summary>Защищает отобранные записи от удаления или снимает защиту.</summary>
    [RelayCommand]
    private void ToggleImportant()
    {
        var rows = Chosen();
        if (rows.Count == 0)
        {
            Hint = "отберите записи галочками";
            return;
        }

        // Если хоть одна не защищена, защищаем всё: так понятнее, чем
        // переключать каждую строку в свою сторону.
        var protect = rows.Any(r => !r.Important);

        try
        {
            var log = new CollectionLog(_dbPath);
            foreach (var row in rows)
            {
                log.SetImportant(row.Path, protect);
                row.Important = protect;
            }

            Hint = protect
                ? $"защищено записей: {rows.Count}"
                : $"снята защита с записей: {rows.Count}";
        }
        catch (Exception e)
        {
            Hint = $"не удалось изменить защиту: {e.Message}";
        }
    }

    [RelayCommand]
    private void SaveNote()
    {
        var rows = Chosen();
        if (rows.Count == 0)
        {
            Hint = "отберите записи галочками";
            return;
        }

        try
        {
            var note = NoteInput.Trim();
            var log = new CollectionLog(_dbPath);
            foreach (var row in rows)
            {
                log.SetNote(row.Path, note);
                row.Note = note;
            }

            Hint = note.Length == 0
                ? $"заметка снята с записей: {rows.Count}"
                : $"заметка сохранена, записей: {rows.Count}";
        }
        catch (Exception e)
        {
            Hint = $"не удалось сохранить заметку: {e.Message}";
        }
    }

    /// <summary>
    /// Удаляет отобранные записи. Защищённые пропускаются, и сколько именно
    /// пропущено, говорится прямо: иначе оператор считал бы, что удалил всё.
    /// </summary>
    [RelayCommand]
    private void Delete()
    {
        var rows = Chosen();
        if (rows.Count == 0)
        {
            Hint = "отберите записи галочками";
            return;
        }

        try
        {
            var result = new ArchiveSearch(_dbPath).Delete(rows.Select(r => r.Row));

            foreach (var row in rows.Where(r => !r.Important).ToArray())
                Results.Remove(row);

            var parts = new List<string> { $"удалено записей: {result.Deleted}" };
            if (result.Skipped > 0)
                parts.Add($"пропущено защищённых: {result.Skipped}");
            if (result.Failed > 0)
                parts.Add($"не удалось удалить: {result.Failed}");

            Hint = string.Join(", ", parts);
            Refresh();

            new ActionLog(_dbPath).Write(ActionLog.Cleanup,
                $"удалено записей: {result.Deleted}, пропущено защищённых: {result.Skipped}");
        }
        catch (Exception e)
        {
            Hint = $"удаление не удалось: {e.Message}";
        }
    }

    /// <summary>Выгружает отобранное наружу: на флешку или в сетевую папку.</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (Exporting)
            return;

        var rows = Chosen();
        if (rows.Count == 0)
        {
            Hint = "отберите записи галочками";
            return;
        }

        var target = ExportTarget.Trim();
        if (target.Length == 0)
        {
            Hint = "укажите, куда выгружать";
            return;
        }

        if (!Directory.Exists(target))
        {
            Hint = $"папка «{target}» недоступна, проверьте носитель";
            return;
        }

        var paths = rows.Select(r => r.Path).ToList();
        Exporting = true;

        try
        {
            var result = await Task.Run(() => FileExporter.Export(
                paths, target, DateTime.Now,
                (done, total) => Dispatcher.UIThread.Post(
                    () => Hint = $"выгружено {done} из {total}")));

            var parts = new List<string>
            {
                $"выгружено {Numerals.Plural(result.Copied, "файл", "файла", "файлов")}",
            };
            if (result.Missing > 0)
                parts.Add($"не нашлось в архиве: {result.Missing}");
            if (result.Failed > 0)
                parts.Add($"не скопировалось: {result.Failed}");

            Hint = string.Join(", ", parts);
            new ActionLog(_dbPath).Write(ActionLog.Export,
                $"выгружено {Numerals.Plural(result.Copied, "файл", "файла", "файлов")} в {target}");
        }
        catch (Exception e)
        {
            Hint = $"выгрузка не удалась: {e.Message}";
        }
        finally
        {
            Exporting = false;
        }
    }

    /// <summary>Обновляет счётчики отбора в подписях действий.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ExportLabel));
        OnPropertyChanged(nameof(DeleteLabel));
    }

    /// <summary>Отобранные строки, а если галочек нет, то выделенная в списке.</summary>
    private List<FoundFile> Chosen()
    {
        var selected = Results.Where(r => r.Selected).ToList();
        if (selected.Count > 0)
            return selected;

        return Current is { } single ? [single] : [];
    }

    private static FoundFile ToRow(ArchiveRow row) => new()
    {
        Row = row,
        Camera = row.CameraName,
        FileName = Path.GetFileName(row.File.DestPath),
        Size = Size(row.File.SizeBytes),
        CollectedAt = row.File.CollectedAt.ToString("dd.MM.yy HH:mm"),
        ShotAt = row.File.ShotAt is null
            ? "неизвестно"
            : row.File.ShotAtTrusted
                ? row.File.ShotAt.Value.ToString("dd.MM.yy HH:mm")
                : "часы сбиты",
        Employee = row.EmployeeName.Length > 0 ? row.EmployeeName : row.PersonnelNo,
        Kind = MediaKinds.Name(row.Kind),
        Path = row.File.DestPath,
        Important = row.File.Important,
        Note = row.File.Note,
    };

    /// <summary>Камера по номеру или имени, если оператор её указал.</summary>
    private long? CameraId()
    {
        var wanted = Camera.Trim();
        if (wanted.Length == 0)
            return null;

        try
        {
            using var registry = new DeviceRegistry(_dbPath);
            var match = registry.ListDevices().FirstOrDefault(d =>
                d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || d.FirmwareId.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || d.Serial.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || d.Id.ToString() == wanted);

            return match?.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), ["dd.MM.yyyy", "dd.MM.yy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string Size(long bytes)
    {
        var mb = bytes / 1024d / 1024;
        return mb >= 1024 ? $"{mb / 1024:0.0} ГБ"
            : mb >= 1 ? $"{mb:0.0} МБ"
            : $"{bytes / 1024d:0} КБ";
    }
}
