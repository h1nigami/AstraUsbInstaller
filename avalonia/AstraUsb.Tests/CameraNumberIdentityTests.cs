using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Опознание камеры, у которой заменили карту.
///
/// Файл на карте остаётся источником истины. Но карта может выйти из строя,
/// и тогда камеру ещё можно узнать по номеру, которым она подписывает имена
/// своих записей: этот номер живёт в аппарате.
/// </summary>
public sealed class CameraNumberIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-camno-").FullName;

    private DeviceRegistry NewRegistry() => new(Path.Combine(_dir, "devices.db"));

    /// <summary>Карта с одной записью, подписанной камерой.</summary>
    private string Card(string name, string deviceNo, string personnelNo = "222222")
    {
        var video = Path.Combine(_dir, name, "DCIM", "VIDEO");
        Directory.CreateDirectory(video);
        File.WriteAllText(
            Path.Combine(video, $"A11_{deviceNo}_{personnelNo}_20260902180118_0001.mp4"), "x");
        return Path.Combine(_dir, name);
    }

    [Fact]
    public void Card_number_stays_the_source_of_truth()
    {
        using var registry = NewRegistry();
        var card = Card("first", "2222222");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1",
            RecordingName.FromCard(card));

        // Номер выдан станцией и лёг на карту.
        Assert.Equal("BCU-01-0001", CardIdentity.Read(card));
        Assert.Equal("BCU-01-0001", registry.FirmwareIdOf(id));
        // И номер самой камеры запомнен на случай замены карты.
        Assert.Equal("2222222", registry.CameraNoOf(id));
    }

    [Fact]
    public void Replaced_card_does_not_make_a_second_camera()
    {
        using var registry = NewRegistry();

        var old = Card("old", "2222222");
        var first = registry.ResolveByCard(old, 1, "BESTCAM", "sdb1", RecordingName.FromCard(old));

        // Ту же камеру подключили с чистой картой: файла номера на ней нет.
        var fresh = Card("fresh", "2222222");
        var second = registry.ResolveByCard(fresh, 1, "BESTCAM", "sdb1", RecordingName.FromCard(fresh));

        Assert.Equal(first, second);
        // Прежний номер вернулся на новую карту.
        Assert.Equal("BCU-01-0001", CardIdentity.Read(fresh));
    }

    [Fact]
    public void A_different_camera_gets_its_own_number()
    {
        using var registry = NewRegistry();

        var one = Card("one", "2222222");
        var first = registry.ResolveByCard(one, 1, "BESTCAM", "sdb1", RecordingName.FromCard(one));

        var two = Card("two", "3333333");
        var second = registry.ResolveByCard(two, 1, "BESTCAM", "sdc1", RecordingName.FromCard(two));

        Assert.NotEqual(first, second);
        Assert.Equal("BCU-01-0002", CardIdentity.Read(two));
    }

    [Fact]
    public void Factory_zeros_do_not_merge_cameras()
    {
        using var registry = NewRegistry();

        // Обеим камерам номер не прописывали, в именах записей стоят нули.
        var one = Card("zero-one", "0000000");
        var first = registry.ResolveByCard(one, 1, "BESTCAM", "sdb1", RecordingName.FromCard(one));

        var two = Card("zero-two", "0000000");
        var second = registry.ResolveByCard(two, 1, "BESTCAM", "sdc1", RecordingName.FromCard(two));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Empty_card_without_recordings_gets_a_new_number()
    {
        using var registry = NewRegistry();
        var empty = Path.Combine(_dir, "blank");
        Directory.CreateDirectory(empty);

        var id = registry.ResolveByCard(empty, 7, "BESTCAM", "sdb1", RecordingName.FromCard(empty));

        Assert.Equal("BCU-07-0001", CardIdentity.Read(empty));
        Assert.Equal("BCU-07-0001", registry.FirmwareIdOf(id));
        Assert.Null(registry.CameraNoOf(id));
    }

    [Fact]
    public void A_number_from_another_station_is_kept()
    {
        using var registry = NewRegistry();
        var card = Card("guest", "4444444");
        CardIdentity.Write(card, "BCU-05-0013");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1", RecordingName.FromCard(card));

        Assert.Equal("BCU-05-0013", CardIdentity.Read(card));
        Assert.Equal("BCU-05-0013", registry.FirmwareIdOf(id));
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
