using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Журнал сбора. Опора — время загрузки в станцию: часы камеры сбиваются,
/// и время съёмки нельзя считать надёжным.
/// </summary>
public sealed class CollectionLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-collect-").FullName;

    private CollectionLog NewLog() => new(Path.Combine(_dir, "devices.db"));

    private static CollectedFile File(string path, DateTime collected,
        DateTime? shot = null, long device = 1, long size = 100) =>
        new(device, path, size, shot, collected);

    [Fact]
    public void Finds_files_by_the_time_they_reached_the_station()
    {
        var log = NewLog();
        log.Record(
        [
            File("/dest/Device1/a.mp4", new DateTime(2026, 9, 1, 10, 0, 0)),
            File("/dest/Device1/b.mp4", new DateTime(2026, 9, 5, 10, 0, 0)),
        ]);

        var found = log.CollectedBetween(
            new DateTime(2026, 9, 4), new DateTime(2026, 9, 6));

        Assert.Single(found);
        Assert.EndsWith("b.mp4", found[0].DestPath);
    }

    [Fact]
    public void A_wrong_camera_clock_does_not_hide_the_file()
    {
        // Камера с несброшенными часами: съёмка «в 1970 году», а приехал файл сегодня.
        var log = NewLog();
        log.Record([File("/dest/Device1/old.mp4",
            collected: new DateTime(2026, 9, 2, 12, 0, 0),
            shot: new DateTime(1970, 1, 1))]);

        var found = log.CollectedBetween(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));

        Assert.Single(found);
        Assert.False(found[0].ShotAtTrusted,
            "такому времени съёмки доверять нельзя, и это должно быть видно");
    }

    [Fact]
    public void A_sane_shot_time_is_marked_trusted()
    {
        var log = NewLog();
        log.Record([File("/dest/Device1/ok.mp4",
            collected: new DateTime(2026, 9, 2, 12, 0, 0),
            shot: new DateTime(2026, 9, 2, 9, 30, 0))]);

        Assert.True(log.CollectedBetween(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 3))[0].ShotAtTrusted);
    }

    [Fact]
    public void Shot_time_from_the_future_is_not_trusted()
    {
        var log = NewLog();
        log.Record([File("/dest/Device1/future.mp4",
            collected: new DateTime(2026, 9, 2),
            shot: new DateTime(2030, 1, 1))]);

        Assert.False(log.CollectedBetween(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 3))[0].ShotAtTrusted);
    }

    [Fact]
    public void Can_narrow_the_search_to_one_camera()
    {
        var log = NewLog();
        var moment = new DateTime(2026, 9, 2, 12, 0, 0);
        log.Record(
        [
            File("/dest/Device1/a.mp4", moment, device: 1),
            File("/dest/Device2/b.mp4", moment, device: 2),
        ]);

        var found = log.CollectedBetween(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 3), deviceId: 2);

        Assert.Single(found);
        Assert.Equal(2, found[0].DeviceId);
    }

    [Fact]
    public void Re_collecting_the_same_file_updates_it_instead_of_doubling()
    {
        var log = NewLog();
        log.Record([File("/dest/Device1/a.mp4", new DateTime(2026, 9, 1), size: 100)]);
        log.Record([File("/dest/Device1/a.mp4", new DateTime(2026, 9, 5), size: 250)]);

        Assert.Equal(1, log.Count());
        var found = log.CollectedBetween(new DateTime(2026, 9, 4), new DateTime(2026, 9, 6));
        Assert.Equal(250, found[0].SizeBytes);
    }

    [Fact]
    public void Lists_what_arrived_before_a_moment_for_cleanup()
    {
        var log = NewLog();
        log.Record(
        [
            File("/dest/Device1/old.mp4", new DateTime(2026, 1, 1)),
            File("/dest/Device1/new.mp4", new DateTime(2026, 9, 1)),
        ]);

        var stale = log.CollectedBefore(new DateTime(2026, 6, 1));

        Assert.Single(stale);
        Assert.EndsWith("old.mp4", stale[0].DestPath);
    }

    [Fact]
    public void Forgetting_a_file_removes_only_it()
    {
        var log = NewLog();
        log.Record(
        [
            File("/dest/Device1/a.mp4", new DateTime(2026, 9, 1)),
            File("/dest/Device1/b.mp4", new DateTime(2026, 9, 1)),
        ]);

        log.Forget("/dest/Device1/a.mp4");

        Assert.Equal(1, log.Count());
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
