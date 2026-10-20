using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AstraUsb.Tests;

internal sealed class FtpReviewServer : IAsyncDisposable
{
    private readonly TcpListener _control = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly ConcurrentDictionary<string, byte> _directories = new();
    private readonly Task _accept;
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public bool RefuseUpload { get; init; }
    public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;

    public FtpReviewServer()
    {
        _directories["/"] = 0;
        _directories["/backup"] = 0;
        _control.Start();
        _accept = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _control.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(Serve(client));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" })
        {
            var cwd = "/";
            TcpListener? data = null;
            string Resolve(string value) => (value.StartsWith('/') ? value : cwd.TrimEnd('/') + "/" + value).TrimEnd('/') is { Length: > 0 } path ? path : "/";
            try
            {
                await writer.WriteLineAsync("220 Local FTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var parts = line.Split(' ', 2);
                    var argument = parts.Length > 1 ? parts[1] : "";
                    var path = Resolve(argument);
                    switch (parts[0])
                    {
                        case "USER": await writer.WriteLineAsync("331 Password"); break;
                        case "PASS": await writer.WriteLineAsync("230 Logged in"); break;
                        case "OPTS": case "TYPE": await writer.WriteLineAsync("200 OK"); break;
                        case "PWD": await writer.WriteLineAsync($"257 \"{cwd}\""); break;
                        case "CWD":
                            if (_directories.ContainsKey(path)) { cwd = path; await writer.WriteLineAsync("250 Directory changed"); }
                            else await writer.WriteLineAsync("550 Directory missing");
                            break;
                        case "MKD":
                            var parent = path[..path.LastIndexOf('/')];
                            if (parent.Length == 0) parent = "/";
                            if (_directories.ContainsKey(parent) && _directories.TryAdd(path, 0)) await writer.WriteLineAsync("257 Directory created");
                            else await writer.WriteLineAsync("550 Cannot create directory");
                            break;
                        case "PASV":
                            data?.Stop();
                            data = new TcpListener(IPAddress.Loopback, 0); data.Start();
                            var port = ((IPEndPoint)data.LocalEndpoint).Port;
                            await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                            break;
                        case "STOR":
                            var folder = path[..path.LastIndexOf('/')];
                            if (folder.Length == 0) folder = "/";
                            if (RefuseUpload || !_directories.ContainsKey(folder)) { await writer.WriteLineAsync("550 Cannot store file"); break; }
                            await writer.WriteLineAsync("150 Opening data connection");
                            using (var transfer = await data!.AcceptTcpClientAsync(_stop.Token))
                            using (var bytes = new MemoryStream())
                            {
                                await transfer.GetStream().CopyToAsync(bytes, _stop.Token);
                                Files[path] = bytes.ToArray();
                            }
                            await writer.WriteLineAsync("226 Transfer complete");
                            break;
                        case "QUIT": await writer.WriteLineAsync("221 Bye"); return;
                        default: await writer.WriteLineAsync("502 Unsupported command"); break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { data?.Stop(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _control.Stop();
        await _accept;
        await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }
}
