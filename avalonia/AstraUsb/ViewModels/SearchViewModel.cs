using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка результата поиска.</summary>
public sealed partial class FoundFile : ObservableObject
{
    public required string Camera { get; init; }
    public required string FileName { get; init; }
    public required string Size { get; init; }
    public required string CollectedAt { get; init; }

    /// <summary>Время съёмки или пометка, что часам камеры доверять нельзя.</summary>
    public required string ShotAt { get; init; }

    public required string Path { get; init; }

    /// <summary>Запись защищена от уборки.</summary>
    [ObservableProperty] private bool _important;

    /// <summary>Заметка оператора: по какому случаю запись нужна.</summary>
    [ObservableProperty] private string _note = "";

    public string Mark => Important ? "защищено" : "";

    partial void OnImportantChanged(bool value) => OnPropertyChanged(nameof(Mark));
}

/// <summary>
/// Вкладка «Поиск».
///
/// Ищем по времени загрузки в станцию, а не по времени съёмки: часы на
/// камерах сбиваются, и файл, снятый «в 1970 году», по съёмке не найдётся.
/// Время съёмки показывается рядом и помечается, если ему нельзя верить.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int Limit = 500;

    private readonly string _dbPath;

    public ObservableCollection<FoundFile> Results { get; } = new();

    [ObservableProperty] private string _from = DateTime.Now.AddDays(-7).ToString("dd.MM.yyyy");
    [ObservableProperty] private string _to = DateTime.Now.ToString("dd.MM.yyyy");
    [ObservableProperty] private string _camera = "";
    [ObservableProperty] private string _hint = "укажите период и нажмите «Найти»";

    /// <summary>Куда выгружать найденное: флешка, сетевая папка, что укажут.</summary>
    [ObservableProperty] private string _exportTarget = "";

    /// <summary>Выгрузка идёт: второй раз запускать её не нужно.</summary>
    [ObservableProperty] private bool _exporting;

    [ObservableProperty] private FoundFile? _selected;
    [ObservableProperty] private string _noteInput = "";

    public SearchViewModel() : this(AppPaths.Database)
    {
    }

    public SearchViewModel(string dbPath) => _dbPath = dbPath;

    /// <summary>Выбор в списке подставляет заметку в поле: её правят на месте.</summary>
    partial void OnSelectedChanged(FoundFile? value) => NoteInput = value?.Note ?? "";

    /// <summary>
    /// Защищает запись от уборки или снимает защиту. Защищённое не уходит ни
    /// по сроку хранения, ни при нехватке места: такие записи держат по
    /// случаю, и восстановить их будет неоткуда.
    /// </summary>
    [RelayCommand]
    private void ToggleImportant()
    {
        if (Selected is not { } file)
        {
            Hint = "выберите запись в списке";
            return;
        }

        var next = !file.Important;

        try
        {
            new CollectionLog(_dbPath).SetImportant(file.Path, next);
            file.Important = next;
            Hint = next
                ? $"«{file.FileName}» защищено от автоудаления"
                : $"с «{file.FileName}» снята защита";
        }
        catch (Exception e)
        {
            Hint = $"не удалось изменить защиту: {e.Message}";
        }
    }

    /// <summary>Сохраняет заметку к записи.</summary>
    [RelayCommand]
    private void SaveNote()
    {
        if (Selected is not { } file)
        {
            Hint = "выберите запись в списке";
            return;
        }

        try
        {
            var note = NoteInput.Trim();
            new CollectionLog(_dbPath).SetNote(file.Path, note);
            file.Note = note;
            Hint = note.Length == 0 ? "заметка снята" : "заметка сохранена";
        }
        catch (Exception e)
        {
            Hint = $"не удалось сохранить заметку: {e.Message}";
        }
    }

    [RelayCommand]
    private void Search()
    {
        Results.Clear();

        if (!TryDate(From, out var from) || !TryDate(To, out var to))
        {
            Hint = "дата пишется как 02.09.2026";
            return;
        }

        try
        {
            long? camera = null;
            if (!string.IsNullOrWhiteSpace(Camera))
            {
                using var registry = new DeviceRegistry(_dbPath);
                var match = registry.ListDevices().FirstOrDefault(d =>
                    d.Name.Contains(Camera.Trim(), StringComparison.OrdinalIgnoreCase)
                    || d.Serial.Contains(Camera.Trim(), StringComparison.OrdinalIgnoreCase)
                    || d.Id.ToString() == Camera.Trim());

                if (match is null)
                {
                    Hint = $"камера «{Camera.Trim()}» не найдена";
                    return;
                }
                camera = match.Id;
            }

            var names = CameraNames();
            var log = new CollectionLog(_dbPath);
            var found = log.CollectedBetween(from.Date, to.Date.AddDays(1).AddSeconds(-1), camera);

            foreach (var file in found.Take(Limit))
            {
                Results.Add(new FoundFile
                {
                    Camera = names.TryGetValue(file.DeviceId, out var name) ? name : file.DeviceId.ToString(),
                    FileName = Path.GetFileName(file.DestPath),
                    Size = Size(file.SizeBytes),
                    CollectedAt = file.CollectedAt.ToString("dd.MM.yy HH:mm"),
                    ShotAt = file.ShotAt is null
                        ? "неизвестно"
                        : file.ShotAtTrusted
                            ? file.ShotAt.Value.ToString("dd.MM.yy HH:mm")
                            : "часы камеры сбиты",
                    Path = file.DestPath,
                    Important = file.Important,
                    Note = file.Note,
                });
            }

            Hint = found.Count switch
            {
                0 => "за этот период ничего не загружалось",
                > Limit => $"найдено {found.Count}, показаны первые {Limit}",
                _ => $"найдено файлов: {found.Count}",
            };
        }
        catch (Exception e)
        {
            Hint = $"поиск не удался: {e.Message}";
        }
    }

    /// <summary>
    /// Выгружает найденное наружу. Копирование идёт в стороне от интерфейса:
    /// записей может быть много, и окно не должно застывать.
    /// </summary>
    [RelayCommand]
    private async Task Export()
    {
        if (Exporting)
            return;

        if (Results.Count == 0)
        {
            Hint = "сначала найдите записи, потом выгружайте";
            return;
        }

        var target = ExportTarget.Trim();
        if (target.Length == 0)
        {
            Hint = "укажите папку, куда выгружать";
            return;
        }

        if (!Directory.Exists(target))
        {
            Hint = $"папка «{target}» недоступна, проверьте носитель";
            return;
        }

        var paths = Results.Select(r => r.Path).ToList();
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
                parts.Add($"не нашлось в хранилище: {result.Missing}");
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

    [RelayCommand]
    private void Reset()
    {
        From = DateTime.Now.AddDays(-7).ToString("dd.MM.yyyy");
        To = DateTime.Now.ToString("dd.MM.yyyy");
        Camera = "";
        Results.Clear();
        Hint = "укажите период и нажмите «Найти»";
    }

    private Dictionary<long, string> CameraNames()
    {
        try
        {
            using var registry = new DeviceRegistry(_dbPath);
            return registry.ListDevices().ToDictionary(
                d => d.Id,
                d => string.IsNullOrEmpty(d.Name) ? d.Serial.Replace("CARD_", "") : d.Name);
        }
        catch (Exception)
        {
            return new Dictionary<long, string>();
        }
    }

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), ["dd.MM.yyyy", "dd.MM.yy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string Size(long bytes)
    {
        var mb = bytes / 1024d / 1024;
        return mb >= 1024 ? $"{mb / 1024:0.0} ГБ" : $"{mb:0.0} МБ";
    }
}
