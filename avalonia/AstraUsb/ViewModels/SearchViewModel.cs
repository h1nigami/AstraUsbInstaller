using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using Avalonia.Media.Imaging;
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

    /// <summary>Открыт ли просмотр записи поверх экрана.</summary>
    [ObservableProperty] private bool _viewerVisible;

    [ObservableProperty] private string _viewerTitle = "";
    [ObservableProperty] private string _viewerNote = "";

    /// <summary>Снимок для показа: фото станция умеет показывать сама.</summary>
    [ObservableProperty] private Bitmap? _viewerImage;

    /// <summary>Текст журнала регистратора: он тоже читается на месте.</summary>
    [ObservableProperty] private string _viewerText = "";

    /// <summary>Длительность открытой записи в секундах: ноль значит не видео.</summary>
    [ObservableProperty] private double _viewerLength;

    /// <summary>Где стоит ползунок времени, в секундах.</summary>
    [ObservableProperty] private double _viewerPosition;

    public bool ViewerHasImage => ViewerImage is not null;
    public bool ViewerHasText => ViewerText.Length > 0;
    public bool ViewerHasVideo => ViewerLength > 0;

    /// <summary>Подпись под шкалой: где стоим и сколько всего.</summary>
    public string ViewerTimeLabel =>
        $"{VideoPreview.Label(TimeSpan.FromSeconds(ViewerPosition))} "
        + $"из {VideoPreview.Label(TimeSpan.FromSeconds(ViewerLength))}";

    private string _videoPath = "";

    // Ползунок тянут, и запросов кадра выходит больше, чем ffmpeg успевает
    // выполнить. Показывается кадр только последнего запроса.
    private int _frameRequest;
    private int _openRequest;

    partial void OnViewerImageChanged(Bitmap? value) => OnPropertyChanged(nameof(ViewerHasImage));
    partial void OnViewerTextChanged(string value) => OnPropertyChanged(nameof(ViewerHasText));

    partial void OnViewerLengthChanged(double value)
    {
        OnPropertyChanged(nameof(ViewerHasVideo));
        OnPropertyChanged(nameof(ViewerTimeLabel));
    }

    partial void OnViewerPositionChanged(double value)
    {
        OnPropertyChanged(nameof(ViewerTimeLabel));
        ShowFrame(TimeSpan.FromSeconds(value));
    }

    /// <summary>Шаг по записи: искать нужный момент ползунком неудобно.</summary>
    [RelayCommand]
    private void StepBack() => ViewerPosition = Math.Max(0, ViewerPosition - 10);

    [RelayCommand]
    private void StepForward() => ViewerPosition = Math.Min(ViewerLength, ViewerPosition + 10);

    /// <summary>Достаёт кадр записи и показывает его, если он ещё нужен.</summary>
    private void ShowFrame(TimeSpan at)
    {
        if (_videoPath.Length == 0)
            return;

        var mine = ++_frameRequest;
        var path = _videoPath;

        // За последним кадром в записи пусто, а конец шкалы это ровно
        // длительность: без отступа кадр в конце не выходил вовсе.
        if (ViewerLength > 0.3 && at.TotalSeconds > ViewerLength - 0.3)
            at = TimeSpan.FromSeconds(ViewerLength - 0.3);

        Task.Run(() =>
        {
            var frame = VideoPreview.Frame(path, at);
            if (frame is null || mine != _frameRequest)
                return;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(frame);
            }
            catch (IOException)
            {
                // Кадр перезаписывается следующим запросом: значит он и не нужен.
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (mine != _frameRequest)
                    return;

                try
                {
                    using var stream = new MemoryStream(bytes);
                    var next = new Bitmap(stream);
                    ViewerImage?.Dispose();
                    ViewerImage = next;
                }
                catch (Exception)
                {
                    // Кадр не разобрался: шкала остаётся, картинка прежняя.
                }
            });
        });
    }

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
        _ = ReloadDepartments();
    }

    /// <summary>Список отделов для отбора. Пополняется, пока станция работает.</summary>
    public async Task ReloadDepartments()
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
            // Путь отдела собирается по всей ветке предков, а таких запросов
            // столько же, сколько отделов: читаем в стороне от интерфейса.
            var staff = new StaffDirectory(_dbPath);
            var loaded = await Task.Run(() => staff.Departments()
                .Select(d => new DepartmentRow { Id = d.Id, Path = staff.DepartmentPath(d.Id) })
                .ToList());

            foreach (var department in loaded)
                Departments.Add(department);
        }
        catch (Exception)
        {
            // Справочник ещё не заведён: отбор по отделу просто недоступен.
        }

        Department = Departments.FirstOrDefault(d => d.Id == kept) ?? Departments[0];
    }

    partial void OnCurrentChanged(FoundFile? value)
    {
        CloseViewer();
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
    /// Открывает запись. Снимки и журналы станция показывает сама: для них
    /// кодеки не нужны. Видео и звук отдаются системному проигрывателю, потому
    /// что свой потребовал бы кодеки в сборке и отдельную возню с каждым
    /// форматом регистратора.
    /// </summary>
    /// <summary>Что удалось открыть: готовится в стороне от интерфейса.</summary>
    private sealed record Opened(Bitmap? Image, string Text, TimeSpan? Length, string Message);

    /// <summary>
    /// Открывает выбранную запись. Снимок надо раскодировать, у видео узнать
    /// длительность через ffprobe, журнал прочитать с диска: любое из этого
    /// на станции занимает сотни миллисекунд, поэтому окно просмотра
    /// открывается сразу, а содержимое подставляется, когда прочитается.
    /// </summary>
    [RelayCommand]
    private async Task Play()
    {
        if (Current is not { } file)
        {
            Hint = "выберите запись в списке";
            return;
        }

        CloseViewer();
        var request = _openRequest;

        var path = file.Path;
        var kind = file.Row.Kind;
        var size = file.Size;
        var shot = file.ShotAt;

        ViewerTitle = file.FileName;
        ViewerNote = "открываем запись";
        ViewerVisible = kind is MediaKind.Photo or MediaKind.Log or MediaKind.Video;

        var opened = await Task.Run(() => Open(path, kind));

        // Пока читали, оператор мог закрыть просмотр или выбрать другое.
        if (request != _openRequest)
        {
            opened.Image?.Dispose();
            return;
        }

        if (opened.Message.Length > 0)
        {
            Hint = opened.Message;
            if (opened.Image is null && opened.Text.Length == 0 && opened.Length is null)
            {
                ViewerVisible = false;
                return;
            }
        }

        if (opened.Image is not null)
        {
            ViewerImage = opened.Image;
            ViewerNote = $"{size}, снято {shot}";
        }
        else if (opened.Text.Length > 0)
        {
            ViewerText = opened.Text;
            ViewerNote = size;
        }
        else if (opened.Length is { } length)
        {
            _videoPath = path;
            ViewerLength = length.TotalSeconds;
            ViewerPosition = 0;
            ViewerNote = $"{size}, снято {shot}, "
                         + $"длительность {VideoPreview.Label(length)}. "
                         + "Просмотр по кадрам, звук в системном проигрывателе";
            ShowFrame(TimeSpan.Zero);
        }

        try
        {
            await Task.Run(() => new ActionLog(_dbPath).Write(ActionLog.Export,
                $"просмотр записи {Path.GetFileName(path)}"));
        }
        catch (Exception error)
        {
            Hint = UserError.Report("Не удалось записать просмотр в журнал", error);
        }
    }

    /// <summary>Читает запись. Работает в стороне: диск и ffprobe не быстры.</summary>
    private static Opened Open(string path, MediaKind kind)
    {
        if (!File.Exists(path))
            return new Opened(null, "", null, "записи больше нет в архиве");

        try
        {
            switch (kind)
            {
                case MediaKind.Photo:
                    using (var stream = File.OpenRead(path))
                        return new Opened(new Bitmap(stream), "", null, "");

                case MediaKind.Log:
                    // Журнал регистратора бывает на десятки мегабайт, а
                    // оператору нужно начало: читаем первые страницы.
                    var head = Read(path, 64 * 1024);
                    return new Opened(null, head.Length > 0 ? head : "файл пуст", null, "");

                case MediaKind.Video:
                    // Без длительности шкалу строить не из чего, и запись
                    // уходит системному проигрывателю, как раньше.
                    if (VideoPreview.Duration(path) is { } length)
                        return new Opened(null, "", length, "");
                    break;
            }

            var result = MediaTools.Open(path);
            return new Opened(null, "", null, result.Ok
                ? $"{Path.GetFileName(path)} открыт системным проигрывателем"
                : result.Message);
        }
        catch (Exception e)
        {
            return new Opened(null, "", null, UserError.Report("Не удалось открыть запись", e));
        }
    }

    /// <summary>Отдаёт запись системному проигрывателю по просьбе оператора.</summary>
    [RelayCommand]
    private void OpenExternally()
    {
        if (Current is not { } file)
            return;

        var result = MediaTools.Open(file.Path);
        Hint = result.Message;
    }

    [RelayCommand]
    private void CloseViewer()
    {
        _openRequest++;
        ViewerVisible = false;
        ViewerImage?.Dispose();
        ViewerImage = null;
        ViewerText = "";
        ViewerNote = "";
        _videoPath = "";
        _frameRequest++;
        ViewerLength = 0;
        ViewerPosition = 0;
    }

    /// <summary>Читает начало файла, не поднимая в память весь.</summary>
    private static string Read(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(limit, stream.Length)];
        var read = stream.Read(buffer, 0, buffer.Length);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

        return stream.Length > read
            ? text + Environment.NewLine + Environment.NewLine + "… показано начало файла"
            : text;
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

        if (string.IsNullOrWhiteSpace(Format))
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
            Hint = UserError.Report("Не удалось преобразовать запись", e);
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

        if ((!string.IsNullOrWhiteSpace(ShotFrom) && !TryDate(ShotFrom, out _))
            || (!string.IsNullOrWhiteSpace(ShotTo) && !TryDate(ShotTo, out _)))
        {
            Hint = "дата съёмки пишется как 02.09.2026";
            return;
        }

        DateTime? shotFrom = TryDate(ShotFrom, out var sf) ? sf.Date : null;
        DateTime? shotTo = TryDate(ShotTo, out var st) ? EndOfDay(st) : null;

        var filter = new ArchiveFilter
        {
            CollectedFrom = from.Date,
            CollectedTo = EndOfDay(to),
            ShotFrom = shotFrom,
            ShotTo = shotTo,
            DepartmentId = Department is { Id: > 0 } dep ? dep.Id : null,
            DeviceId = CameraId(),
            PersonnelNo = PersonnelNo?.Trim() ?? "",
            EmployeeName = EmployeeName?.Trim() ?? "",
            FileName = FileName?.Trim() ?? "",
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
            var message = UserError.Report("Не удалось выполнить запрос", e);
            if (generation == _generation)
                Hint = message;
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
        _generation++;
        Searching = false;
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
    private async Task ToggleImportant()
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
            var paths = rows.Select(r => r.Path).ToList();

            await Task.Run(() =>
            {
                var log = new CollectionLog(_dbPath);
                foreach (var path in paths)
                    log.SetImportant(path, protect);
            });

            foreach (var row in rows)
                row.Important = protect;

            Hint = protect
                ? $"защищено записей: {rows.Count}"
                : $"снята защита с записей: {rows.Count}";
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось изменить защиту", e);
        }
    }

    [RelayCommand]
    private async Task SaveNote()
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
            var paths = rows.Select(r => r.Path).ToList();

            await Task.Run(() =>
            {
                var log = new CollectionLog(_dbPath);
                foreach (var path in paths)
                    log.SetNote(path, note);
            });

            foreach (var row in rows)
                row.Note = note;

            Hint = note.Length == 0
                ? $"заметка снята с записей: {rows.Count}"
                : $"заметка сохранена, записей: {rows.Count}";
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось сохранить заметку", e);
        }
    }

    /// <summary>
    /// Удаляет отобранные записи. Защищённые пропускаются, и сколько именно
    /// пропущено, говорится прямо: иначе оператор считал бы, что удалил всё.
    /// </summary>
    [RelayCommand]
    private async Task Delete()
    {
        var rows = Chosen();
        if (rows.Count == 0)
        {
            Hint = "отберите записи галочками";
            return;
        }

        try
        {
            Hint = $"удаляем записей: {rows.Count}";
            var chosen = rows.Select(r => r.Row).ToList();

            // Удаление обходит файлы на диске: с сотней записей это надолго,
            // и интерфейс не должен замирать всё это время.
            var result = await Task.Run(() => new ArchiveSearch(_dbPath).Delete(chosen));

            var deleted = result.DeletedPaths.ToHashSet();
            if (Current is { } current && deleted.Contains(current.Path))
                Current = null;
            foreach (var row in rows.Where(r => deleted.Contains(r.Path)).ToArray())
                Results.Remove(row);

            var parts = new List<string> { $"удалено записей: {result.Deleted}" };
            if (result.Skipped > 0)
                parts.Add($"пропущено защищённых: {result.Skipped}");
            if (result.Failed > 0)
                parts.Add($"не удалось удалить: {result.Failed}");

            Hint = string.Join(", ", parts);
            Refresh();

            await Task.Run(() => new ActionLog(_dbPath).Write(ActionLog.Cleanup,
                $"удалено записей: {result.Deleted}, пропущено защищённых: {result.Skipped}"));
        }
        catch (Exception e)
        {
            Hint = UserError.Report("Не удалось удалить записи", e);
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

        var target = ExportTarget?.Trim() ?? "";
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
            Hint = UserError.Report("Не удалось выгрузить записи", e);
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
        var wanted = Camera?.Trim() ?? "";
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

            return match?.Id ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text?.Trim(), ["dd.MM.yyyy", "dd.MM.yy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static DateTime EndOfDay(DateTime date) =>
        date.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : date.Date.AddDays(1).AddTicks(-1);

    private static string Size(long bytes)
    {
        var mb = bytes / 1024d / 1024;
        return mb >= 1024 ? $"{mb / 1024:0.0} ГБ"
            : mb >= 1 ? $"{mb:0.0} МБ"
            : $"{bytes / 1024d:0} КБ";
    }
}
