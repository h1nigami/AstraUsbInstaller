using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Опознание регистратора. Серийник USB здесь бесполезен: аппарат
/// представляется как «Linux File-Stor Gadget» с зашитым серийником
/// 123456789ABC, одинаковым у всех экземпляров. Опознаём по номеру, который
/// прошивка пишет в свой журнал.
/// </summary>
public sealed class DeviceIdentifierTests : IDisposable
{
    private readonly string _card = Directory.CreateTempSubdirectory("astra-id-").FullName;

    private void WriteLog(string name, string firstLine)
    {
        var dir = Path.Combine(_card, "LOG");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), firstLine + "\n");
    }

    [Fact]
    public void Reads_the_number_the_firmware_writes()
    {
        WriteLog("20260902.txt", "2026/09/02-15:49:29 #ID:2222222 #Включение системы");

        var identity = DeviceIdentifier.Resolve(_card);

        Assert.Equal(IdentityKind.FirmwareId, identity.Kind);
        Assert.Equal("2222222", identity.Value);
    }

    [Fact]
    public void Takes_the_newest_log()
    {
        WriteLog("20260829.txt", "2026/08/29-14:05:44 #ID:1111111 #USB Connect");
        WriteLog("20260902.txt", "2026/09/02-15:49:29 #ID:2222222 #Включение системы");

        Assert.Equal("2222222", DeviceIdentifier.Resolve(_card).Value);
    }

    [Fact]
    public void Factory_number_does_not_identify_anything()
    {
        // Так выглядит журнал камеры, которой номер не прописывали.
        WriteLog("20260829.txt", "2026/08/29-14:05:44 #ID:0000000-000000#USB Connect");

        var identity = DeviceIdentifier.Resolve(_card);

        Assert.NotEqual(IdentityKind.FirmwareId, identity.Kind);
    }

    [Fact]
    public void Falls_back_to_the_card_marker_when_there_is_no_log()
    {
        DeviceRegistry.WriteDeviceIdToUsb(_card, 17);

        var identity = DeviceIdentifier.Resolve(_card);

        Assert.Equal(IdentityKind.CardMarker, identity.Kind);
        Assert.Equal("17", identity.Value);
    }

    [Fact]
    public void Firmware_number_wins_over_the_card_marker()
    {
        // Карту могли переставить из другой камеры, верить надо аппарату.
        DeviceRegistry.WriteDeviceIdToUsb(_card, 17);
        WriteLog("20260902.txt", "2026/09/02-15:49:29 #ID:2222222 #Включение системы");

        Assert.Equal(IdentityKind.FirmwareId, DeviceIdentifier.Resolve(_card).Kind);
        Assert.Equal("2222222", DeviceIdentifier.Resolve(_card).Value);
    }

    [Fact]
    public void Blank_card_identifies_nothing()
    {
        var identity = DeviceIdentifier.Resolve(_card);

        Assert.Equal(IdentityKind.Unknown, identity.Kind);
        Assert.False(identity.IsKnown);
    }

    [Fact]
    public void Missing_mount_point_identifies_nothing()
    {
        Assert.Equal(IdentityKind.Unknown, DeviceIdentifier.Resolve(null).Kind);
        Assert.Equal(IdentityKind.Unknown, DeviceIdentifier.Resolve("").Kind);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_card, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
