using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SearchViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-searchvm-").FullName;
    private readonly string _db;
    private readonly string _path;
    private readonly string _archive;
    private readonly string _previous = AppPaths.Root;

    public SearchViewModelTests()
    {
        AppPaths.Root = _dir;
        _db = Path.Combine(_dir, "devices.db");
        _archive = Path.Combine(_dir, "archive");
        _path = Path.Combine(_archive, "Device1", "record.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        ArchiveGuard.Mark(_archive);
        File.WriteAllText(_path, "запись");
        File.SetLastWriteTime(_path, new DateTime(2026, 9, 2, 23, 59, 59).AddMilliseconds(500));
        using var registry = new DeviceRegistry(_db);
        new CollectionLog(_db).Record([new CollectedFile(1, _path, 10, null,
            new DateTime(2026, 9, 2, 23, 59, 59).AddMilliseconds(500))]);
    }

    private SearchViewModel Model() => new(_db, _archive) { From = "02.09.2026", To = "03.09.2026" };

    [Fact]
    public async Task Protecting_a_search_result_prevents_its_deletion_without_another_search()
    {
        var model = Model();
        await model.SearchCommand.ExecuteAsync(null);
        model.Current = Assert.Single(model.Results);
        await model.ToggleImportantCommand.ExecuteAsync(null);

        await model.DeleteCommand.ExecuteAsync(null);

        Assert.True(File.Exists(_path));
        Assert.Single(model.Results);
        Assert.Equal(1, new CollectionLog(_db).Count());
    }

    [Fact]
    public async Task Unprotecting_a_search_result_allows_its_deletion_without_another_search()
    {
        new CollectionLog(_db).SetImportant(_path, true);
        var model = Model();
        await model.SearchCommand.ExecuteAsync(null);
        model.Current = Assert.Single(model.Results);
        await model.ToggleImportantCommand.ExecuteAsync(null);

        await model.DeleteCommand.ExecuteAsync(null);

        Assert.False(File.Exists(_path));
        Assert.Empty(model.Results);
        Assert.Null(model.Current);
    }

    [Fact]
    public async Task An_unknown_camera_does_not_return_the_entire_archive()
    {
        var model = Model();
        model.Camera = "несуществующая камера";

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Empty(model.Results);
    }

    [Fact]
    public async Task The_last_fractional_second_of_the_selected_day_is_included()
    {
        var model = Model();
        model.To = "02.09.2026";

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Single(model.Results);
    }

    [Fact]
    public async Task An_invalid_shot_date_does_not_silently_remove_the_filter()
    {
        var model = Model();
        model.ShotFrom = "не дата";

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Empty(model.Results);
        Assert.Contains("дата", model.Hint);
    }

    [Fact]
    public async Task A_small_file_size_is_shown_in_bytes()
    {
        var model = Model();

        await model.SearchCommand.ExecuteAsync(null);

        Assert.Equal("10 Б", Assert.Single(model.Results).Size);
    }

    private string AddSecondRecord()
    {
        var path = Path.Combine(_archive, "Device1", "second.mp4");
        File.WriteAllText(path, "вторая запись");
        File.SetLastWriteTime(path, new DateTime(2026, 9, 2, 12, 0, 0));
        new CollectionLog(_db).Record([new CollectedFile(1, path, 20, null, new DateTime(2026, 9, 2))]);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_uses_checked_rows_or_all_results_independently_of_the_current_row(bool checkedOnly)
    {
        AddSecondRecord();
        var model = Model();
        model.ExportTarget = Directory.CreateDirectory(Path.Combine(_dir, "export")).FullName;
        await model.SearchCommand.ExecuteAsync(null);
        var selected = model.Results.Single(r => r.Path == _path);
        model.Current = model.Results.Single(r => r.Path != _path);
        selected.Selected = checkedOnly;

        await model.ExportCommand.ExecuteAsync(null);

        var exported = Directory.GetFiles(model.ExportTarget, "*.mp4", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(checkedOnly ? ["record.mp4"] : new[] { "record.mp4", "second.mp4" }, exported);
    }

    [Fact]
    public async Task Individual_checkboxes_notify_the_export_count_without_manual_refresh()
    {
        AddSecondRecord();
        var model = Model();
        await model.SearchCommand.ExecuteAsync(null);
        var changes = new List<string?>();
        model.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        model.Results[0].Selected = true;

        Assert.Contains(nameof(model.ExportLabel), changes);
        Assert.Equal("Выгрузить (1)", model.ExportLabel);
        model.Results[0].Selected = false;
        Assert.Equal("Выгрузить (2)", model.ExportLabel);
    }

    [Fact]
    public async Task Export_is_disabled_until_results_exist_and_while_export_is_running()
    {
        var model = Model();
        Assert.Equal("Выгрузить (0)", model.ExportLabel);
        Assert.False(model.ExportCommand.CanExecute(null));
        var changed = 0;
        model.ExportCommand.CanExecuteChanged += (_, _) => changed++;

        await model.SearchCommand.ExecuteAsync(null);

        Assert.True(changed > 0);
        Assert.True(model.ExportCommand.CanExecute(null));
        model.Exporting = true;
        Assert.False(model.ExportCommand.CanExecute(null));
        model.Exporting = false;
        model.Results.Clear();
        Assert.False(model.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Clearing_all_checkboxes_does_not_expand_delete_to_the_entire_result()
    {
        var second = AddSecondRecord();
        var model = Model();
        await model.SearchCommand.ExecuteAsync(null);
        model.Current = null;

        await model.DeleteCommand.ExecuteAsync(null);
        await model.ToggleImportantCommand.ExecuteAsync(null);

        Assert.True(File.Exists(_path));
        Assert.True(File.Exists(second));
        Assert.All(new CollectionLog(_db).CollectedBetween(DateTime.MinValue, DateTime.MaxValue),
            row => Assert.False(row.Important));
    }

    [AvaloniaFact]
    public void The_collection_screen_shows_archive_capacity_above_the_device_tiles()
    {
        new Settings { AlarmSound = false }.Save();
        using var model = new MainWindowViewModel(() => []);
        var window = new MainWindow(model);
        window.Show();
        try
        {
            window.UpdateLayout();
            var caption = window.GetVisualDescendants().OfType<TextBlock>()
                .SingleOrDefault(t => t.IsEffectivelyVisible && t.Text == "ДИСК АРХИВА");
            Assert.NotNull(caption);
            var capacity = window.GetVisualDescendants().OfType<ProgressBar>()
                .SingleOrDefault(p => p.IsEffectivelyVisible && p.Maximum == 150);
            Assert.NotNull(capacity);
            Assert.True(capacity.Bounds.Width > 150);
            var tile = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.IsEffectivelyVisible && b.Classes.Contains("bay"));
            var barBottom = capacity.TranslatePoint(new Point(0, capacity.Bounds.Height), window)!.Value.Y;
            var tileTop = tile.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            Assert.True(barBottom < tileTop);
        }
        finally
        {
            model.ExitCommand.Execute(null);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
        }
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
