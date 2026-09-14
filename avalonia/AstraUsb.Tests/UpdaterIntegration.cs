#if UPDATER_INTEGRATION
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AstraUsb.Services;

namespace AstraUsb.Tests;

// Запускается отдельной программой только в одноразовом Linux-контейнере.
public static class UpdaterIntegration
{
    public static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("UPDATER_TEST_CONTAINER") != "1")
            throw new InvalidOperationException("нужен изолированный тестовый контейнер");
        var scenario = args[0];
        var app = AppContext.BaseDirectory.TrimEnd('/');
        Environment.SetEnvironmentVariable("UPDATER_TEST_APP", app);
        AppPaths.EnsureCreated();
        File.WriteAllText(AppPaths.VersionFile, "v2.0 2026-09-01\n");
        File.WriteAllText(Path.Combine(app, "old.keep"), "old");
        File.WriteAllText(Path.Combine(AppPaths.DataDir, "recording"), "video");
        var work = Directory.CreateTempSubdirectory("update-test-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(work, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "AstraUsb"), scenario == "busy"
            ? "#!/bin/sh\ntouch \"$UPDATER_TEST_APP/data/.copying\"\n"
            : "#!/bin/sh\nexit 0\n");
        var version = scenario == "mismatch" ? "v2.8" : "v2.1";
        File.WriteAllText(Path.Combine(source, "install_native.sh"),
            $"#!/bin/sh\nprintf '{version} 2026-09-14\\n' > \"$UPDATER_TEST_APP/VERSION\"\n"
            + "echo new > \"$UPDATER_TEST_APP/old.keep\"\necho new > \"$UPDATER_TEST_APP/new.file\"\n"
            + (scenario == "restarts" ? "echo 1 > /tmp/update-restarts\n" : ""));
        var archive = Path.Combine(work, "release.tar.gz");
        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
        var bytes = File.ReadAllBytes(archive);
        var checksum = Encoding.ASCII.GetBytes(Updater.Sha256(archive));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            foreach (var body in new[] { bytes, checksum })
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(body);
            }
        });
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var install = typeof(Updater).GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (int)install.Invoke(null, [new Release("v2.1", DateTime.UtcNow,
            new Dictionary<string, string>()), new ReleaseAsset(url + "archive", url + "sum"), work])!;
        await server;
        var success = scenario == "success";
        if (result != (scenario == "busy" || success ? 0 : 1)
            || File.ReadAllText(Path.Combine(app, "old.keep")).Trim() != (success ? "new" : "old")
            || File.Exists(Path.Combine(app, "new.file")) != success
            || File.ReadAllText(Path.Combine(AppPaths.DataDir, "recording")) != "video"
            || Updater.InstalledTag() != (success ? "v2.1" : "v2.0"))
            throw new InvalidOperationException($"сценарий {scenario}: состояние станции испорчено");
        if (scenario == "busy" && Directory.Exists(app + ".prev"))
            throw new InvalidOperationException("занятая станция дошла до snapshot");
        if (scenario != "busy" && !success && !Updater.AlreadyFailed("v2.1"))
            throw new InvalidOperationException("сбойный тег не записан");
        if ((scenario == "busy" || success) && Updater.AlreadyFailed("v2.1"))
            throw new InvalidOperationException("исправный тег помечен сбойным");
        Console.WriteLine($"PASS {scenario}");
        return 0;
    }
}
#endif
