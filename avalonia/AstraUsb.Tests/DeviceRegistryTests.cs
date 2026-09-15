using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-registry-").FullName;
    private string DbPath => Path.Combine(_dir, "devices.db");
    private DeviceRegistry NewRegistry() => new(DbPath);

    private string Card(string name, long? id, string? oldMarker = null)
    {
        var card = Path.Combine(_dir, name);
        Directory.CreateDirectory(card);
        if (id is { } number)
        {
            var dcim = Path.Combine(card, "DCIM");
            Directory.CreateDirectory(dcim);
            File.WriteAllText(Path.Combine(dcim,
                $"A11_{number}_222222_20260915120000_0001.mp4"), "video");
        }
        if (oldMarker is not null)
            File.WriteAllText(Path.Combine(card, ".astra_id"), oldMarker);
        return card;
    }

    private object? Scalar(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Device_id_wins_over_old_astra_marker()
    {
        using var registry = NewRegistry();
        var card = Card("device", 1234567, "999\n");

        Assert.Equal(1234567, registry.ResolveByCard(card, 1, "CAM", "sdb1"));
        Assert.Equal("999\n", File.ReadAllText(Path.Combine(card, ".astra_id")));
        Assert.Equal("device", Scalar("SELECT id_source FROM devices WHERE id = 1234567"));
    }

    [Fact]
    public void Missing_id_does_not_create_a_row_or_marker()
    {
        using var registry = NewRegistry();
        var card = Card("blank", null);

        Assert.Throws<InvalidDataException>(() => registry.ResolveByCard(card, 1, "CAM", "sdb1"));
        Assert.Empty(registry.ListDevices());
        Assert.False(File.Exists(Path.Combine(card, ".astra_id")));
        Assert.False(File.Exists(Path.Combine(card, ".bestcam_id")));
    }

    [Fact]
    public void Removed_device_is_not_registered_after_id_was_read()
    {
        using var registry = NewRegistry();
        var card = Card("removed", 1234567);

        Assert.Throws<IOException>(() =>
            registry.ResolveByCard(card, 1, "CAM", "sdb1", connected: () => false));
        Assert.Empty(registry.ListDevices());
        Assert.False(File.Exists(Path.Combine(card, ".astra_id")));
    }

    [Fact]
    public void Replaced_card_is_not_registered_under_previous_id()
    {
        using var registry = NewRegistry();
        var card = Card("replaced", 1234567);
        var dcim = Path.Combine(card, "DCIM");

        Assert.Throws<IOException>(() => registry.ResolveByCard(card, 1, "CAM", "sdb1",
            connected: () =>
            {
                File.Delete(Path.Combine(dcim, "A11_1234567_222222_20260915120000_0001.mp4"));
                File.WriteAllText(Path.Combine(dcim,
                    "A11_7654321_222222_20260915120000_0001.mp4"), "other card");
                return true;
            }));
        Assert.Empty(registry.ListDevices());
    }

    [Fact]
    public void Reconnecting_same_id_reuses_device_row()
    {
        using var registry = NewRegistry();
        var first = Card("first", 1234567);
        var second = Card("second", 1234567);

        Assert.Equal(1234567, registry.ResolveDeviceId(first, "SER", "CAM", "sdb1"));
        Assert.Equal(1234567, registry.ResolveDeviceId(second, "OTHER", "CAM", "sdc1"));
        Assert.Single(registry.ListDevices());
    }

    [Fact]
    public void Shared_usb_serial_does_not_merge_different_ids()
    {
        using var registry = NewRegistry();
        var first = Card("one", 1234567);
        var second = Card("two", 7654321);

        Assert.Equal(1234567, registry.ResolveDeviceId(first, "SHARED", "CAM", "sdb1"));
        Assert.Equal(7654321, registry.ResolveDeviceId(second, "SHARED", "CAM", "sdc1"));
        Assert.Equal(2, registry.ListDevices().Count);
    }

    [Fact]
    public void Old_database_row_with_same_number_is_not_reused()
    {
        using var registry = NewRegistry();
        using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO devices (id, serial, first_seen, last_seen)
                VALUES (1234567, 'OLD', '2026-09-01T10:00:00', '2026-09-01T10:00:00')
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() =>
            registry.ResolveByCard(Card("new", 1234567), 1, "CAM", "sdb1"));
        Assert.Equal("", Scalar("SELECT id_source FROM devices WHERE id = 1234567"));
    }

    [Fact]
    public void Missing_device_row_is_recovered_from_old_backup()
    {
        using (var registry = NewRegistry())
        using (var db = new SqliteConnection($"Data Source={DbPath};Foreign Keys=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO backups (device_id, dest_path, started_at, finished_at)
                VALUES (777, '/dest/Device777', '2026-09-01T10:00:00', '2026-09-01T10:05:00')
                """;
            command.ExecuteNonQuery();
        }
        using var reopened = NewRegistry();
        Assert.True(reopened.DeviceExists(777));
        Assert.Equal("", Scalar("SELECT id_source FROM devices WHERE id = 777"));
    }

    [Fact]
    public void Friendly_label_remains_for_search_and_devices_tab()
    {
        Assert.Equal("Astra ID 3 · Проходная", DeviceRegistry.FriendlyLabel(3, "Проходная"));
        Assert.Equal("Astra ID 3", DeviceRegistry.FriendlyLabel(3, ""));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
