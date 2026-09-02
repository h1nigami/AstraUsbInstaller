using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Автоперезапись удаляет записи безвозвратно, поэтому проверяем границы:
/// удаляется только самое раннее, только в режиме перезаписи и ровно
/// столько, сколько нужно.
/// </summary>
public sealed class StorageManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-storage-").FullName;

    private string Write(string relative, int sizeBytes, DateTime written)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void Warn_mode_never_deletes_anything()
    {
        var old = Write("Device1/2026-01-01/a.mp4", 1000, new DateTime(2026, 1, 1));

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 100_000, StorageMode.Warn);

        Assert.Equal(0, freed);
        Assert.True(File.Exists(old), "в режиме предупреждения записи должны оставаться");
    }

    [Fact]
    public void Deletes_the_earliest_records_first()
    {
        var oldest = Write("Device1/2026-01-01/a.mp4", 1000, new DateTime(2026, 1, 1));
        var newest = Write("Device1/2026-06-01/b.mp4", 1000, new DateTime(2026, 6, 1));

        StorageManager.FreeUpSpace(_root, bytesToFree: 500, StorageMode.Overwrite);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest), "поздние записи трогать рано");
    }

    [Fact]
    public void Stops_as_soon_as_enough_is_freed()
    {
        Write("Device1/2026-01-01/a.mp4", 1000, new DateTime(2026, 1, 1));
        Write("Device1/2026-02-01/b.mp4", 1000, new DateTime(2026, 2, 1));
        var third = Write("Device1/2026-03-01/c.mp4", 1000, new DateTime(2026, 3, 1));

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 1500, StorageMode.Overwrite);

        Assert.Equal(2000, freed);
        Assert.True(File.Exists(third), "лишнего удалять не нужно");
    }

    [Fact]
    public void Nothing_to_free_means_nothing_deleted()
    {
        var file = Write("Device1/2026-01-01/a.mp4", 1000, new DateTime(2026, 1, 1));

        var freed = StorageManager.FreeUpSpace(_root, bytesToFree: 0, StorageMode.Overwrite);

        Assert.Equal(0, freed);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Empty_folders_are_tidied_after_deletion()
    {
        Write("Device1/2026-01-01/a.mp4", 1000, new DateTime(2026, 1, 1));

        StorageManager.FreeUpSpace(_root, bytesToFree: 1000, StorageMode.Overwrite);

        Assert.False(Directory.Exists(Path.Combine(_root, "Device1", "2026-01-01")));
    }

    [Fact]
    public void Check_reports_low_space_against_the_threshold()
    {
        // Порог заведомо больше любого реального диска: проверка обязана
        // сказать, что места мало.
        var status = StorageManager.Check(_root, minFreeBytes: long.MaxValue / 2);

        Assert.True(status.LowOnSpace);
        Assert.True(status.TotalBytes > 0);
    }

    [Fact]
    public void Check_survives_a_missing_folder()
    {
        var status = StorageManager.Check(Path.Combine(_root, "нет-такой"), minFreeBytes: 1);

        Assert.False(status.LowOnSpace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
