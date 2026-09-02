using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Журнал действий. По нему разбирают, кто входил в закрытые разделы и что
/// уносил, поэтому он должен переживать годы работы, не разрастаясь без конца.
/// </summary>
public sealed class ActionLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-actions-").FullName;
    private readonly string _db;

    public ActionLogTests()
    {
        _db = Path.Combine(_dir, "devices.db");
    }

    private ActionLog Log() => new(_db);

    [Fact]
    public void An_event_can_be_read_back()
    {
        Log().Write(ActionLog.Access, "пароль принят, открыт раздел «Настройки»");

        var entry = Assert.Single(Log().Between(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1)));

        Assert.Equal(ActionLog.Access, entry.Kind);
        Assert.Contains("Настройки", entry.Message);
    }

    [Fact]
    public void Events_outside_the_period_are_not_returned()
    {
        Log().Write(ActionLog.Exit, "выход из программы");

        var found = Log().Between(DateTime.Now.AddDays(-3), DateTime.Now.AddDays(-2));

        Assert.Empty(found);
    }

    [Fact]
    public void The_newest_event_comes_first()
    {
        var log = Log();
        log.Write(ActionLog.Access, "первое");
        log.Write(ActionLog.Access, "второе");

        var found = log.Between(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));

        Assert.Equal("второе", found[0].Message);
    }

    [Fact]
    public void A_long_history_is_cut_to_the_limit()
    {
        var log = Log();
        for (var i = 0; i < 20; i++)
            log.Write(ActionLog.Backup, $"загрузка {i}");

        var removed = log.Trim(5);

        Assert.Equal(15, removed);
        Assert.Equal(5, log.Count());
    }

    [Fact]
    public void Trimming_keeps_the_newest_events()
    {
        var log = Log();
        log.Write(ActionLog.Backup, "давнее");
        log.Write(ActionLog.Backup, "недавнее");

        log.Trim(1);

        var entry = Assert.Single(log.Between(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1)));
        Assert.Equal("недавнее", entry.Message);
    }

    [Fact]
    public void A_short_history_survives_trimming_untouched()
    {
        var log = Log();
        log.Write(ActionLog.Settings, "пароль изменён");

        Assert.Equal(0, log.Trim(100));
        Assert.Equal(1, log.Count());
    }

    [Fact]
    public void The_limit_caps_how_much_a_query_returns()
    {
        var log = Log();
        for (var i = 0; i < 10; i++)
            log.Write(ActionLog.Backup, $"загрузка {i}");

        var found = log.Between(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1), limit: 3);

        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void The_log_shares_the_database_with_the_rest()
    {
        // Журнал надстраивается над той же базой, что и устройства: отдельный
        // файл пришлось бы отдельно переносить и отдельно терять.
        using var registry = new DeviceRegistry(_db);
        Log().Write(ActionLog.Access, "пароль принят");

        Assert.Equal(1, Log().Count());
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
