using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using AstraUsb.Services;
using Microsoft.Data.Sqlite;

namespace AstraUsb.Tests;

/// <summary>
/// Живая веб-панель во временном каталоге станции: настоящий сервер, настоящая
/// база и архив. Тесты ходят в неё по HTTP так же, как браузер, поэтому
/// проверяется и разбор запросов, и защита входом, и сами сервисы под ними.
/// </summary>
internal sealed class PanelHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "web-panel-" + Guid.NewGuid().ToString("N"));
    public string Store { get; }
    public string Db => AppPaths.Database;
    public int Port { get; }
    public WebPanel Panel { get; }
    public HttpClient Http { get; }
    public string Token { get; private set; } = "";

    public static string Password => PasswordGate.Default();

    public PanelHarness(Action<Settings>? configure = null, bool login = true)
    {
        AppPaths.Root = Root;
        AppPaths.EnsureCreated();
        Store = Path.Combine(Root, "archive");
        Directory.CreateDirectory(Store);

        Port = FreePort();
        var settings = Settings.Load();
        if (!settings.SelectBackupRoot(Store))
            throw new InvalidOperationException("архив теста не выбран");
        settings.WebEnabled = true;
        settings.WebPort = Port;
        configure?.Invoke(settings);
        if (!settings.Save(replaceUnreadable: true))
            throw new InvalidOperationException("настройки теста не записались");

        // Схема создаётся заранее: сервисы панели читают базу, не заводя таблиц.
        using (new DeviceRegistry(Db)) { }
        _ = new CollectionLog(Db);
        _ = new StaffDirectory(Db);
        _ = new ActionLog(Db);

        Panel = new WebPanel(Db);
        if (!Panel.Start(settings))
            throw new InvalidOperationException("панель теста не поднялась");

        Http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        { BaseAddress = new Uri($"{(settings.WebSsl ? "https" : "http")}://127.0.0.1:{Port}") };

        if (login)
            Token = Task.Run(() => LoginAsync()).GetAwaiter().GetResult();
    }

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task<HttpResponseMessage> LoginRawAsync(string account = "admin", string? password = null) =>
        await Http.PostAsync("/api/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["account"] = account,
            ["password"] = password ?? Password,
        }));

    public async Task<string> LoginAsync(string account = "admin", string? password = null)
    {
        var response = await LoginRawAsync(account, password);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
    }

    public async Task<(HttpStatusCode Status, JsonNode? Json)> SendAsync(
        HttpMethod method, string url, object? body = null, bool auth = true)
    {
        using var request = new HttpRequestMessage(method, url);
        if (auth)
            request.Headers.Add("X-Token", Token);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
        return (response.StatusCode, json);
    }

    public Task<(HttpStatusCode Status, JsonNode? Json)> GetAsync(string url, bool auth = true) =>
        SendAsync(HttpMethod.Get, url, null, auth);

    public Task<(HttpStatusCode Status, JsonNode? Json)> PostAsync(string url, object? body = null, bool auth = true) =>
        SendAsync(HttpMethod.Post, url, body ?? new { }, auth);

    public async Task<string> TicketAsync() =>
        (await PostAsync("/api/ticket")).Json!["ticket"]!.GetValue<string>();

    /// <summary>Камера в списке: прямая вставка, как её оставил бы сбор.</summary>
    public long AddDevice(string serial, string name = "", string firmware = "")
    {
        using var db = new SqliteConnection($"Data Source={Db}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO devices (serial, label, name, first_seen, last_seen, firmware_id)
            VALUES ($serial, '', $name, $at, $at, $firmware);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$serial", serial);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$firmware", firmware);
        command.Parameters.AddWithValue("$at", DeviceRegistry.Timestamp());
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Запись архива: файл на диске и строка в журнале сбора.</summary>
    public string AddRecord(string relative, string content, long device = 1, DateTime? collected = null,
        bool important = false, string note = "", DateTime? shot = null)
    {
        var full = Path.Combine(Store, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        var log = new CollectionLog(Db);
        log.Record([new CollectedFile(device, full, new FileInfo(full).Length,
            shot ?? DateTime.Now.AddHours(-3), collected ?? DateTime.Now.AddHours(-1))]);
        if (important)
            log.SetImportant(full, true);
        if (note.Length > 0)
            log.SetNote(full, note);
        return full;
    }

    public void Dispose()
    {
        // Остановка сервера в потоке окна Avalonia вешает процесс: гасим её в стороне.
        Task.Run(Panel.Dispose).GetAwaiter().GetResult();
        Http.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
