using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Опознание камеры по номеру из её прошивки. Это ключ, который живёт в
/// аппарате, поэтому переживает и замену карты, и одинаковый на всех
/// экземплярах серийник USB.
/// </summary>
public sealed class FirmwareIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-fw-").FullName;
    private string Db => Path.Combine(_dir, "devices.db");

    private string Card(string name, string? firmwareId = null)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(path, "LOG"));
        if (firmwareId is not null)
        {
            File.WriteAllText(Path.Combine(path, "LOG", "20260902.txt"),
                $"2026/09/02-15:49:29 #ID:{firmwareId} #Включение системы\n");
        }
        return path;
    }

    [Fact]
    public void Same_camera_keeps_its_record_across_reconnects()
    {
        using var registry = new DeviceRegistry(Db);
        var card = Card("cam", "2222222");

        var first = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(card), card, "sdb1", "sdb1");
        var second = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(card), card, "sdb1", "sdb1");

        Assert.Equal(first, second);
        Assert.Equal("2222222", registry.FirmwareIdOf(first));
    }

    [Fact]
    public void Different_cameras_get_different_records_despite_one_serial()
    {
        // Именно этот случай ломал прежнюю версию: серийник у всех одинаковый.
        using var registry = new DeviceRegistry(Db);

        var one = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(Card("a", "1111111")), null, "sdb1", "sdb1");
        var two = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(Card("b", "2222222")), null, "sdc1", "sdc1");

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void Replacing_the_card_does_not_create_a_second_record()
    {
        using var registry = new DeviceRegistry(Db);

        // Та же камера, но карта другая: журнал на ней тот же самый номер.
        var before = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(Card("old-card", "2222222")), null, "sdb1", "sdb1");
        var after = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(Card("new-card", "2222222")), null, "sdb1", "sdb1");

        Assert.Equal(before, after);
    }

    [Fact]
    public void Camera_without_a_number_falls_back_to_the_card_marker()
    {
        using var registry = new DeviceRegistry(Db);
        var card = Card("no-number");

        var id = registry.ResolveByIdentity(
            DeviceIdentifier.Resolve(card), card, "sdb1", "sdb1");

        Assert.True(registry.DeviceExists(id));
        Assert.Null(registry.FirmwareIdOf(id));
        Assert.Equal(id, DeviceRegistry.ReadDeviceIdFromUsb(card));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
