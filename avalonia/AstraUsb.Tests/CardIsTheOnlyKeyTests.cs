using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Камера опознаётся только по файлу на карте.
///
/// Других признаков у этой модели нет: серийник USB зашит одинаковым на всех
/// экземплярах, и номер, которым камера подписывает записи, тоже везде один и
/// тот же. Опознание по ним слило бы разные аппараты в один, поэтому карта без
/// файла всегда означает новую камеру и новый номер.
/// </summary>
public sealed class CardIsTheOnlyKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-cardkey-").FullName;

    private DeviceRegistry NewRegistry() => new(Path.Combine(_dir, "devices.db"));

    /// <summary>Карта с одной записью, подписанной камерой.</summary>
    private string Card(string name, string deviceNo = "2222222", string personnelNo = "222222")
    {
        var video = Path.Combine(_dir, name, "DCIM", "VIDEO");
        Directory.CreateDirectory(video);
        File.WriteAllText(
            Path.Combine(video, $"A11_{deviceNo}_{personnelNo}_20260902180118_0001.mp4"), "x");
        return Path.Combine(_dir, name);
    }

    [Fact]
    public void A_card_without_astra_id_gets_a_local_number_and_keeps_it()
    {
        using var registry = NewRegistry();
        var card = Card("first");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1");

        Assert.Equal(id, DeviceRegistry.ReadDeviceIdFromUsb(card));
        Assert.Equal(id, registry.ResolveByCard(card, 1, "BESTCAM", "sdb1"));
    }

    [Fact]
    public void Cameras_with_the_same_factory_number_stay_separate()
    {
        using var registry = NewRegistry();

        // Заводской номер в именах записей у всех камер одинаковый.
        var one = Card("one");
        var two = Card("two");

        var first = registry.ResolveByCard(one, 1, "BESTCAM", "sdb1");
        var second = registry.ResolveByCard(two, 1, "BESTCAM", "sdc1");

        Assert.NotEqual(first, second);
        Assert.Equal(first, DeviceRegistry.ReadDeviceIdFromUsb(one));
        Assert.Equal(second, DeviceRegistry.ReadDeviceIdFromUsb(two));
    }

    [Fact]
    public void A_replaced_card_means_a_new_number()
    {
        using var registry = NewRegistry();

        var old = Card("old");
        var first = registry.ResolveByCard(old, 1, "BESTCAM", "sdb1");

        // Карту заменили: файла на ней нет, и станция считает камеру новой.
        var fresh = Card("fresh");
        var second = registry.ResolveByCard(fresh, 1, "BESTCAM", "sdb1");

        Assert.NotEqual(first, second);
        Assert.Equal(second, DeviceRegistry.ReadDeviceIdFromUsb(fresh));
    }

    [Fact]
    public void An_empty_card_without_recordings_gets_a_number_too()
    {
        using var registry = NewRegistry();
        var empty = Path.Combine(_dir, "blank");
        Directory.CreateDirectory(empty);

        var id = registry.ResolveByCard(empty, 7, "BESTCAM", "sdb1");

        Assert.Equal(id, DeviceRegistry.ReadDeviceIdFromUsb(empty));
    }

    [Fact]
    public void Astra_marker_wins_over_legacy_bestcam_id()
    {
        using var registry = NewRegistry();
        var card = Card("guest");
        DeviceRegistry.WriteDeviceIdToUsb(card, 999);
        CardIdentity.Write(card, "BCU-01-0001");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1");

        Assert.Equal(999, id);
    }

    [Fact]
    public void Known_legacy_marker_is_migrated_to_astra_id()
    {
        using var registry = NewRegistry();
        var card = Card("old");
        CardIdentity.Write(card, "BCU-01-0001");
        var existing = SeedLegacyDevice("BCU-01-0001", "64001");

        Assert.Equal(existing, registry.ResolveByCard(card, 1, "CAM", "sdb1"));
        Assert.Equal(existing, DeviceRegistry.ReadDeviceIdFromUsb(card));
        Assert.Equal("64001", registry.GetDeviceName(existing));
    }

    [Fact]
    public void Unknown_legacy_marker_does_not_define_identity()
    {
        using var registry = NewRegistry();
        var first = Card("first");
        var second = Card("second");
        CardIdentity.Write(first, "BCU-01-0001");
        CardIdentity.Write(second, "BCU-01-0001");

        var firstId = registry.ResolveByCard(first, 1, "CAM", "sdb1");
        var secondId = registry.ResolveByCard(second, 1, "CAM", "sdc1");

        Assert.NotEqual(firstId, secondId);
        Assert.Null(registry.FirmwareIdOf(firstId));
        Assert.Null(registry.FirmwareIdOf(secondId));
    }

    private long SeedLegacyDevice(string firmwareId, string name)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "devices.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (serial, label, name, first_seen, last_seen, firmware_id)
            VALUES ($serial, 'CAM', $name, '2026-09-14T10:00:00',
                    '2026-09-14T10:00:00', $firmware);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$serial", $"CARD_{firmwareId}_{name}");
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$firmware", firmwareId);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public void Ambiguous_legacy_marker_gets_a_new_local_id()
    {
        using var registry = NewRegistry();
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "devices.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DROP INDEX idx_devices_firmware";
        cmd.ExecuteNonQuery();
        SeedLegacyDevice("BCU-01-0001", "one");
        SeedLegacyDevice("BCU-01-0001", "two");
        var card = Card("ambiguous");
        CardIdentity.Write(card, "BCU-01-0001");

        Assert.Equal(3, registry.ResolveByCard(card, 1, "CAM", "sdb1"));
        Assert.Equal(3, DeviceRegistry.ReadDeviceIdFromUsb(card));
        Assert.Null(registry.FirmwareIdOf(3));
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
