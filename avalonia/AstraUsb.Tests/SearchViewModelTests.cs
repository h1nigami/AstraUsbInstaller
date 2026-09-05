using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

public sealed class SearchViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-searchvm-").FullName;
    private readonly string _db;
    private readonly string _path;

    public SearchViewModelTests()
    {
        _db = Path.Combine(_dir, "devices.db");
        _path = Path.Combine(_dir, "record.mp4");
        File.WriteAllText(_path, "запись");
        using var registry = new DeviceRegistry(_db);
        new CollectionLog(_db).Record([new CollectedFile(1, _path, 10, null,
            new DateTime(2026, 9, 2, 23, 59, 59).AddMilliseconds(500))]);
    }

    private SearchViewModel Model() => new(_db) { From = "02.09.2026", To = "03.09.2026" };

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

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
