using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Те же сценарии, на которых Python-версия ловила ошибки идентификации.
/// Перенос обязан вести себя так же: базы у точек уже накоплены.
/// </summary>
public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-tests-").FullName;

    private DeviceRegistry NewRegistry() => new(Path.Combine(_dir, "devices.db"));

    private string Mount(string name, long? astraId = null)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        if (astraId is { } id)
            File.WriteAllText(Path.Combine(path, DeviceRegistry.DeviceIdFile), $"{id}\n");
        return path;
    }

    [Fact]
    public void Marker_survives_a_write_and_read()
    {
        var mount = Mount("a");
        DeviceRegistry.WriteDeviceIdToUsb(mount, 42);
        Assert.Equal(42, DeviceRegistry.ReadDeviceIdFromUsb(mount));
    }

    [Fact]
    public void Missing_marker_reads_as_no_id()
    {
        Assert.Null(DeviceRegistry.ReadDeviceIdFromUsb(Mount("empty")));
        Assert.Null(DeviceRegistry.ReadDeviceIdFromUsb(null));
    }

    [Fact]
    public void Garbage_in_marker_reads_as_no_id()
    {
        var mount = Mount("junk");
        File.WriteAllText(Path.Combine(mount, DeviceRegistry.DeviceIdFile), "не число\n");
        Assert.Null(DeviceRegistry.ReadDeviceIdFromUsb(mount));
    }

    [Fact]
    public void New_device_gets_a_row_and_a_marker()
    {
        using var registry = NewRegistry();
        var mount = Mount("fresh");

        var id = registry.ResolveDeviceId(mount, "SER1", "LABEL1", "sda1");

        Assert.True(registry.DeviceExists(id));
        Assert.Equal(id, DeviceRegistry.ReadDeviceIdFromUsb(mount));
    }

    [Fact]
    public void Same_serial_without_marker_is_the_same_device()
    {
        using var registry = NewRegistry();

        var first = registry.ResolveDeviceId(Mount("one"), "SHARED", "L", "sda1");
        var second = registry.ResolveDeviceId(Mount("two"), "SHARED", "L", "sdb1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Marker_wins_over_the_serial()
    {
        using var registry = NewRegistry();
        registry.ResolveDeviceId(Mount("first"), "SER-A", "L", "sda1");

        var id = registry.ResolveDeviceId(Mount("carried", astraId: 999), "SER-A", "L", "sdb1");

        Assert.Equal(999, id);
    }

    /// <summary>
    /// USB-эмуляторы отдают один серийник всем экземплярам. Устройство, чей
    /// номер принесён на носителе, обязано попасть в список даже тогда.
    /// </summary>
    [Fact]
    public void Device_with_a_taken_serial_still_gets_a_row()
    {
        using var registry = NewRegistry();
        const string shared = "Linux_File-Stor_Gadget_123456789ABC-0:0";

        var first = registry.ResolveDeviceId(Mount("a"), shared, "sdb1", "sdb1");
        var second = registry.ResolveDeviceId(Mount("b", astraId: 3666666), shared, "sdc1", "sdc1");

        Assert.Equal(3666666, second);
        Assert.NotEqual(first, second);
        Assert.True(registry.DeviceExists(second),
            "устройство с занятым серийником должно попадать в список");
    }

    [Fact]
    public void Devices_without_a_serial_do_not_collide()
    {
        using var registry = NewRegistry();

        var first = registry.ResolveDeviceId(Mount("n1"), null, "L", "sda1");
        var second = registry.ResolveDeviceId(Mount("n2"), null, "L", "sdb1");

        Assert.NotEqual(first, second);
        Assert.True(registry.DeviceExists(first));
        Assert.True(registry.DeviceExists(second));
    }

    [Fact]
    public void Devices_that_only_exist_in_backups_are_recovered()
    {
        var dbPath = Path.Combine(_dir, "devices.db");
        using (var registry = new DeviceRegistry(dbPath))
        {
            // Python-версия работала без проверки внешних ключей, поэтому в
            // базах на точках и завелись бэкапы без устройства. Здесь мы
            // воспроизводим именно такую унаследованную базу.
            using var db = new SqliteConnection($"Data Source={dbPath};Foreign Keys=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO backups (device_id, dest_path, total_files, total_bytes,
                                     started_at, finished_at)
                VALUES (777, '/dest/Device777', 3, 100, '2026-09-01T10:00:00', '2026-09-01T10:05:00')
                """;
            cmd.ExecuteNonQuery();
        }

        using var reopened = new DeviceRegistry(dbPath);

        Assert.True(reopened.DeviceExists(777),
            "устройство, от которого остались только бэкапы, должно восстанавливаться");
    }

    [Fact]
    public void Friendly_label_prefers_the_name_and_falls_back_to_the_number()
    {
        Assert.Equal("Проходная", DeviceRegistry.FriendlyLabel(3, "Проходная"));
        Assert.Equal("3", DeviceRegistry.FriendlyLabel(3, ""));
        Assert.Equal("3", DeviceRegistry.FriendlyLabel(3, null));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Файл базы может ещё держаться — для временной папки это неважно.
        }
    }
}
