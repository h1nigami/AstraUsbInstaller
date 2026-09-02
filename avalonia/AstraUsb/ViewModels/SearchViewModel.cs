using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка результата поиска.</summary>
public sealed class FoundFile
{
    public required string Camera { get; init; }
    public required string FileName { get; init; }
    public required string Size { get; init; }
    public required string CollectedAt { get; init; }

    /// <summary>Время съёмки или пометка, что часам камеры доверять нельзя.</summary>
    public required string ShotAt { get; init; }

    public required string Path { get; init; }
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

    public SearchViewModel() : this(AppPaths.Database)
    {
    }

    public SearchViewModel(string dbPath) => _dbPath = dbPath;

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
