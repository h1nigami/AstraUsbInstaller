using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Номер на карте — единственный источник истины. Проверяем маску, выдачу
/// номеров и то, что чужой номер не перезаписывается.
/// </summary>
public sealed class CardIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-card-").FullName;
    private string Db => Path.Combine(_dir, "devices.db");

    private string Card(string name, string? id = null)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        if (id is not null)
            File.WriteAllText(Path.Combine(path, CardIdentity.FileName), id + "\n");
        return path;
    }

    [Fact]
    public void Format_pads_station_and_sequence()
    {
        Assert.Equal("BCU-01-0042", CardIdentity.Format(1, 42));
        Assert.Equal("BCU-07-0001", CardIdentity.Format(7, 1));
    }

    [Fact]
    public void Recognises_its_own_numbers_and_rejects_others()
    {
        Assert.True(CardIdentity.IsOurs("BCU-01-0042"));
        Assert.False(CardIdentity.IsOurs("2222222"));
        Assert.False(CardIdentity.IsOurs("BCU-1-42"));
        Assert.False(CardIdentity.IsOurs(null));
    }

    [Fact]
    public void Tells_which_station_issued_the_number()
    {
        Assert.Equal(3, CardIdentity.StationOf("BCU-03-0007"));
        Assert.Equal(7, CardIdentity.SequenceOf("BCU-03-0007"));
        Assert.Null(CardIdentity.StationOf("2222222"));
    }

    [Fact]
    public void Card_without_a_number_gets_one_written_onto_it()
    {
        using var registry = new DeviceRegistry(Db);
        var card = Card("fresh");

        registry.ResolveByCard(card, stationNumber: 1, "cam", "sdb1");

        var written = CardIdentity.Read(card);
        Assert.True(CardIdentity.IsOurs(written),
            "без записи на карту камера при следующем подключении станет новой");
        Assert.Equal("BCU-01-0001", written);
    }

    [Fact]
    public void Same_card_keeps_its_record()
    {
        using var registry = new DeviceRegistry(Db);
        var card = Card("same");

        var first = registry.ResolveByCard(card, 1, "cam", "sdb1");
        var second = registry.ResolveByCard(card, 1, "cam", "sdb1");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Sequence_grows_for_each_new_camera()
    {
        using var registry = new DeviceRegistry(Db);

        registry.ResolveByCard(Card("a"), 1, "cam", "sdb1");
        registry.ResolveByCard(Card("b"), 1, "cam", "sdc1");

        Assert.Equal("BCU-01-0002", CardIdentity.Read(Path.Combine(_dir, "b")));
    }

    [Fact]
    public void Number_from_another_station_is_kept_not_overwritten()
    {
        using var registry = new DeviceRegistry(Db);
        var card = Card("visitor", "BCU-07-0123");

        registry.ResolveByCard(card, stationNumber: 1, "cam", "sdb1");

        Assert.Equal("BCU-07-0123", CardIdentity.Read(card));
    }

    [Fact]
    public void Camera_numbered_by_hand_is_kept_too()
    {
        // На карте номер не нашего формата — например заводской. Не трогаем.
        using var registry = new DeviceRegistry(Db);
        var card = Card("manual", "2222222");

        registry.ResolveByCard(card, 1, "cam", "sdb1");

        Assert.Equal("2222222", CardIdentity.Read(card));
    }

    [Fact]
    public void Numbers_of_different_stations_do_not_collide()
    {
        using var registry = new DeviceRegistry(Db);

        var ours = registry.ResolveByCard(Card("ours"), stationNumber: 1, "cam", "sdb1");
        var theirs = registry.ResolveByCard(Card("theirs", "BCU-02-0001"), 1, "cam", "sdc1");

        Assert.NotEqual(ours, theirs);
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
