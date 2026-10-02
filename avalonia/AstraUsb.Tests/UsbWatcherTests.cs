using System.Diagnostics;
using System.Reflection;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class UsbWatcherTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_mount_that_succeeded_despite_a_command_error_is_kept_and_not_mounted_twice(
        bool fallback, bool cancel)
    {
        var root = Directory.CreateTempSubdirectory("astra-mount-").FullName;
        try
        {
            using var stop = new CancellationTokenSource();
            var calls = 0;
            string? liveMount = null;
            var target = Path.Combine(root, "sdb1");
            var method = typeof(MountManager).GetMethod("MountOurselves", BindingFlags.Static | BindingFlags.NonPublic)!;
            var run = new Func<string, string[], CancellationToken, bool>((program, arguments, token) =>
            {
                Assert.Equal("mount", program);
                calls++;
                if (calls == (fallback ? 2 : 1))
                {
                    liveMount = target;
                    if (cancel) stop.Cancel();
                }
                return false;
            });

            var mounted = (Mounted?)method.Invoke(null,
                ["sdb1", stop.Token, run, new Func<string, string?>(_ => liveMount), root]);

            Assert.NotNull(mounted);
            Assert.Equal(target, mounted.Path);
            Assert.True(mounted.Ours);
            Assert.Equal(fallback ? 2 : 1, calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_cancelled_mount_without_a_live_mount_does_not_try_a_second_command()
    {
        var root = Directory.CreateTempSubdirectory("astra-mount-").FullName;
        try
        {
            using var stop = new CancellationTokenSource();
            var calls = 0;
            var method = typeof(MountManager).GetMethod("MountOurselves", BindingFlags.Static | BindingFlags.NonPublic)!;
            var run = new Func<string, string[], CancellationToken, bool>((_, _, _) =>
            {
                calls++;
                stop.Cancel();
                return false;
            });

            var mounted = (Mounted?)method.Invoke(null,
                ["sdb1", stop.Token, run, new Func<string, string?>(_ => null), root]);

            Assert.Null(mounted);
            Assert.Equal(1, calls);
            Assert.False(Directory.Exists(Path.Combine(root, "sdb1")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"blockdevices\":null}")]
    [InlineData("{\"blockdevices\":[null]}")]
    public void An_invalid_lsblk_shape_falls_back_to_sysfs(string json)
    {
        var root = Directory.CreateTempSubdirectory("astra-usb-").FullName;
        try
        {
            var sys = Directory.CreateDirectory(Path.Combine(root, "usb1", "block")).FullName;
            var part = Directory.CreateDirectory(Path.Combine(sys, "sdb1")).FullName;
            File.WriteAllText(Path.Combine(part, "partition"), "1");
            var mounts = Path.Combine(root, "mounts");
            File.WriteAllText(mounts, "/dev/sdb1 /media/CAM vfat rw 0 0");
            var list = typeof(UsbWatcher).GetMethod("ListLinux", BindingFlags.Static | BindingFlags.NonPublic)!;

            var found = (IReadOnlyList<UsbDevice>)list.Invoke(null,
                [sys, Path.Combine(root, "uuid"), mounts, new Func<string?>(() => json)])!;

            var card = Assert.Single(found);
            Assert.Equal("sdb1", card.Name);
            Assert.Equal("/media/CAM", card.MountPoint);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Failed_lsblk_falls_back_to_sysfs_and_keeps_partition_identity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("astra-usb-").FullName;
        try
        {
            var sys = Directory.CreateDirectory(Path.Combine(root, "block")).FullName;
            var disk = Directory.CreateDirectory(Path.Combine(root, "devices", "usb1", "sdb")).FullName;
            Directory.CreateDirectory(Path.Combine(disk, "device"));
            File.WriteAllText(Path.Combine(disk, "device", "serial"), "SERIAL");
            var part = Directory.CreateDirectory(Path.Combine(disk, "sdb1")).FullName;
            File.WriteAllText(Path.Combine(part, "partition"), "1");
            Directory.CreateSymbolicLink(Path.Combine(sys, "sdb"), disk);
            Directory.CreateSymbolicLink(Path.Combine(sys, "sdb1"), part);
            var uuids = Directory.CreateDirectory(Path.Combine(root, "uuid")).FullName;
            File.CreateSymbolicLink(Path.Combine(uuids, "CARD-UUID"), "/dev/sdb1");
            var mounts = Path.Combine(root, "mounts");
            File.WriteAllText(mounts, "/dev/sdb1 /media/CAM vfat rw 0 0");
            var list = typeof(UsbWatcher).GetMethod("ListLinux", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(4, list.GetParameters().Length);

            var found = (IReadOnlyList<UsbDevice>)list.Invoke(null, [sys, uuids, mounts, new Func<string?>(() => null)])!;

            var card = Assert.Single(found);
            Assert.Equal("sdb1", card.Name);
            Assert.Equal("/media/CAM", card.MountPoint);
            Assert.Equal("CARD-UUID", typeof(UsbDevice).GetProperty("FileSystemUuid")!.GetValue(card));
            Assert.Equal("SERIAL", typeof(UsbDevice).GetProperty("Serial")!.GetValue(card));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Partitions_keep_their_uuid_and_the_parent_serial()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {"name":"sdb","tran":"usb","serial":"CAM-USB","children":[
              {"name":"sdb1","mountpoint":"/media/CAM","uuid":"CARD-1"},
              {"name":"sdb2","mountpoint":null,"uuid":"CARD-2"}]}
            """);
        var found = new List<UsbDevice>();
        typeof(UsbWatcher).GetMethod("CollectFromDisk", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [json.RootElement, found]);

        Assert.Equal(2, found.Count);
        Assert.Equal("CARD-1", typeof(UsbDevice).GetProperty("FileSystemUuid")?.GetValue(found[0]));
        Assert.Equal("CAM-USB", typeof(UsbDevice).GetProperty("Serial")?.GetValue(found[1]));
    }

    [Fact]
    public async Task A_process_without_eof_is_killed_at_the_timeout()
    {
        var run = typeof(UsbWatcher).GetMethod("RunProcess", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(run);
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "ping -n 20 127.0.0.1 >nul" : "sleep 20");
        var watch = Stopwatch.StartNew();

        var result = await Task.Run(() => run.Invoke(null, [info, 100, CancellationToken.None]))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_foreign_mount_is_not_reported_as_released()
    {
        var released = typeof(MountManager).GetMethod(nameof(MountManager.Release))!
            .Invoke(null, [new Mounted("/media/CAM", false)]);

        Assert.Equal(false, released);
    }
}
