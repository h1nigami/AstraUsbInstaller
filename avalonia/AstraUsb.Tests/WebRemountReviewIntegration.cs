#if WEB_REMOUNT_INTEGRATION
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text;
using AstraUsb.Services;
using AstraUsb.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var temp = Directory.CreateTempSubdirectory("astra-web-remount-").FullName;
var old = Path.Combine(temp, "old", "archive");
var live = Path.Combine(temp, "live", "archive");
var db = Path.Combine(temp, "devices.db");
AppPaths.Root = temp;
Environment.SetEnvironmentVariable("ASTRA_WEB_FIXTURE_LIVE", live);
Directory.CreateDirectory(Path.Combine(old, "Device7", "DCIM"));
File.WriteAllText(Path.Combine(old, "Device7", "DCIM", "old.mp4"), "старая запись");
if (!ArchiveGuard.Mark(old)) throw new Exception("Не создан маркер тестового архива");
var settings = new Settings { BackupRoot = old, BackupUuid = "fixture", BackupRelativePath = "archive" };
settings.Save();
var log = new CollectionLog(db);
var oldKey = Path.Combine(old, "Device7", "DCIM", "old.mp4");
log.Record([new CollectedFile(7, oldKey, 10, null, DateTime.Now.AddMinutes(-1))]);
Directory.CreateDirectory(Path.GetDirectoryName(live)!);
Directory.Move(old, live);
var newKey = Path.Combine(live, "Device7", "DCIM", "new.mp4");
File.WriteAllText(newKey, "новая запись");
log.Record([new CollectedFile(7, newKey, 10, null, DateTime.Now)]);

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
await using var app = builder.Build();
using var panel = new WebPanel(db);
var sessions = (Dictionary<string, DateTime>)typeof(WebPanel).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
sessions["local"] = DateTime.Now.AddMinutes(5);
typeof(WebPanel).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [app, settings]);
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.Add("X-Token", "local");
var failures = 0;
async Task Check(string name, Func<Task> check)
{
    try { await check(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
}
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
try
{
    await Check("ftp-queue-survives-detach-and-remount", () =>
    {
        var queue = new FtpQueue(db);
        queue.Add(oldKey);
        var detached = Path.Combine(temp, "detached");
        Directory.Move(live, detached);
        try
        {
            Assert(queue.Prune(settings) == 0 && queue.Count() == 1, "Очередь потеряна при отключении архива");
        }
        finally { Directory.Move(detached, live); }
        Assert(queue.Prune(settings) == 0 && queue.Count() == 1, "Очередь потеряна после перемонтирования");
        var item = queue.Next().Single();
        Assert(item.Path == oldKey && item.Attempts == 0, "Изменён ключ очереди или израсходована попытка");
        return Task.CompletedTask;
    });
    await Check("archive-links-use-live-row-paths", async () =>
    {
        using var rows = JsonDocument.Parse(await client.GetStringAsync("/api/archive"));
        Assert(rows.RootElement.GetArrayLength() == 2, "Ожидались обе записи после перемонтирования");
        foreach (var row in rows.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("file").GetString();
            var path = row.GetProperty("path").GetString();
            Assert(path == "Device7/DCIM/" + name, $"Некорректная ссылка на {name}: {path}");
            using var download = await client.GetAsync("/api/file?p=" + Uri.EscapeDataString(path!));
            Assert(download.IsSuccessStatusCode, $"Ссылка на {name} не скачивается: {download.StatusCode}");
        }
    });
    await Check("ftp-sends-saved-key-after-remount", async () =>
    {
        await using var server = new FtpReviewServer();
        settings.FtpHost = "127.0.0.1";
        settings.FtpPort = server.Port;
        settings.FtpFolder = "backup";
        var result = FtpSender.Send(settings, oldKey);
        Assert(result.Ok, "Не отправлена запись по сохранённому пути: " + result.Message);
        Assert(server.Files.TryGetValue("/backup/Device7/DCIM/old.mp4", out var bytes)
            && Encoding.UTF8.GetString(bytes) == "старая запись", "FTP получил другой путь или содержимое");
        var queue = new FtpQueue(db);
        queue.Done(queue.Next().Single().Id);
        Assert(queue.Count() == 0, "Успешная отправка осталась в очереди");
    });
    await Check("saved-key-download-uses-live-root", async () =>
    {
        var content = await client.GetStringAsync("/api/file?p=Device7/DCIM/old.mp4");
        Assert(content == "старая запись", "Прочитана другая запись");
        Assert(log.CollectedBetween(DateTime.MinValue, DateTime.MaxValue).Any(file => file.DestPath == oldKey),
            "Потерян сохранённый ключ журнала");
    });
    await Check("service-marker-is-refused", async () =>
    {
        settings.BackupRoot = live;
        settings.Save();
        File.Copy(Path.Combine(live, Markers.Archive), Path.Combine(live, "Device7", Markers.Archive));
        using var response = await client.GetAsync("/api/file?p=Device7/.bestcam_archive");
        Assert(!response.IsSuccessStatusCode, "Служебная метка отдана как запись");
    });
    if (OperatingSystem.IsLinux())
        await Check("case-different-sibling-is-refused", async () =>
        {
            settings.BackupRoot = live;
            settings.Save();
            var sibling = Path.Combine(Path.GetDirectoryName(live)!, "Archive", "Device7");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "внешний секрет");
            using var response = await client.GetAsync("/api/file?p=" + Uri.EscapeDataString("../Archive/Device7/secret.txt"));
            Assert(!response.IsSuccessStatusCode, "Отдан файл соседнего каталога с другим регистром");
        });
}
finally
{
    await app.StopAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(temp, true);
}
Console.WriteLine($"RESULT {(failures == 0 ? "PASS" : "FAIL")} failures={failures}");
return failures == 0 ? 0 : 1;
#endif
