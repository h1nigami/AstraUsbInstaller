using System.IO.Compression;
using System.Net;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Разделы веб-панели за входом: архив, камеры, сотрудники, журнал и настройки.
/// Каждый вызов идёт по HTTP в настоящую панель с настоящей базой и архивом.
/// </summary>
[Collection("Каталог данных")]
public sealed class WebApiEndpointTests
{
    // --- Архив ---------------------------------------------------------------

    private static PanelHarness Seeded()
    {
        var panel = new PanelHarness();
        var staff = new StaffDirectory(panel.Db);
        var ivan = staff.AddEmployee("Иван Петров", "101");
        var device1 = panel.AddDevice("S1", "Охрана-1", "BCU-01");
        var device2 = panel.AddDevice("S2");
        staff.AssignDevice(device1, ivan);
        panel.AddRecord("Device1/VID_1.mp4", "видео", device1, DateTime.Now.AddHours(-1), important: true);
        panel.AddRecord("Device1/IMG_2.jpg", "фото", device1, DateTime.Now.AddHours(-2), note: "инцидент");
        panel.AddRecord("Device2/REC_3.wav", "звук", device2, DateTime.Now.AddDays(-3));
        panel.AddRecord("Device2/LOG_4.txt", "журнал", device2, DateTime.Now.AddDays(-20), shot: DateTime.Now.AddYears(-30));
        return panel;
    }

    private static string[] Files(System.Text.Json.Nodes.JsonNode? json) =>
        json!["rows"]!.AsArray().Select(r => r!["file"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Search_shows_files_changed_in_the_last_week_by_default_with_owner_and_flags()
    {
        using var panel = Seeded();

        var (status, json) = await panel.GetAsync("/api/search");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(500, json!["limit"]!.GetValue<int>());
        Assert.Equal(["VID_1.mp4", "IMG_2.jpg", "REC_3.wav", "LOG_4.txt"], Files(json));
        var video = json["rows"]![0]!;
        Assert.Equal("Видео", video["kind"]!.GetValue<string>());
        Assert.Equal("Охрана-1", video["camera"]!.GetValue<string>());
        Assert.Equal("Иван Петров", video["employee"]!.GetValue<string>());
        Assert.True(video["important"]!.GetValue<bool>());
        Assert.Equal("инцидент", json["rows"]![1]!["note"]!.GetValue<string>());
        Assert.Equal("Device1/VID_1.mp4", video["path"]!.GetValue<string>());
        Assert.Contains("mp4", video["formats"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Theory]
    [InlineData("kind=Photo", "IMG_2.jpg")]
    [InlineData("kind=Audio", "REC_3.wav")]
    [InlineData("name=VID", "VID_1.mp4")]
    [InlineData("protected=1", "VID_1.mp4")]
    [InlineData("employee=Иван", "VID_1.mp4,IMG_2.jpg")]
    [InlineData("no=101", "VID_1.mp4,IMG_2.jpg")]
    [InlineData("camera=Охрана", "VID_1.mp4,IMG_2.jpg")]
    [InlineData("camera=BCU-01", "VID_1.mp4,IMG_2.jpg")]
    [InlineData("camera=нет-такой", "")]
    [InlineData("kind=чепуха", "VID_1.mp4,IMG_2.jpg,REC_3.wav,LOG_4.txt")]
    public async Task Search_narrows_by_each_condition(string query, string expected)
    {
        using var panel = Seeded();

        var (_, json) = await panel.GetAsync("/api/search?" + query);

        Assert.Equal(expected, string.Join(",", Files(json)));
    }

    [Fact]
    public async Task Search_takes_an_explicit_period_and_marks_a_wrong_camera_clock()
    {
        using var panel = Seeded();
        var from = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd");
        var to = DateTime.Now.ToString("yyyy-MM-dd");

        var (_, json) = await panel.GetAsync($"/api/search?from={from}&to={to}&kind=Log");

        var log = Assert.Single(json!["rows"]!.AsArray())!;
        Assert.Equal("LOG_4.txt", log["file"]!.GetValue<string>());
        Assert.Equal("off", log["shot"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_by_department_includes_its_people()
    {
        using var panel = Seeded();
        var staff = new StaffDirectory(panel.Db);
        var department = staff.AddDepartment("Охрана");
        var person = staff.Employees().Single();
        staff.UpdateEmployee(person with { DepartmentId = department });

        var (_, list) = await panel.GetAsync("/api/departments");
        var (_, json) = await panel.GetAsync($"/api/search?department={department}");

        Assert.Equal("Охрана", list![0]!["name"]!.GetValue<string>());
        Assert.Equal(["VID_1.mp4", "IMG_2.jpg"], Files(json));
    }

    [Fact]
    public async Task Protecting_and_unprotecting_records_is_remembered()
    {
        using var panel = Seeded();
        var paths = new[] { "Device1/IMG_2.jpg", "Device2/REC_3.wav" };

        var (status, json) = await panel.PostAsync("/api/archive/protect", new { paths, on = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, json!["count"]!.GetValue<int>());
        Assert.Equal(3, (await panel.GetAsync("/api/search?protected=1")).Json!["rows"]!.AsArray().Count);

        await panel.PostAsync("/api/archive/protect", new { paths, on = false });
        Assert.Single((await panel.GetAsync("/api/search?protected=1")).Json!["rows"]!.AsArray());
    }

    [Fact]
    public async Task A_note_is_saved_and_can_be_cleared()
    {
        using var panel = Seeded();
        var paths = new[] { "Device2/REC_3.wav" };

        await panel.PostAsync("/api/archive/note", new { paths, note = "  по заявке  " });
        Assert.Equal("по заявке", (await panel.GetAsync("/api/search?kind=Audio")).Json!["rows"]![0]!["note"]!.GetValue<string>());

        await panel.PostAsync("/api/archive/note", new { paths });
        Assert.Equal("", (await panel.GetAsync("/api/search?kind=Audio")).Json!["rows"]![0]!["note"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleting_removes_files_but_skips_protected_ones()
    {
        using var panel = Seeded();
        var paths = new[] { "Device1/VID_1.mp4", "Device1/IMG_2.jpg" };

        var (status, json) = await panel.PostAsync("/api/archive/delete", new { paths });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, json!["deleted"]!.GetValue<int>());
        Assert.Equal(1, json["skipped"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(panel.Store, "Device1", "VID_1.mp4")));
        Assert.False(File.Exists(Path.Combine(panel.Store, "Device1", "IMG_2.jpg")));
    }

    [Fact]
    public async Task Converting_a_missing_record_is_refused_and_a_wrong_format_is_explained()
    {
        using var panel = Seeded();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await panel.PostAsync("/api/archive/convert", new { paths = new[] { "нет.mp4" }, format = "mp4" })).Status);

        var (status, json) = await panel.PostAsync("/api/archive/convert",
            new { paths = new[] { "Device2/LOG_4.txt" }, format = "mp4" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(json["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_pack_downloads_as_one_zip_with_unique_names()
    {
        using var panel = Seeded();
        panel.AddRecord("Device2/VID_1.mp4", "второй с тем же именем");
        var ticket = await panel.TicketAsync();
        var (_, pack) = await panel.PostAsync("/api/archive/pack", new
        {
            paths = new[] { "Device1/VID_1.mp4", "Device2/VID_1.mp4", "Device2/REC_3.wav", "../чужой.txt", "Device1/нет.txt" },
        });

        var bytes = await panel.Http.GetByteArrayAsync($"/api/zip?id={pack!["id"]!.GetValue<string>()}&t={ticket}");

        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["REC_3.wav", "VID_1 (2).mp4", "VID_1.mp4"], zip.Entries.Select(e => e.Name).Order().ToArray());
    }

    [Fact]
    public async Task A_pack_that_does_not_exist_or_holds_nothing_is_not_found()
    {
        using var panel = Seeded();
        var ticket = await panel.TicketAsync();
        var (_, empty) = await panel.PostAsync("/api/archive/pack", new { });

        Assert.Equal(HttpStatusCode.NotFound, (await panel.GetAsync($"/api/zip?id=нет&t={ticket}", auth: false)).Status);
        Assert.Equal(HttpStatusCode.NotFound,
            (await panel.GetAsync($"/api/zip?id={empty!["id"]!.GetValue<string>()}&t={ticket}", auth: false)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.GetAsync("/api/zip?id=нет", auth: false)).Status);
    }

    // --- Камеры ----------------------------------------------------------------

    [Fact]
    public async Task Devices_are_listed_with_their_owner_and_the_people_to_choose_from()
    {
        using var panel = Seeded();

        var (_, json) = await panel.GetAsync("/api/devices");

        var first = json!["devices"]![0]!;
        Assert.Equal("Охрана-1", first["name"]!.GetValue<string>());
        Assert.Equal("Иван Петров", first["employee"]!.GetValue<string>());
        Assert.Equal(2, json["devices"]!.AsArray().Count);
        Assert.Equal("101", json["staff"]![0]!["no"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_device_is_renamed_reassigned_and_released()
    {
        using var panel = Seeded();
        var staff = new StaffDirectory(panel.Db);
        var maria = staff.AddEmployee("Мария Сидорова", "102");

        await panel.PostAsync("/api/devices/2/name", new { name = "  Склад  " });
        await panel.PostAsync("/api/devices/2/employee", new { employeeId = maria });
        var second = (await panel.GetAsync("/api/devices")).Json!["devices"]![1]!;
        Assert.Equal("Склад", second["name"]!.GetValue<string>());
        Assert.Equal("Мария Сидорова", second["employee"]!.GetValue<string>());

        await panel.PostAsync("/api/devices/2/name", new { });
        await panel.PostAsync("/api/devices/2/employee", new { });
        second = (await panel.GetAsync("/api/devices")).Json!["devices"]![1]!;
        Assert.Equal("", second["name"]!.GetValue<string>());
        Assert.Equal("", second["employee"]!.GetValue<string>());
    }

    // --- Сотрудники -------------------------------------------------------------

    [Fact]
    public async Task Staff_cards_are_added_changed_and_deleted()
    {
        using var panel = new PanelHarness();

        Assert.Equal(HttpStatusCode.OK, (await panel.PostAsync("/api/staff", new { no = "7", name = "Пётр", phone = " 123 " })).Status);
        var card = (await panel.GetAsync("/api/staff")).Json![0]!;
        Assert.Equal("Пётр", card["name"]!.GetValue<string>());
        Assert.Equal("123", card["phone"]!.GetValue<string>());

        var id = card["id"]!.GetValue<long>();
        await panel.PostAsync("/api/staff", new { id, no = "8", name = "" });
        card = (await panel.GetAsync("/api/staff")).Json![0]!;
        Assert.Equal("8", card["no"]!.GetValue<string>());
        Assert.Equal("8", card["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await panel.SendAsync(HttpMethod.Delete, $"/api/staff/{id}")).Status);
        Assert.Empty((await panel.GetAsync("/api/staff")).Json!.AsArray());
    }

    [Fact]
    public async Task A_card_without_a_number_or_with_a_taken_number_is_refused()
    {
        using var panel = new PanelHarness();
        await panel.PostAsync("/api/staff", new { no = "7", name = "Пётр" });

        var (noNumber, a) = await panel.PostAsync("/api/staff", new { name = "Без номера" });
        var (taken, b) = await panel.PostAsync("/api/staff", new { no = "7", name = "Второй" });

        Assert.Equal(HttpStatusCode.BadRequest, noNumber);
        Assert.Contains("номер", a!["error"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, taken);
        Assert.Contains("уже занят", b!["error"]!.GetValue<string>());
    }

    // --- Журнал ------------------------------------------------------------------

    [Fact]
    public async Task The_log_is_filtered_by_period()
    {
        using var panel = new PanelHarness();
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        var (_, now) = await panel.GetAsync($"/api/events?from={today}&to={today}");
        var (_, none) = await panel.GetAsync("/api/events?from=2001-01-01&to=2001-01-02");
        var (_, byDefault) = await panel.GetAsync("/api/events");

        Assert.NotEmpty(now!.AsArray());
        Assert.Empty(none!.AsArray());
        Assert.NotEmpty(byDefault!.AsArray());
        Assert.Equal("доступ", now[0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Diagnostics_arrive_as_a_zip_without_secrets()
    {
        using var panel = new PanelHarness(s => s.FtpPassword = "супер-секрет-ftp");
        await panel.PostAsync("/api/restart");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/diagnostics");
        request.Headers.Add("X-Token", panel.Token);
        using var response = await panel.Http.SendAsync(request);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());

        Assert.Equal(["app.log", "summary.txt", "version.txt"], zip.Entries.Select(e => e.Name).Order().ToArray());
        using var log = new StreamReader(zip.GetEntry("app.log")!.Open());
        Assert.DoesNotContain("супер-секрет-ftp", await log.ReadToEndAsync());
    }

    // --- Настройки ----------------------------------------------------------------

    [Fact]
    public async Task Settings_are_shown_without_passwords()
    {
        using var panel = new PanelHarness(s =>
        {
            s.FtpPassword = "ftp-pass";
            s.SqlPassword = "sql-pass";
            s.FtpEnabled = true;
        });

        var (status, json) = await panel.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("ftp-pass", json!.ToJsonString());
        Assert.DoesNotContain("sql-pass", json.ToJsonString());
        Assert.True(json["ftp"]!["hasPassword"]!.GetValue<bool>());
        Assert.Equal("admin", json["account"]!["name"]!.GetValue<string>());
        Assert.True(json["account"]!["defaultPassword"]!.GetValue<bool>());
        Assert.StartsWith("в очереди", json["ftp"]!["queue"]!.GetValue<string>());
        Assert.Equal(panel.Port, json["web"]!["port"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_storage_settings_are_saved_and_clamped()
    {
        using var panel = new PanelHarness();

        var (status, _) = await panel.PostAsync("/api/settings/storage", new
        {
            backupRoot = panel.Store, minFreeGb = 0, stationNumber = 500, stationPlace = " Склад ",
            storageMode = 1, deleteVideoAfterCopy = true, keepDays = 99999, alarmSound = false, voiceHints = true,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var saved = Settings.Load();
        Assert.Equal(1, saved.MinFreeGb);
        Assert.Equal(99, saved.StationNumber);
        Assert.Equal("Склад", saved.StationPlace);
        Assert.Equal(StorageMode.Overwrite, saved.StorageMode);
        Assert.Equal(3650, saved.KeepDays);
        Assert.True(saved.DeleteVideoAfterCopy);
        Assert.False(saved.AlarmSound);
        Assert.True(saved.VoiceHints);
    }

    [Fact]
    public async Task An_archive_folder_that_cannot_be_chosen_is_refused()
    {
        using var panel = new PanelHarness();
        var blocked = Path.Combine(panel.Root, "файл");
        File.WriteAllText(blocked, "не папка");

        var (status, json) = await panel.PostAsync("/api/settings/storage",
            new { backupRoot = Path.Combine(blocked, "внутри"), minFreeGb = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("папку архива", json!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bays_and_the_lock_time_are_saved_within_limits()
    {
        using var panel = new PanelHarness();

        await panel.PostAsync("/api/settings/bays", new { bayCount = 99, baysPerRow = 1 });
        await panel.PostAsync("/api/settings/lock", new { minutes = 999 });

        var saved = Settings.Load();
        Assert.Equal(30, saved.BayCount);
        Assert.Equal(2, saved.BaysPerRow);
        Assert.Equal(240, saved.LockTimeoutMinutes);
    }

    [Theory]
    [InlineData("не тот", "новый-пароль", "новый-пароль", "admin", "текущий пароль")]
    [InlineData(null, "123", "123", "admin", "короче четырёх")]
    [InlineData(null, "новый-пароль", "другой", "admin", "не совпали")]
    [InlineData(null, "новый-пароль", "новый-пароль", "  ", "имя учётной записи")]
    public async Task A_bad_password_change_is_explained_and_changes_nothing(
        string? current, string next, string repeat, string account, string reason)
    {
        using var panel = new PanelHarness();

        var (status, json) = await panel.PostAsync("/api/settings/password",
            new { account, current = current ?? PanelHarness.Password, next, repeat });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, json!["error"]!.GetValue<string>());
        Assert.Equal("", Settings.Load().PasswordHash);
    }

    [Fact]
    public async Task A_changed_password_signs_in_under_the_new_account()
    {
        using var panel = new PanelHarness();

        var (status, _) = await panel.PostAsync("/api/settings/password",
            new { account = "оператор", current = PanelHarness.Password, next = "новый-пароль", repeat = "новый-пароль" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await panel.LoginRawAsync("admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await panel.LoginRawAsync("оператор", "новый-пароль")).StatusCode);
    }

    [Fact]
    public async Task Server_settings_keep_the_old_password_when_the_field_is_left_empty()
    {
        using var panel = new PanelHarness(s => { s.FtpPassword = "старый"; s.SqlPassword = "старый-sql"; });

        await panel.PostAsync("/api/settings/ftp", new { enabled = true, host = " ftp.local ", port = 0, user = "u", folder = "/in", ssl = true });
        await panel.PostAsync("/api/settings/sql", new { enabled = true, host = "db.local", port = 99999, database = "d", user = "u" });

        var saved = Settings.Load();
        Assert.Equal("старый", saved.FtpPassword);
        Assert.Equal("ftp.local", saved.FtpHost);
        Assert.Equal(21, saved.FtpPort);
        Assert.True(saved.FtpSsl);
        Assert.Equal("старый-sql", saved.SqlPassword);
        Assert.Equal(3306, saved.SqlPort);

        await panel.PostAsync("/api/settings/ftp", new { enabled = false, password = "новый" });
        await panel.PostAsync("/api/settings/sql", new { enabled = false, password = "новый-sql" });
        saved = Settings.Load();
        Assert.Equal("новый", saved.FtpPassword);
        Assert.Equal("новый-sql", saved.SqlPassword);
        Assert.False(saved.FtpEnabled);
    }

    [Fact]
    public async Task Checking_the_server_without_an_address_says_what_is_missing()
    {
        using var panel = new PanelHarness();

        var (_, ftp) = await panel.PostAsync("/api/ftp/test", new { host = "", password = "x" });
        var (_, sql) = await panel.PostAsync("/api/sql/test", new { host = "", port = 3306 });

        Assert.False(ftp!["ok"]!.GetValue<bool>());
        Assert.False(sql!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(sql["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Checking_a_database_of_an_unsupported_kind_is_refused_with_a_reason()
    {
        using var panel = new PanelHarness(s => s.SqlKind = "Oracle");

        var (_, sql) = await panel.PostAsync("/api/sql/test", new { host = "x", port = 1 });

        Assert.False(sql!["ok"]!.GetValue<bool>());
        Assert.Contains("Oracle", sql["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Retrying_postponed_files_reports_the_queue()
    {
        using var panel = new PanelHarness();

        var (_, json) = await panel.PostAsync("/api/ftp/retry");

        Assert.Equal("отложенных нет, в очереди 0", json!["message"]!.GetValue<string>());

        var queue = new FtpQueue(panel.Db);
        queue.Add("a.mp4");
        for (var i = 0; i < FtpQueue.MaxAttempts; i++)
            queue.Failed(queue.Next(1)[0].Id, "сбой");
        (_, json) = await panel.PostAsync("/api/ftp/retry");
        Assert.StartsWith("возвращено в очередь: 1", json!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Syncing_with_nothing_to_send_says_so()
    {
        using var panel = new PanelHarness();

        var (_, json) = await panel.PostAsync("/api/sql/sync");

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("отправлять нечего", json["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_update_check_waits_while_the_station_is_busy()
    {
        using var panel = new PanelHarness();

        var (_, idle) = await panel.PostAsync("/api/update/check");
        Assert.True(idle!["ok"]!.GetValue<bool>());

        BusyMarker.Touch();
        var (_, busy) = await panel.PostAsync("/api/update/check");
        Assert.False(busy!["ok"]!.GetValue<bool>());
        Assert.Contains("дождитесь", busy["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_factory_reset_needs_the_password_and_then_clears_the_station()
    {
        using var panel = Seeded();
        StationCommands.Take();

        var (wrong, json) = await panel.PostAsync("/api/factory-reset", new { password = "не тот" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong);
        Assert.Contains("пароль", json!["error"]!.GetValue<string>());
        Assert.Empty((await panel.GetAsync("/api/search")).Json!["rows"]!.AsArray().Take(0));
        Assert.NotEmpty((await panel.GetAsync("/api/search")).Json!["rows"]!.AsArray());

        var (status, done) = await panel.PostAsync("/api/factory-reset", new { password = PanelHarness.Password });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("удалено", done!["message"]!.GetValue<string>());
        Assert.Empty((await panel.GetAsync("/api/search")).Json!["rows"]!.AsArray());
        Assert.Equal(StationAction.Restart, Assert.Single(StationCommands.Take()).Action);
    }

    [Fact]
    public async Task A_factory_reset_is_refused_while_the_station_is_working()
    {
        using var panel = Seeded();
        BusyMarker.Touch();

        var (status, json) = await panel.PostAsync("/api/factory-reset", new { password = PanelHarness.Password });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("идёт", json!["error"]!.GetValue<string>());
        File.Delete(BusyMarker.FilePath);
    }
}
