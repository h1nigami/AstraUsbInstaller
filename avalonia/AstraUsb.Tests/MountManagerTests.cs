using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class MountManagerTests
{
    [Fact]
    public void An_unreadable_mount_table_does_not_look_like_an_unmounted_device()
    {
        var missing = Path.Combine(Path.GetTempPath(), "astra-missing-mounts-" + Guid.NewGuid());

        var error = Assert.ThrowsAny<IOException>(() => Invoke<IReadOnlyList<MountEntry>>("Current", missing));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public void A_disconnected_own_mount_is_released_lazily_only_after_normal_unmount_fails()
    {
        using var mounts = new MountFixture();
        var target = mounts.Add("/dev/sdb1", "sdb1");
        var calls = new List<string[]>();
        var run = new Func<string, string[], CancellationToken, bool>((program, arguments, _) =>
        {
            Assert.Equal("umount", program);
            calls.Add(arguments);
            if (arguments.Length == 1)
                throw new IOException("umount: device is busy");
            mounts.Remove(target);
            return true;
        });

        Assert.True(mounts.Release(target, run, _ => false));

        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { target }, calls[0]);
        Assert.Equal(new[] { "-l", target }, calls[1]);
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("/dev/sdb1", true)]
    [InlineData("server:/cam", false)]
    [InlineData("tmpfs", false)]
    public void A_live_or_unknown_source_is_never_released_lazily(string source, bool exists)
    {
        using var mounts = new MountFixture();
        var target = mounts.Add(source, "sdb1");
        var calls = new List<string[]>();

        Assert.False(mounts.Release(target, (_, arguments, _) => { calls.Add(arguments); return false; }, _ => exists));

        Assert.Equal(new[] { target }, Assert.Single(calls));
        Assert.True(Directory.Exists(target));
        Assert.Contains(target, MountTable.Parse(File.ReadAllLines(mounts.Table)).Select(m => m.MountPoint));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_foreign_mount_is_never_unmounted_even_when_its_ownership_flag_is_wrong(bool ours, bool outside)
    {
        using var mounts = new MountFixture();
        var target = mounts.Add("/dev/sdb1", "sdb1", outside);
        var calls = 0;

        Assert.False(mounts.Release(target, (_, _, _) => { calls++; return true; }, _ => false, ours));

        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void A_mount_below_a_directory_link_outside_our_base_is_never_unmounted()
    {
        using var mounts = new MountFixture();
        var outside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(mounts.Base)!, "outside")).FullName;
        var link = Path.Combine(mounts.Base, "linked");
        if (OperatingSystem.IsWindows())
        {
            var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var target = mounts.Add("/dev/sdb1", Path.Combine("linked", "sdb1"));
            var calls = 0;

            Assert.False(mounts.Release(target, (_, _, _) => { calls++; return false; }, _ => false));

            Assert.Equal(0, calls);
            Assert.True(Directory.Exists(Path.Combine(outside, "sdb1")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void An_already_removed_mount_still_removes_its_empty_own_directory()
    {
        using var mounts = new MountFixture();
        var target = Directory.CreateDirectory(Path.Combine(mounts.Base, "sdb1")).FullName;

        Assert.True(mounts.Release(target, (_, _, _) => throw new Exception("umount should not run"), _ => false));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void A_successful_command_does_not_release_a_mount_still_present_in_the_table()
    {
        using var mounts = new MountFixture();
        var target = mounts.Add("/dev/sdb1", "sdb1");

        Assert.False(mounts.Release(target, (_, _, _) => true, _ => true));

        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void A_successful_lazy_command_does_not_remove_a_mount_still_present_in_the_table()
    {
        using var mounts = new MountFixture();
        var target = mounts.Add("/dev/sdb1", "sdb1");

        Assert.False(mounts.Release(target, (_, arguments, _) => arguments.Length == 2, _ => false));

        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void Cleanup_removes_only_dead_own_dev_mounts_and_keeps_other_mounts()
    {
        using var mounts = new MountFixture();
        var dead = mounts.Add("/dev/sdb1", "dead");
        var live = mounts.Add("/dev/sdc1", "live");
        var foreign = mounts.Add("/dev/sdd1", "foreign", outside: true);
        var network = mounts.Add("server:/cam", "network");
        var calls = new List<string[]>();
        var run = new Func<string, string[], CancellationToken, bool>((_, arguments, _) =>
        {
            calls.Add(arguments);
            if (arguments.Length == 1)
                return false;
            mounts.Remove(arguments[^1]);
            return true;
        });

        Invoke<object?>("CleanupDeadMountsLinux", mounts.Table, run,
            new Func<string, bool>(source => source == "/dev/sdc1"), mounts.Base);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(foreign));
        Assert.True(Directory.Exists(network));
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { dead }, calls[0]);
        Assert.Equal(new[] { "-l", dead }, calls[1]);
    }

    [Fact]
    public void Failed_mount_attempts_keep_the_command_reason_and_remove_the_empty_directory()
    {
        var root = Directory.CreateTempSubdirectory("astra-mount-").FullName;
        try
        {
            var attempts = 0;
            var run = new Func<string, string[], CancellationToken, bool>((_, _, _) =>
            {
                attempts++;
                throw new IOException(attempts == 1 ? "mount: no medium found" : "mount: unknown filesystem");
            });

            var error = Assert.Throws<IOException>(() => Invoke<Mounted?>("MountOurselves",
                "sdb1", CancellationToken.None, run, new Func<string, string?>(_ => null), root));

            Assert.Equal(2, attempts);
            Assert.Contains("sdb1", error.Message);
            Assert.Contains("no medium found", error.ToString());
            Assert.False(Directory.Exists(Path.Combine(root, "sdb1")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_failed_mount_command_keeps_stderr_and_exit_code()
    {
        var windows = OperatingSystem.IsWindows();
        var arguments = windows
            ? new[] { "/c", "echo mount: no medium found 1>&2 & exit /b 32" }
            : new[] { "-c", "printf 'mount: no medium found' >&2; exit 32" };

        var error = Assert.Throws<IOException>(() => Invoke<bool>("Run",
            windows ? "cmd.exe" : "/bin/sh", arguments, CancellationToken.None));

        Assert.Contains("no medium found", error.Message);
        Assert.Contains("32", error.Message);
    }

    internal static T Invoke<T>(string method, params object?[] arguments)
    {
        try
        {
            return (T)typeof(MountManager).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, arguments)!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed class MountFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("astra-release-").FullName;
        private readonly List<MountEntry> _entries = new();
        public string Base { get; }
        public string Table { get; }

        public MountFixture()
        {
            Base = Directory.CreateDirectory(Path.Combine(_root, "own")).FullName;
            Table = Path.Combine(_root, "mounts");
            Save();
        }

        public string Add(string source, string name, bool outside = false)
        {
            var target = Directory.CreateDirectory(Path.Combine(outside ? Base + "_foreign" : Base, name)).FullName;
            _entries.Add(new MountEntry(source, target, "vfat", "rw"));
            Save();
            return target;
        }

        public void Remove(string target)
        {
            _entries.RemoveAll(entry => entry.MountPoint == target);
            Save();
        }

        public bool Release(string target, Func<string, string[], CancellationToken, bool> run,
            Func<string, bool> exists, bool ours = true) =>
            Invoke<bool>("ReleaseLinux", new Mounted(target, ours), Table, run, exists, Base);

        private void Save()
        {
            static string Escape(string value) => value.Replace("\\", "\\134").Replace(" ", "\\040");
            File.WriteAllLines(Table, _entries.Select(entry =>
                $"{Escape(entry.Device)} {Escape(entry.MountPoint)} vfat rw 0 0"));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
