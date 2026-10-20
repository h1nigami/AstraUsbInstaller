using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class StorageManagerFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-storage-errors-").FullName;
    private readonly string _previous = AppPaths.Root;
    private readonly string _archive;

    public StorageManagerFailureTests()
    {
        AppPaths.Root = _root;
        _archive = AppPaths.BackupsRoot;
        Assert.True(ArchiveGuard.Mark(_archive));
        Assert.True(new Settings { BackupRoot = _archive }.Save());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_locked_archive_file_and_its_record_are_preserved_and_the_deletion_cause_is_logged(bool expired)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Write("Device7/locked.mp4", 8);
        var collected = DateTime.Now.AddDays(-40);
        var log = new CollectionLog(AppPaths.Database);
        log.Record([new CollectedFile(7, path, 8, collected, collected)]);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        long freed;
        if (expired)
        {
            var removed = StorageManager.DeleteExpired(log, DateTime.Now.AddDays(-30), _archive);
            Assert.Equal(0, removed.Files);
            freed = removed.Bytes;
        }
        else
            freed = StorageManager.FreeUpSpace(_archive, 8, StorageMode.Overwrite, log);

        Assert.Equal(0, freed);
        Assert.True(File.Exists(path));
        Assert.Equal(path, Assert.Single(log.CollectedBetween(DateTime.MinValue, DateTime.MaxValue)).DestPath);
        Assert.True(File.Exists(CrashLog.FilePath));
        var cause = File.ReadAllText(CrashLog.FilePath);
        Assert.Contains(path, cause);
        Assert.Contains("IOException", cause);
        Assert.Contains("Win32Exception", cause);
    }

    [Fact]
    public void An_inaccessible_oldest_directory_is_logged_instead_of_looking_empty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Write("Device7/blocked/oldest.mp4", 8);
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var original = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var denied = directory.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        using var archive = ArchiveGuard.Open(_archive);
        try
        {
            directory.SetAccessControl(denied);

            Assert.Empty(Invoke<IEnumerable<FileInfo>>("OldestFirst", archive));

            Assert.True(File.Exists(CrashLog.FilePath));
            var cause = File.ReadAllText(CrashLog.FilePath);
            Assert.Contains(directory.FullName, cause);
            Assert.Contains("UnauthorizedAccessException", cause);
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restored);
        }
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("Remove")]
    [InlineData("OldestFirst")]
    public void A_programming_error_does_not_become_an_empty_cleanup_result(string method)
    {
        object?[] arguments = method == "Remove"
            ? [null, Path.Combine(_archive, "Device7", "clip.mp4"), null, null]
            : [null];

        Assert.Throws<NullReferenceException>(() => Invoke<object?>(method, arguments));
    }

    private string Write(string relative, int size)
    {
        var path = Path.GetFullPath(Path.Combine(_archive, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static T Invoke<T>(string method, params object?[] arguments)
    {
        try
        {
            return (T)typeof(StorageManager).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, arguments)!;
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
