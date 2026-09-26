using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class FactoryResetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-reset-").FullName;
    private readonly string _appRoot = AppPaths.Root;

    public FactoryResetTests() => AppPaths.Root = _root;

    public void Dispose()
    {
        AppPaths.Root = _appRoot;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private string Archive()
    {
        var archive = Path.Combine(_root, "archive");
        Assert.True(ArchiveGuard.Mark(archive));
        Directory.CreateDirectory(Path.Combine(archive, "Device1"));
        File.WriteAllText(Path.Combine(archive, "Device1", "clip.mp4"), "запись");
        return archive;
    }

    [Fact]
    public void Clears_devices_history_archive_and_settings_but_keeps_action_log()
    {
        var db = AppPaths.Database;
        var archive = Archive();
        var card = Directory.CreateDirectory(Path.Combine(_root, "card", "LOG")).Parent!.FullName;
        File.WriteAllText(Path.Combine(card, "LOG", "20260915.txt"), "#ID:1\n");
        using (var registry = new DeviceRegistry(db))
            registry.ResolveByCard(card, 1, "CAM", "sdb1");
        new ActionLog(db).Write(ActionLog.Settings, "до сброса");
        new CollectionLog(db).Record([new CollectedFile(1, Path.Combine(archive, "Device1", "clip.mp4"),
            6, DateTime.Now, DateTime.Now)]);
        Assert.True(new Settings { BackupRoot = archive }.Save());

        var result = FactoryReset.Run(db, archive);

        Assert.Equal(1, result.Devices);
        Assert.True(result.Entries >= 2);
        Assert.Empty(Directory.GetFileSystemEntries(archive));
        Assert.False(File.Exists(Settings.FilePath));
        Assert.Empty(new CollectionLog(db).CollectedBefore(DateTime.Now.AddDays(1)));
        using var check = new SqliteConnection($"Data Source={db}");
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM devices), (SELECT COUNT(*) FROM action_log)";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.True(reader.GetInt64(1) > 0);
    }

    [Fact]
    public void Refuses_while_station_is_copying()
    {
        var archive = Archive();
        BusyMarker.Touch();

        Assert.Throws<InvalidOperationException>(() => FactoryReset.Run(AppPaths.Database, archive));
        Assert.True(File.Exists(Path.Combine(archive, "Device1", "clip.mp4")));
    }

    [Fact]
    public void Folder_without_archive_marker_is_not_touched()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "not-archive")).FullName;
        File.WriteAllText(Path.Combine(folder, "important.txt"), "чужой файл");

        var result = FactoryReset.Run(AppPaths.Database, folder);

        Assert.Equal(0, result.Entries);
        Assert.True(File.Exists(Path.Combine(folder, "important.txt")));
    }
}
