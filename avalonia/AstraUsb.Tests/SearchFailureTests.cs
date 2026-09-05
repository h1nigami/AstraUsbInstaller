using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SearchFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-search-errors-").FullName;
    private readonly string _previous = AppPaths.Root;
    private readonly string _blocked;

    public SearchFailureTests()
    {
        AppPaths.Root = _root;
        _blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(_blocked, "не каталог");
    }

    private FoundFile LogFile()
    {
        var path = Path.Combine(_root, "record.log");
        File.WriteAllText(path, "журнал камеры");
        return new FoundFile
        {
            Row = new ArchiveRow(new CollectedFile(1, path, 10, null, DateTime.Now), "1", "", "", ""),
            Path = path, FileName = "record.log", Camera = "1", Size = "10 Б",
            CollectedAt = "", ShotAt = "", Employee = "", Kind = "Журнал",
        };
    }

    [Theory]
    [InlineData("search")]
    [InlineData("note")]
    [InlineData("protect")]
    [InlineData("delete")]
    public async Task Database_failures_are_reported_without_exposing_exception_details(string operation)
    {
        var model = new SearchViewModel(Path.Combine(_blocked, "devices.db")) { Current = LogFile() };
        var command = operation switch
        {
            "note" => model.SaveNoteCommand,
            "protect" => model.ToggleImportantCommand,
            "delete" => model.DeleteCommand,
            _ => model.SearchCommand,
        };

        await command.ExecuteAsync(null);

        Assert.DoesNotContain(_root, model.Hint);
        Assert.True(File.Exists(CrashLog.FilePath));
        Assert.True(File.Exists(model.Current!.Path));
    }

    [Fact]
    public async Task A_failed_view_audit_does_not_throw_from_the_play_button()
    {
        var model = new SearchViewModel(Path.Combine(_blocked, "devices.db")) { Current = LogFile() };

        var error = await Record.ExceptionAsync(() => model.PlayCommand.ExecuteAsync(null));

        Assert.Null(error);
        Assert.Equal("журнал камеры", model.ViewerText);
        Assert.DoesNotContain(_root, model.Hint);
    }

    [Fact]
    public async Task Changing_selection_closes_the_old_viewer()
    {
        var model = new SearchViewModel(Path.Combine(_root, "devices.db")) { Current = LogFile() };
        await model.PlayCommand.ExecuteAsync(null);
        Assert.True(model.ViewerVisible);

        model.Current = null;

        Assert.False(model.ViewerVisible);
        Assert.Empty(model.ViewerText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_does_not_accept_results_from_the_pending_search(bool unavailableDatabase)
    {
        var db = Path.Combine(_root, "devices.db");
        var row = LogFile();
        new CollectionLog(db).Record([row.Row.File]);
        var model = new SearchViewModel(unavailableDatabase ? Path.Combine(_blocked, "devices.db") : db);

        var searching = model.SearchCommand.ExecuteAsync(null);
        model.ResetCommand.Execute(null);
        await searching;

        Assert.Empty(model.Results);
        Assert.False(model.Searching);
        Assert.Contains("укажите условия", model.Hint);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("destination")]
    [InlineData("date")]
    public async Task Clearing_a_bound_input_does_not_throw_from_its_button(string input)
    {
        var model = new SearchViewModel(Path.Combine(_root, "devices.db")) { Current = LogFile() };
        model.Format = null!;
        model.ExportTarget = null!;
        model.From = null!;
        var command = input switch
        {
            "format" => model.ConvertFileCommand,
            "destination" => model.ExportCommand,
            _ => model.SearchCommand,
        };

        var error = await Record.ExceptionAsync(() => command.ExecuteAsync(null));

        Assert.Null(error);
        Assert.False(model.Converting);
        Assert.False(model.Exporting);
        Assert.False(model.Searching);
    }

    [Fact]
    public void An_unwritable_frame_folder_is_reported_without_throwing()
    {
        File.WriteAllText(AppPaths.DataDir, "не каталог");

        var error = Record.Exception(() => Assert.Null(VideoPreview.Frame(LogFile().Path, TimeSpan.Zero)));

        Assert.Null(error);
    }

    [Theory]
    [InlineData("camera")]
    [InlineData("personnel")]
    [InlineData("employee")]
    [InlineData("filename")]
    public async Task Clearing_an_optional_filter_does_not_throw(string input)
    {
        var model = new SearchViewModel(Path.Combine(_root, "devices.db"));
        switch (input)
        {
            case "camera": model.Camera = null!; break;
            case "personnel": model.PersonnelNo = null!; break;
            case "employee": model.EmployeeName = null!; break;
            case "filename": model.FileName = null!; break;
        }

        var error = await Record.ExceptionAsync(() => model.SearchCommand.ExecuteAsync(null));

        Assert.Null(error);
        Assert.False(model.Searching);
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
