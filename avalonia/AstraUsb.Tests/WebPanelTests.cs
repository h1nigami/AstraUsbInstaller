using System.Net;
using System.Net.Http.Headers;
using AstraUsb.Services;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Каркас веб-панели: вход, защита адресов, состояние, команды станции,
/// отдача записей и страницы. Сервер настоящий, запросы идут по HTTP.
/// </summary>
[Collection("Каталог данных")]
public sealed class WebPanelTests
{
    private static string[] ProtectedGets =>
    [
        "/api/state", "/api/departments", "/api/search", "/api/devices", "/api/staff", "/api/events",
        "/api/settings", "/api/diagnostics", "/api/file?p=a", "/api/zip?id=1",
    ];

    [Fact]
    public async Task Every_data_address_refuses_a_visitor_without_a_key()
    {
        using var panel = new PanelHarness(login: false);

        foreach (var url in ProtectedGets)
            Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync(url, auth: false)).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostAsync("/api/ticket", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostAsync("/api/restart", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostAsync("/api/bay/0/priority", auth: false)).Status);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        using var panel = new PanelHarness();
        panel.Http.DefaultRequestHeaders.Add("X-Token", "wrong");

        var response = await panel.Http.GetAsync("/api/state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_returns_a_key_and_is_written_to_the_log()
    {
        using var panel = new PanelHarness(login: false);

        var token = await panel.LoginAsync();

        Assert.Equal(32, token.Length);
        var entries = new ActionLog(panel.Db).Between(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(1));
        Assert.Contains(entries, e => e.Kind == ActionLog.Access && e.Message.Contains("вход в веб-панель"));
    }

    [Fact]
    public async Task The_account_name_ignores_case_and_a_wrong_name_is_refused()
    {
        using var panel = new PanelHarness(login: false);

        Assert.Equal(HttpStatusCode.OK, (await panel.LoginRawAsync("ADMIN")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.LoginRawAsync("кто-то")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_recorded()
    {
        using var panel = new PanelHarness(login: false);

        var response = await panel.LoginRawAsync(password: "неверный");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var entries = new ActionLog(panel.Db).Between(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(1));
        Assert.Contains(entries, e => e.Message.Contains("отказ"));
    }

    [Fact]
    public async Task Five_failures_in_a_row_lock_the_sign_in_even_for_the_right_password()
    {
        using var panel = new PanelHarness(login: false);

        for (var i = 0; i < 5; i++)
            await panel.LoginRawAsync(password: "неверный");

        Assert.Equal((HttpStatusCode)429, (await panel.LoginRawAsync()).StatusCode);
    }

    [Fact]
    public async Task The_state_is_what_the_station_published()
    {
        using var panel = new PanelHarness();
        StationSnapshot.Publish(new StationState(DateTime.Now, "BC-10 · Склад", "Windows", "2.0", 2, 1, 0, 7,
            true, false, "", "диск", 100, 40, "", [new BaySnapshot(0, "Копирование", "5", "Иван", "", "1 из 3", 33)]));

        var (status, json) = await panel.GetAsync("/api/state");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("BC-10 · Склад", json!["station"]!.GetValue<string>());
        Assert.Equal(33, json["bays"]![0]!["percent"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("priority", StationAction.Prioritize)]
    [InlineData("charge", StationAction.ChargeOnly)]
    [InlineData("RESUME", StationAction.Resume)]
    public async Task A_bay_action_is_queued_for_the_station(string action, StationAction expected)
    {
        using var panel = new PanelHarness();
        StationCommands.Take();

        var (status, _) = await panel.PostAsync($"/api/bay/3/{action}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new StationCommand(expected, 3), Assert.Single(StationCommands.Take()));
    }

    [Fact]
    public async Task An_unknown_bay_action_is_a_bad_request()
    {
        using var panel = new PanelHarness();
        StationCommands.Take();

        Assert.Equal(HttpStatusCode.BadRequest, (await panel.PostAsync("/api/bay/1/взорвать")).Status);
        Assert.Empty(StationCommands.Take());
    }

    [Fact]
    public async Task A_restart_request_is_queued_and_logged()
    {
        using var panel = new PanelHarness();
        StationCommands.Take();

        Assert.Equal(HttpStatusCode.OK, (await panel.PostAsync("/api/restart")).Status);

        Assert.Equal(StationAction.Restart, Assert.Single(StationCommands.Take()).Action);
        Assert.Contains(new ActionLog(panel.Db).Between(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(1)),
            e => e.Message.Contains("перезапуск станции"));
    }

    [Fact]
    public async Task A_record_is_served_with_range_support()
    {
        using var panel = new PanelHarness();
        panel.AddRecord("Device1/clip.mp4", "0123456789");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file?p=Device1/clip.mp4");
        request.Headers.Add("X-Token", panel.Token);
        request.Headers.Range = new RangeHeaderValue(2, 5);
        using var response = await panel.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("2345", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_extension_is_served_as_a_download()
    {
        using var panel = new PanelHarness();
        panel.AddRecord("Device1/raw.zzz", "x");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file?p=Device1/raw.zzz");
        request.Headers.Add("X-Token", panel.Token);
        using var response = await panel.Http.SendAsync(request);

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("Device1/../../outside.txt")]
    public async Task A_path_that_leaves_the_archive_is_refused(string path)
    {
        using var panel = new PanelHarness();
        File.WriteAllText(Path.Combine(panel.Root, "outside.txt"), "секрет");

        var (status, _) = await panel.GetAsync($"/api/file?p={Uri.EscapeDataString(path)}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_missing_record_is_not_found()
    {
        using var panel = new PanelHarness();

        Assert.Equal(HttpStatusCode.NotFound, (await panel.GetAsync("/api/file?p=Device1/нет.mp4")).Status);
    }

    [Fact]
    public async Task A_ticket_opens_records_and_archives_but_nothing_else()
    {
        using var panel = new PanelHarness();
        panel.AddRecord("Device1/a.txt", "один");
        var ticket = await panel.TicketAsync();

        Assert.Equal(HttpStatusCode.OK, (await panel.GetAsync($"/api/file?p=Device1/a.txt&t={ticket}", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync($"/api/state?t={ticket}", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync("/api/file?p=Device1/a.txt&t=чужой", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.PostAsync($"/api/restart?t={ticket}", auth: false)).Status);
    }

    [Fact]
    public async Task Expired_tickets_are_dropped_when_a_new_one_is_issued()
    {
        using var panel = new PanelHarness();
        var first = await panel.TicketAsync();
        var tickets = (Dictionary<string, DateTime>)typeof(WebPanel)
            .GetField("_tickets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(panel.Panel)!;
        tickets[first] = DateTime.Now.AddMinutes(-1);

        await panel.TicketAsync();

        Assert.DoesNotContain(first, tickets.Keys);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync($"/api/file?p=a&t={first}", auth: false)).Status);
    }

    [Fact]
    public async Task An_idle_session_expires()
    {
        using var panel = new PanelHarness();
        var sessions = (Dictionary<string, DateTime>)typeof(WebPanel)
            .GetField("_sessions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(panel.Panel)!;
        sessions[panel.Token] = DateTime.Now.AddMinutes(-1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync("/api/state")).Status);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task A_failing_request_answers_with_a_phrase_and_does_not_stop_the_panel()
    {
        using var panel = new PanelHarness();
        panel.AddRecord("Device1/a.txt", "x");
        // Диск архива отключили: маркер пропал, и любое обращение к архиву падает.
        File.Delete(Path.Combine(panel.Store, ".bestcam_archive"));

        var (status, json) = await panel.PostAsync("/api/archive/delete", new { paths = new[] { "Device1/a.txt" } });

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.False(string.IsNullOrWhiteSpace(json!["error"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.OK, (await panel.GetAsync("/api/state")).Status);
    }

    [AvaloniaFact]
    public void The_page_its_styles_modules_logo_and_fonts_are_packed_into_the_program()
    {
        Assert.Contains("/js/app.js", WebPage.Html);
        Assert.Contains("--accent", WebPage.Css);
        foreach (var module in new[] { "app", "util", "net", "ui", "store", "overview", "bays", "archive", "devices", "staff", "log", "settings" })
            Assert.False(string.IsNullOrWhiteSpace(WebPage.Script($"{module}.js")), module);

        Assert.NotNull(WebPage.Font("Manrope-Bold.ttf"));
        Assert.Null(WebPage.Font("нет.ttf"));
        Assert.Null(WebPage.Script("нет.js"));

        var logo = typeof(WebPanel).GetMethod("Logo", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotEmpty((byte[])logo.Invoke(null, null)!);
        Assert.NotEmpty((byte[])logo.Invoke(null, null)!);
    }

    [Fact]
    public async Task The_page_routes_answer_without_signing_in()
    {
        using var panel = new PanelHarness(login: false);

        var html = await panel.Http.GetAsync("/");
        Assert.Equal("text/html", html.Content.Headers.ContentType!.MediaType);
        Assert.Equal("text/css", (await panel.Http.GetAsync("/app.css")).Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("/js/нет.js")]
    [InlineData("/js/app.txt")]
    [InlineData("/js/..%5Capp.js")]
    [InlineData("/fonts/нет.ttf")]
    [InlineData("/fonts/app.css")]
    public async Task Unknown_or_dangerous_asset_names_are_not_found(string url)
    {
        using var panel = new PanelHarness(login: false);

        Assert.Equal(HttpStatusCode.NotFound, (await panel.Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public void A_name_with_a_path_is_never_looked_up()
    {
        Assert.Null(WebPage.Script("app.txt"));
        Assert.Null(WebPage.Script("a/b.js"));
        Assert.Null(WebPage.Font("a/b.ttf"));
        Assert.Null(WebPage.Font("a\b.ttf"));
    }

    [Fact]
    public void The_port_falls_back_to_the_default_when_it_is_invalid()
    {
        Assert.Equal(8080, WebPanel.Port(new Settings { WebPort = 0 }));
        Assert.Equal(8080, WebPanel.Port(new Settings { WebPort = 70000 }));
        Assert.Equal(9000, WebPanel.Port(new Settings { WebPort = 9000 }));
    }

    [Fact]
    public void A_disabled_panel_does_not_start_and_a_started_one_does_not_start_twice()
    {
        using var panel = new PanelHarness();

        Assert.False(panel.Panel.Start(Settings.Load()));

        using var idle = new WebPanel(panel.Db);
        Assert.False(idle.Start(new Settings { WebEnabled = false }));
    }

    [Fact]
    public void A_busy_port_is_reported_by_a_false_and_does_not_throw()
    {
        using var panel = new PanelHarness();
        using var second = new WebPanel(panel.Db);

        Assert.False(second.Start(new Settings { WebEnabled = true, WebPort = panel.Port }));
    }

    [Fact]
    public async Task A_secured_panel_answers_over_https()
    {
        using var panel = new PanelHarness(s => s.WebSsl = true);

        Assert.StartsWith("https", panel.Http.BaseAddress!.Scheme);
        Assert.Equal(HttpStatusCode.OK, (await panel.GetAsync("/api/state")).Status);
    }
}
