using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using AstraUsb.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class WebReviewFixTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("astra-web-review-").FullName;
    private readonly string _previous = AppPaths.Root;
    private string Root => Path.Combine(_temp, "archive");
    private string Db => Path.Combine(_temp, "devices.db");

    public WebReviewFixTests()
    {
        AppPaths.Root = _temp;
        Directory.CreateDirectory(Path.Combine(Root, "Device7", "DCIM"));
        Assert.True(ArchiveGuard.Mark(Root));
        new Settings { BackupRoot = Root }.Save();
        _ = new CollectionLog(Db);
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("mobile")]
    public async Task Archive_actions_treat_a_recording_name_as_data(string layout)
    {
        const string name = "');alert(1);('x.mp4";
        var source = Directory.CreateDirectory(Path.Combine(_temp, "source", "DCIM")).Parent!.FullName;
        File.WriteAllText(Path.Combine(source, "DCIM", name), "запись");
        var copied = FileCopier.Copy(source, Path.Combine(Root, "Device7"), "stamp");
        Assert.Equal(1, copied.CopiedFiles);
        var settings = Settings.Load();
        var backup = new BackupService(Db, settings);
        Assert.Equal(true, typeof(BackupService).GetMethod("RecordCollected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(backup, [7L, copied, DateTime.Now, settings, Root]));
        await using var server = await Start();
        var rows = await server.Client.GetStringAsync("/api/archive");
        using (var json = System.Text.Json.JsonDocument.Parse(rows))
        {
            var row = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(name, row.GetProperty("file").GetString());
            Assert.Equal("Device7/DCIM/" + name, row.GetProperty("path").GetString());
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(root.FullName, "avalonia", "AstraUsb.Tests", "web-review-fixes.js"));
        start.ArgumentList.Add(Path.Combine(root.FullName, "avalonia", "AstraUsb", "Services", "WebPage.cs"));
        start.ArgumentList.Add(layout);
        start.ArgumentList.Add(rows);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    [Fact]
    public async Task File_endpoint_refuses_a_junction_to_an_external_directory()
    {
        var external = Directory.CreateDirectory(Path.Combine(_temp, "external")).FullName;
        File.WriteAllText(Path.Combine(external, "secret.txt"), "внешний секрет");
        var link = Path.Combine(Root, "Device7", "link");
        CreateDirectoryLink(link, external);
        try
        {
            await using var server = await Start();
            using var response = await server.Client.GetAsync("/api/file?p=Device7/link/secret.txt");
            Assert.False(response.IsSuccessStatusCode, "Веб-панель отдала внешний файл через ссылку");
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("Device0/a.txt")]
    [InlineData("notes/a.txt")]
    [InlineData("Device7/.bestcam_archive")]
    public async Task File_endpoint_refuses_paths_outside_device_recordings(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "служебный файл");
        await using var server = await Start();
        using var response = await server.Client.GetAsync("/api/file?p=" + Uri.EscapeDataString(relative));
        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task File_endpoint_requires_the_archive_marker()
    {
        File.WriteAllText(Path.Combine(Root, "Device7", "a.txt"), "запись");
        File.Delete(Path.Combine(Root, Markers.Archive));
        await using var server = await Start();
        using var response = await server.Client.GetAsync("/api/file?p=Device7/a.txt");
        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task File_endpoint_serves_a_range_from_a_verified_recording()
    {
        File.WriteAllText(Path.Combine(Root, "Device7", "a.txt"), "0123456789");
        await using var server = await Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/file?p=Device7/a.txt");
        request.Headers.Range = new RangeHeaderValue(3, 6);
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("3456", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task File_endpoint_keeps_the_operation_lock_until_the_response_is_closed()
    {
        using (var file = File.Create(Path.Combine(Root, "Device7", "large.mp4"))) file.SetLength(64 * 1024 * 1024);
        await using var server = await Start();
        using (var response = await server.Client.GetAsync("/api/file?p=Device7/large.mp4", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.True(response.IsSuccessStatusCode);
            Assert.Throws<StationBusyException>(() => { using var blocked = OperationGuard.Acquire(exclusive: true, dbPath: Db); });
        }
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { using var released = OperationGuard.Acquire(exclusive: true, dbPath: Db); break; }
            catch (StationBusyException) when (DateTime.UtcNow < until) { await Task.Delay(10); }
        }
    }

    private async Task<PanelServer> Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var panel = new WebPanel(Db);
        var sessions = (Dictionary<string, DateTime>)typeof(WebPanel).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        sessions["test"] = DateTime.Now.AddMinutes(5);
        typeof(WebPanel).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [app, Settings.Load()]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-Token", "test");
        return new PanelServer(app, client);
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed record PanelServer(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); }
    }

    public void Dispose() { AppPaths.Root = _previous; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_temp, true); }
}


