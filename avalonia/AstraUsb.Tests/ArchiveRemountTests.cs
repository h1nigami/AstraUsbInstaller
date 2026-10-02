using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class ArchiveRemountTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-remount-").FullName;
    private readonly string _previous = AppPaths.Root;
    private string Old => Path.Combine(_root, "old");
    private string Live => Path.Combine(_root, "live");
    private string Db => Path.Combine(_root, "devices.db");
    private string OldFile => Path.Combine(Old, "Device7", "record.log");
    private string LiveFile => Path.Combine(Live, "Device7", "record.log");

    public ArchiveRemountTests()
    {
        AppPaths.Root = _root;
        Directory.CreateDirectory(Path.GetDirectoryName(LiveFile)!);
        File.WriteAllText(LiveFile, "живой архив");
        Assert.True(ArchiveGuard.Mark(Live));
    }

    private ArchiveSearch Search()
    {
        var resolve = ArchivePathTests.Resolver(new Settings
        {
            BackupRoot = Old, BackupUuid = "same", BackupRelativePath = "archive",
        }, Live, new("same", "", "archive"));
        var constructor = typeof(ArchiveSearch).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string), typeof(string), typeof(Func<string, string>)], null)!;
        return (ArchiveSearch)constructor.Invoke([Db, Live, resolve]);
    }

    private void Record(string path, bool important = false, DateTime? collected = null)
    {
        var log = new CollectionLog(Db);
        log.Record([new CollectedFile(7, path, 10, null, collected ?? DateTime.Now)]);
        log.SetImportant(path, important);
    }

    [Fact]
    public void Find_returns_the_live_file_and_preserves_the_database_key()
    {
        Record(OldFile);
        var row = Assert.Single(Search().Find(new ArchiveFilter()));
        Assert.Equal(OldFile, row.File.DestPath);
        Assert.Equal(LiveFile, row.Path);
        Assert.Equal("живой архив", File.ReadAllText(row.Path));
    }

    [Fact]
    public void Find_merges_mount_aliases_and_keeps_protection()
    {
        Record(OldFile, important: true, DateTime.Now.AddDays(-2));
        Record(LiveFile);
        var row = Assert.Single(Search().Find(new ArchiveFilter()));
        Assert.True(row.File.Important);
        Assert.Contains(OldFile, row.LoggedPaths);
        Assert.Contains(LiveFile, row.LoggedPaths);
    }

    [Fact]
    public void A_protected_alias_outside_the_search_period_still_protects_the_file()
    {
        Record(OldFile, important: true, DateTime.Now.AddDays(-2));
        Record(LiveFile);
        Assert.True(Assert.Single(Search().Find(new ArchiveFilter
        { CollectedFrom = DateTime.Now.AddDays(-1) })).File.Important);
    }

    [Fact]
    public void Delete_removes_the_live_file_and_forgets_all_logged_aliases()
    {
        Record(OldFile);
        Record(LiveFile);
        var row = new ArchiveRow(new CollectedFile(7, OldFile, 10, null, DateTime.Now), "7", "", "", "")
        { Path = LiveFile };
        var result = Search().Delete([row]);
        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(LiveFile));
        Assert.Equal(0, new CollectionLog(Db).Count());
        Assert.Contains(OldFile, result.DeletedPaths);
    }

    [Fact]
    public void Delete_rechecks_protection_on_an_alias_added_after_the_search()
    {
        Record(LiveFile);
        var row = Assert.Single(Search().Find(new ArchiveFilter()));
        Record(OldFile, important: true);
        var result = Search().Delete([row]);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(LiveFile));
        Assert.Equal(2, new CollectionLog(Db).Count());
    }

    [Theory]
    [InlineData("protect")]
    [InlineData("note")]
    public async Task Ui_edits_use_the_database_keys_instead_of_the_current_mountpoint(string action)
    {
        Record(OldFile);
        var row = new ArchiveRow(new CollectedFile(7, OldFile, 10, null, DateTime.Now), "7", "", "", "")
        { Path = LiveFile };
        var model = new SearchViewModel(Db, Live) { NoteInput = "случай" };
        model.Current = new FoundFile
        {
            Row = row, Path = LiveFile, Camera = "7", FileName = "record.log", Size = "10 Б",
            CollectedAt = "", ShotAt = "", Employee = "", Kind = "Журнал",
        };
        model.NoteInput = "случай";
        if (action == "protect") await model.ToggleImportantCommand.ExecuteAsync(null);
        else await model.SaveNoteCommand.ExecuteAsync(null);
        var updated = Assert.Single(new CollectionLog(Db).CollectedBetween(DateTime.MinValue, DateTime.MaxValue));
        if (action == "protect") Assert.True(updated.Important);
        else Assert.Equal("случай", updated.Note);
    }

    [Fact]
    public async Task Ui_delete_removes_the_selection_by_its_logged_key()
    {
        Record(LiveFile);
        var row = new ArchiveRow(new CollectedFile(7, LiveFile, 10, null, DateTime.Now), "7", "", "", "");
        var selected = new FoundFile
        {
            Row = row, Path = OldFile, Camera = "7", FileName = "record.log", Size = "10 Б",
            CollectedAt = "", ShotAt = "", Employee = "", Kind = "Журнал",
        };
        var model = new SearchViewModel(Db, Live) { Current = selected };
        model.Results.Add(selected);
        await model.DeleteCommand.ExecuteAsync(null);
        Assert.Null(model.Current);
        Assert.Empty(model.Results);
        Assert.False(File.Exists(LiveFile));
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
