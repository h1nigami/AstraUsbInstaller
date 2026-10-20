using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class AppRestartTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-restart-").FullName;
    private readonly string _previous = AppPaths.Root;

    public AppRestartTests() => AppPaths.Root = _root;

    [Fact]
    public void A_failed_restart_process_is_reported_with_its_cause()
    {
        var missing = Path.Combine(_root, "missing-application.exe");

        Assert.False(TryStart(new ProcessStartInfo(missing) { UseShellExecute = false }));

        Assert.True(File.Exists(CrashLog.FilePath));
        var log = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains("перезапустить", log);
        Assert.Contains(missing, log);
        Assert.Contains("Win32Exception", log);
    }

    [Fact]
    public void A_programming_error_in_the_start_info_is_not_silenced()
    {
        Assert.Throws<InvalidOperationException>(() => TryStart(new ProcessStartInfo()));
    }

    private static bool TryStart(ProcessStartInfo info)
    {
        try
        {
            return (bool)typeof(AppRestart).GetMethod("TryStart", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [info])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Directory.Delete(_root, recursive: true);
    }
}
