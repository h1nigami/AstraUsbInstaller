using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class StorageStateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-storage-").FullName;

    [Fact]
    public void An_archive_without_its_marker_does_not_report_system_drive_capacity()
    {
        var state = StorageState.Read(_root);

        Assert.False(state.Available);
        Assert.Equal(0, state.Total);
        Assert.Equal(0, state.Free);
    }

    [Fact]
    public void A_marked_archive_reports_available_volume_capacity()
    {
        Assert.True(ArchiveGuard.Mark(_root));

        var state = StorageState.Read(_root);

        Assert.True(state.Available);
        Assert.True(state.Total > 0);
        Assert.InRange(state.Free, 0, state.Total);
    }

    [ArchiveMountFact]
    public void A_nested_linux_archive_reports_its_own_volume_instead_of_the_system_disk()
    {
        var root = Environment.GetEnvironmentVariable("ASTRA_TEST_ARCHIVE_MOUNT")!;
        Assert.True(ArchiveGuard.Mark(root));

        var state = StorageState.Read(root);

        Assert.True(state.Available);
        Assert.Equal(8 * 1024 * 1024, state.Total);
        Assert.InRange(state.Free, 0, state.Total);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}

public sealed class ArchiveMountFactAttribute : FactAttribute
{
    public ArchiveMountFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASTRA_TEST_ARCHIVE_MOUNT")))
            Skip = "Нужен изолированный Linux с tmpfs 8 МиБ в ASTRA_TEST_ARCHIVE_MOUNT";
    }
}
