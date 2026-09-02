using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Уборка хранилища. Удаление необратимо, поэтому проверяется и то, что уходит,
/// и то, что остаётся, и то, что журнал после уборки говорит правду: запись о
/// файле, которого нет, обманывает оператора на поиске.
/// </summary>
public sealed class StorageCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("astra-clean-").FullName;
    private readonly string _root;
    private readonly string _db;

    public StorageCleanupTests()
    {
        _root = Path.Combine(_dir, "USB_Backups");
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_dir, "devices.db");
        using var registry = new DeviceRegistry(_db);
    }

    private CollectionLog Log() => new(_db);

    /// <summary>Кладёт файл в хранилище и записывает его в журнал.</summary>
    private string Collected(string name, int sizeBytes, DateTime collectedAt)
    {
        var path = Path.Combine(_root, "Device1", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeBytes]);

        Log().Record([new CollectedFile(1, path, sizeBytes, collectedAt, collectedAt)]);
        return path;
    }

    [Fact]
    public void Expired_records_leave_the_disk_and_the_log_together()
    {
        var old = Collected("старое.mp4", 1000, Now.AddDays(-40));
        var fresh = Collected("свежее.mp4", 1000, Now.AddDays(-2));

        var (files, bytes) = StorageManager.DeleteExpired(Log(), Now.AddDays(-30), _root);

        Assert.Equal(1, files);
        Assert.Equal(1000, bytes);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.Equal(1, Log().Count());
    }

    [Fact]
    public void Nothing_expires_when_everything_is_recent()
    {
        Collected("свежее.mp4", 1000, Now.AddDays(-2));

        var (files, _) = StorageManager.DeleteExpired(Log(), Now.AddDays(-30), _root);

        Assert.Equal(0, files);
        Assert.Equal(1, Log().Count());
    }

    [Fact]
    public void A_record_whose_file_vanished_is_forgotten()
    {
        var path = Collected("пропало.mp4", 1000, Now.AddDays(-40));
        File.Delete(path);

        StorageManager.DeleteExpired(Log(), Now.AddDays(-30), _root);

        // Файла нет, и запись о нём больше никому не обещает его найти.
        Assert.Equal(0, Log().Count());
    }

    [Fact]
    public void Freeing_space_starts_with_the_earliest_arrival()
    {
        var first = Collected("первое.mp4", 1000, Now.AddDays(-10));
        var second = Collected("второе.mp4", 1000, Now.AddDays(-5));

        StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite, Log());

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Freed_records_disappear_from_the_log()
    {
        Collected("первое.mp4", 1000, Now.AddDays(-10));
        Collected("второе.mp4", 1000, Now.AddDays(-5));

        StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite, Log());

        Assert.Equal(1, Log().Count());
    }

    [Fact]
    public void Freeing_stops_as_soon_as_there_is_enough_room()
    {
        Collected("первое.mp4", 1000, Now.AddDays(-10));
        Collected("второе.mp4", 1000, Now.AddDays(-5));
        var third = Collected("третье.mp4", 1000, Now.AddDays(-1));

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 1500, StorageMode.Overwrite, Log());

        Assert.Equal(2000, freed);
        Assert.True(File.Exists(third));
    }

    [Fact]
    public void Files_the_log_never_knew_are_removed_too()
    {
        // Копии старше журнала: без этого прохода место не освободилось бы вовсе.
        var stray = Path.Combine(_root, "Device9", "ничейное.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllBytes(stray, new byte[1000]);

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite, Log());

        Assert.Equal(1000, freed);
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public void A_logged_file_whose_turn_has_not_come_survives_the_second_pass()
    {
        var newest = Collected("новейшее.mp4", 1000, Now.AddDays(-1));
        var oldest = Collected("древнее.mp4", 1000, Now.AddDays(-30));

        StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite, Log());

        // Ушло только самое раннее по журналу, хотя на диске обе копии
        // созданы только что и по дате файла неразличимы.
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void A_protected_record_outlives_its_retention_period()
    {
        var protectedPath = Collected("по-случаю.mp4", 1000, Now.AddDays(-100));
        var ordinary = Collected("обычное.mp4", 1000, Now.AddDays(-100));
        Log().SetImportant(protectedPath, true);

        var (files, _) = StorageManager.DeleteExpired(Log(), Now.AddDays(-30), _root);

        Assert.Equal(1, files);
        Assert.True(File.Exists(protectedPath));
        Assert.False(File.Exists(ordinary));
    }

    [Fact]
    public void A_protected_record_is_not_sacrificed_for_space()
    {
        var protectedPath = Collected("по-случаю.mp4", 1000, Now.AddDays(-100));
        var ordinary = Collected("обычное.mp4", 1000, Now.AddDays(-50));
        Log().SetImportant(protectedPath, true);

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite, Log());

        // Уборка прошла мимо защищённого и взяла следующее по очереди.
        Assert.Equal(1000, freed);
        Assert.True(File.Exists(protectedPath));
        Assert.False(File.Exists(ordinary));
    }

    [Fact]
    public void Protection_can_be_lifted()
    {
        var path = Collected("по-случаю.mp4", 1000, Now.AddDays(-100));
        var log = Log();
        log.SetImportant(path, true);
        log.SetImportant(path, false);

        StorageManager.DeleteExpired(Log(), Now.AddDays(-30), _root);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_note_stays_with_the_record()
    {
        var path = Collected("по-случаю.mp4", 1000, Now.AddDays(-1));
        Log().SetNote(path, "происшествие на проходной");

        var found = Log().CollectedBetween(Now.AddDays(-2), Now.AddDays(1));

        Assert.Equal("происшествие на проходной", Assert.Single(found).Note);
    }

    [Fact]
    public void A_repeated_backup_does_not_drop_the_protection()
    {
        var path = Collected("по-случаю.mp4", 1000, Now.AddDays(-1));
        Log().SetImportant(path, true);

        // Ту же камеру подключили снова, файл записан в журнал повторно.
        Log().Record([new CollectedFile(1, path, 1000, Now, Now)]);

        var entry = Assert.Single(Log().CollectedBetween(Now.AddDays(-2), Now.AddDays(1)));
        Assert.True(entry.Important);
    }

    [Fact]
    public void Warning_mode_deletes_nothing()
    {
        var path = Collected("первое.mp4", 1000, Now.AddDays(-30));

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 5000, StorageMode.Warn, Log());

        Assert.Equal(0, freed);
        Assert.True(File.Exists(path));
        Assert.Equal(1, Log().Count());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Файл базы может ещё держаться, для временной папки это неважно.
        }
    }
}
