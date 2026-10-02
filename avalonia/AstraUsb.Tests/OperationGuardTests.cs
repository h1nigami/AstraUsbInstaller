using AstraUsb.Services;
using System.Diagnostics;
using Xunit;

namespace AstraUsb.Tests;

public sealed class OperationGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("operation-guard-").FullName;
    private string Database => Path.Combine(_dir, "devices.db");

    [Fact]
    public void Shared_operations_can_run_together_and_exclude_maintenance()
    {
        using var first = Acquire();
        using var second = Acquire();
        Assert.Throws<StationBusyException>(() => Acquire(exclusive: true));
    }

    [Fact]
    public void Maintenance_excludes_other_operations_and_releases_the_lock()
    {
        using (Acquire(exclusive: true))
        {
            Assert.Throws<StationBusyException>(() => Acquire());
            Assert.Throws<StationBusyException>(() => Acquire(exclusive: true));
        }
        using var shared = Acquire();
    }

    [Fact]
    public void Independent_databases_do_not_block_each_other()
    {
        using var first = Acquire(exclusive: true);
        using var second = Acquire(exclusive: true, Path.Combine(_dir, "other.db"));
    }

    [Fact]
    public void Disposing_a_lease_twice_does_not_release_a_later_operation()
    {
        var first = Acquire();
        first.Dispose();
        using var writer = Acquire(exclusive: true);
        first.Dispose();
        Assert.Throws<StationBusyException>(() => Acquire());
    }

    [Fact]
    public void Another_process_obeys_shared_and_exclusive_operations()
    {
        using (Acquire())
        {
            Assert.Equal(0, Probe(exclusive: false));
            Assert.NotEqual(0, Probe(exclusive: true));
        }
        using (Acquire(exclusive: true))
        {
            Assert.NotEqual(0, Probe(exclusive: false));
            Assert.NotEqual(0, Probe(exclusive: true));
        }
        Assert.Equal(0, Probe(exclusive: true));
    }

    private int Probe(bool exclusive)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "flock")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add($"try {{ $f = [IO.File]::Open('{Database.Replace("'", "''")}.operations.lock', "
                + $"'OpenOrCreate', 'ReadWrite', '{(exclusive ? "None" : "ReadWrite")}'); $f.Dispose(); exit 0 }} catch {{ exit 1 }}");
        }
        else
        {
            info.ArgumentList.Add("--nonblock");
            info.ArgumentList.Add(exclusive ? "--exclusive" : "--shared");
            info.ArgumentList.Add(Database + ".operations.lock");
            info.ArgumentList.Add("true");
        }
        using var child = Process.Start(info)!;
        Assert.True(child.WaitForExit(10_000));
        return child.ExitCode;
    }

    private IDisposable Acquire(bool exclusive = false, string? dbPath = null)
        => OperationGuard.Acquire(exclusive, dbPath ?? Database);

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
