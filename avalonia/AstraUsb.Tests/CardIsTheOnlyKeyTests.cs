using AstraUsb.Services;
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
    public void A_card_without_the_file_gets_a_number_and_keeps_it()
    {
        using var registry = NewRegistry();
        var card = Card("first");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1");

        Assert.Equal("BCU-01-0001", CardIdentity.Read(card));
        Assert.Equal("BCU-01-0001", registry.FirmwareIdOf(id));
        // Повторное подключение той же карты не заводит вторую камеру.
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
        Assert.Equal("BCU-01-0001", CardIdentity.Read(one));
        Assert.Equal("BCU-01-0002", CardIdentity.Read(two));
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
        Assert.Equal("BCU-01-0002", CardIdentity.Read(fresh));
    }

    [Fact]
    public void An_empty_card_without_recordings_gets_a_number_too()
    {
        using var registry = NewRegistry();
        var empty = Path.Combine(_dir, "blank");
        Directory.CreateDirectory(empty);

        var id = registry.ResolveByCard(empty, 7, "BESTCAM", "sdb1");

        Assert.Equal("BCU-07-0001", CardIdentity.Read(empty));
        Assert.Equal("BCU-07-0001", registry.FirmwareIdOf(id));
    }

    [Fact]
    public void A_number_from_another_station_is_kept()
    {
        using var registry = NewRegistry();
        var card = Card("guest");
        CardIdentity.Write(card, "BCU-05-0013");

        var id = registry.ResolveByCard(card, 1, "BESTCAM", "sdb1");

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
