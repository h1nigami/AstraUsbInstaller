using System.Collections.ObjectModel;
using System.Globalization;
using AstraUsb.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstraUsb.ViewModels;

/// <summary>Строка журнала на экране.</summary>
public sealed class LogRow
{
    public required string At { get; init; }
    public required string Kind { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Вкладка «Журнал»: что происходило со станцией.
///
/// Период по умолчанию берётся сегодняшний: разбор почти всегда начинается с
/// вопроса «что было только что».
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private const int Limit = 500;

    private readonly string _dbPath;

    public ObservableCollection<LogRow> Entries { get; } = new();

    [ObservableProperty] private string _from = DateTime.Now.ToString("dd.MM.yyyy");
    [ObservableProperty] private string _to = DateTime.Now.ToString("dd.MM.yyyy");
    [ObservableProperty] private string _hint = "";

    public LogViewModel() : this(AppPaths.Database)
    {
    }

    public LogViewModel(string dbPath)
    {
        _dbPath = dbPath;
        _ = Reload();
    }

    [RelayCommand]
    public async Task Reload()
    {
        Entries.Clear();

        if (!TryDate(From, out var from) || !TryDate(To, out var to))
        {
            Hint = "дата пишется как 02.09.2026";
            return;
        }

        try
        {
            // Журнал станции живёт годами, и выборка из базы занимает время.
            // В потоке интерфейса это заметно на переходе между разделами.
            var found = await Task.Run(() => new ActionLog(_dbPath)
                .Between(from.Date, to.Date.AddDays(1).AddSeconds(-1), Limit));

            foreach (var entry in found)
            {
                Entries.Add(new LogRow
                {
                    At = entry.At.ToString("dd.MM.yy HH:mm:ss"),
                    Kind = entry.Kind,
                    Message = entry.Message,
                });
            }

            Hint = found.Count switch
            {
                0 => "за этот период записей нет",
                Limit => $"показаны последние {Limit} событий",
                _ => $"событий: {found.Count}",
            };
        }
        catch (Exception e)
        {
            Hint = $"не удалось прочитать журнал: {e.Message}";
        }
    }

    [RelayCommand]
    private void Today()
    {
        From = DateTime.Now.ToString("dd.MM.yyyy");
        To = From;
        _ = Reload();
    }

    [RelayCommand]
    private void Week()
    {
        From = DateTime.Now.AddDays(-7).ToString("dd.MM.yyyy");
        To = DateTime.Now.ToString("dd.MM.yyyy");
        _ = Reload();
    }

    private static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text.Trim(), ["dd.MM.yyyy", "dd.MM.yy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
