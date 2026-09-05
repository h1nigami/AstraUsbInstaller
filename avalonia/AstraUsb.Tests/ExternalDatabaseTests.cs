using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Учёт собранного во внешней базе.
///
/// Проверки идут против настоящего сервера, если он задан переменными
/// окружения ASTRA_MYSQL_HOST, ASTRA_MYSQL_USER и ASTRA_MYSQL_PASSWORD. Без
/// них они пропускаются: на машине без базы падение говорило бы не о коде, а
/// об окружении.
/// </summary>
public sealed class ExternalDatabaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-extdb-").FullName;
    private readonly string _db;

    private static string? Host => Environment.GetEnvironmentVariable("ASTRA_MYSQL_HOST");
    private static string User => Environment.GetEnvironmentVariable("ASTRA_MYSQL_USER") ?? "root";
    private static string Password => Environment.GetEnvironmentVariable("ASTRA_MYSQL_PASSWORD") ?? "";
    private static string Database => Environment.GetEnvironmentVariable("ASTRA_MYSQL_DB") ?? "bestcam";

    private static bool Ready => !string.IsNullOrEmpty(Host);

    public ExternalDatabaseTests()
    {
        _db = Path.Combine(_dir, "devices.db");
    }

    private static Settings Server() => new()
    {
        SqlHost = Host ?? "",
        SqlPort = 3306,
        SqlDatabase = Database,
        SqlUser = User,
        SqlPassword = Password,
    };

    [Fact]
    public async Task An_unset_address_is_reported_without_touching_the_network()
    {
        var result = await new ExternalDatabase(new Settings()).CheckAsync();

        Assert.False(result.Ok);
        Assert.Contains("адрес", result.Message);
    }

    [Fact]
    public async Task An_unset_database_name_is_reported()
    {
        var result = await new ExternalDatabase(new Settings { SqlHost = "127.0.0.1" }).CheckAsync();

        Assert.False(result.Ok);
        Assert.Contains("имя базы", result.Message);
    }

    [Fact]
    public async Task Nothing_to_send_is_not_an_error()
    {
        var result = await new ExternalDatabase(new Settings()).SendAsync([], "BC-01");

        Assert.True(result.Ok);
        Assert.Equal(0, result.Sent);
    }

    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("MSSQL")]
    public async Task An_unsupported_provider_is_rejected_before_connection_or_sending(string kind)
    {
        var external = new ExternalDatabase(new Settings { SqlKind = kind });

        var check = await external.CheckAsync();
        var send = await external.SendAsync([], "BC-01");

        Assert.False(check.Ok);
        Assert.Contains(kind, check.Message);
        Assert.Contains("MySQL", check.Message);
        Assert.False(send.Ok);
        Assert.Contains(kind, send.Message);
    }

    [Fact]
    public async Task A_wrong_password_is_explained_in_plain_words()
    {
        if (!Ready)
            return;

        var settings = Server();
        settings.SqlPassword = "заведомо не тот";

        var result = await new ExternalDatabase(settings).CheckAsync();

        Assert.False(result.Ok);
        Assert.Contains("не подошли", result.Message);
    }

    [Fact]
    public async Task The_station_creates_its_own_table_and_the_server_answers()
    {
        if (!Ready)
            return;

        var result = await new ExternalDatabase(Server()).CheckAsync();

        Assert.True(result.Ok, result.Message);
        Assert.Contains("отвечает", result.Message);
    }

    [Fact]
    public async Task Collected_records_reach_the_server_and_repeat_does_not_double_them()
    {
        if (!Ready)
            return;

        var log = new CollectionLog(_db);
        var path = Path.Combine(_dir, "VID_0001.MP4");
        log.Record([new CollectedFile(1, path, 2048, DateTime.Now.AddHours(-1), DateTime.Now)]);

        var files = log.CollectedBetween(DateTime.Now.AddDays(-1), DateTime.Now.AddDays(1));
        var external = new ExternalDatabase(Server());

        var first = await external.SendAsync(files, "BC-01");
        var second = await external.SendAsync(files, "BC-01");

        Assert.True(first.Ok, first.Message);
        Assert.Equal(1, first.Sent);

        // Ключ это станция и путь, поэтому повторная отправка обновляет
        // строку, а не плодит вторую.
        Assert.True(second.Ok, second.Message);
        Assert.Equal(1, second.Sent);
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
