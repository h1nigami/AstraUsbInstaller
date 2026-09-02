using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Закрепление гнёзд за окнами. Смысл в том, чтобы плитка не прыгала:
/// камера из второго разъёма всегда должна занимать второе окно, независимо
/// от того, какую подключили раньше.
/// </summary>
public sealed class PortMapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-ports-").FullName;

    private PortMap NewMap() => new(Path.Combine(_dir, "devices.db"));

    private static UsbDevice Device(string name, string? port) =>
        new(name, $"/media/{name}", port);

    [Fact]
    public void Remembers_which_slot_a_socket_belongs_to()
    {
        var map = NewMap();

        map.Assign("1-4.2", 3);

        Assert.Equal(3, map.SlotOf("1-4.2"));
    }

    [Fact]
    public void Unknown_socket_has_no_slot()
    {
        Assert.Null(NewMap().SlotOf("1-9.9"));
        Assert.Null(NewMap().SlotOf(null));
    }

    [Fact]
    public void Assigning_a_slot_releases_its_previous_socket()
    {
        var map = NewMap();
        map.Assign("1-4.1", 0);

        map.Assign("1-4.2", 0);

        Assert.Null(map.SlotOf("1-4.1"));
        Assert.Equal(0, map.SlotOf("1-4.2"));
    }

    [Fact]
    public void Reassigning_a_socket_moves_it()
    {
        var map = NewMap();
        map.Assign("1-4.2", 1);

        map.Assign("1-4.2", 5);

        Assert.Equal(5, map.SlotOf("1-4.2"));
    }

    [Fact]
    public void Mapped_devices_keep_their_windows_whatever_the_order()
    {
        var map = NewMap();
        map.Assign("1-4.1", 0);
        map.Assign("1-4.2", 1);

        // Подключили в обратном порядке: сначала второе гнездо, потом первое.
        var placed = map.Arrange(
            [Device("sdc1", "1-4.2"), Device("sdb1", "1-4.1")], slots: 10);

        Assert.Equal("sdb1", placed[0]?.Name);
        Assert.Equal("sdc1", placed[1]?.Name);
    }

    [Fact]
    public void Unmapped_devices_fill_free_windows_in_order()
    {
        var map = NewMap();
        map.Assign("1-4.3", 2);

        var placed = map.Arrange(
            [Device("sdd1", "1-4.3"), Device("sdb1", "1-4.9"), Device("sdc1", null)],
            slots: 5);

        Assert.Equal("sdd1", placed[2]?.Name);
        Assert.Equal("sdb1", placed[0]?.Name);
        Assert.Equal("sdc1", placed[1]?.Name);
        Assert.Null(placed[3]);
    }

    [Fact]
    public void Devices_beyond_the_window_count_are_dropped_not_crashed()
    {
        var map = NewMap();

        var placed = map.Arrange(
            [Device("a", null), Device("b", null), Device("c", null)], slots: 2);

        Assert.Equal(2, placed.Count);
        Assert.All(placed, d => Assert.NotNull(d));
    }

    [Fact]
    public void Clearing_forgets_every_assignment()
    {
        var map = NewMap();
        map.Assign("1-4.1", 0);
        map.Assign("1-4.2", 1);

        map.Clear();

        Assert.Empty(map.All());
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
