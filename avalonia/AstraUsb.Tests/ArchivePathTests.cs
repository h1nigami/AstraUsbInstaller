using System.Reflection;
using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

public sealed class ArchivePathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-paths-").FullName;
    private string Old => Path.Combine(_root, "old");
    private string Live => Path.Combine(_root, "live");
    private Settings Config => new() { BackupRoot = Old, BackupUuid = "same", BackupRelativePath = "archive" };

    internal static Func<string, string> Resolver(Settings settings, string live,
        ArchiveGuard.DestinationIdentity identity)
    {
        var method = typeof(Settings).GetMethod("ArchivePathResolver", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(string), typeof(Func<string, ArchiveGuard.DestinationIdentity>)], null);
        Assert.NotNull(method);
        try
        {
            return (Func<string, string>)method.Invoke(settings,
                [live, new Func<string, ArchiveGuard.DestinationIdentity>(_ => identity)])!;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void A_confirmed_archive_maps_the_old_mountpoint_without_changing_the_logged_path()
    {
        var path = Path.Combine(Old, "Device7", "DCIM", "movie.mp4");
        var resolve = Resolver(Config, Live, new("same", "", "archive"));
        Assert.Equal(Path.Combine(Live, "Device7", "DCIM", "movie.mp4"), resolve(path));
        Assert.Equal(Path.Combine(Old, "Device7", "DCIM", "movie.mp4"), path);
    }

    [Fact]
    public void A_serial_only_archive_can_move_to_a_new_mountpoint()
    {
        var settings = Config;
        settings.BackupUuid = "";
        settings.BackupSerial = "serial";
        var resolve = Resolver(settings, Live, new("", "serial", "archive"));
        Assert.Equal(Path.Combine(Live, "Device7", "a.txt"), resolve(Path.Combine(Old, "Device7", "a.txt")));
    }

    [Theory]
    [InlineData("other", "archive")]
    [InlineData("same", "other-folder")]
    public void An_unconfirmed_replacement_fails_before_any_path_is_used(string uuid, string relative)
    {
        Assert.Throws<IOException>(() => Resolver(Config, Live, new(uuid, "", relative)));
    }

    [Fact]
    public void Uuid_takes_precedence_over_a_matching_serial()
    {
        var settings = Config;
        settings.BackupSerial = "serial";
        Assert.Throws<IOException>(() => Resolver(settings, Live, new("other", "serial", "archive")));
    }

    [Fact]
    public void A_moved_archive_without_a_saved_relative_folder_cannot_be_confirmed()
    {
        var settings = Config;
        settings.BackupRelativePath = null;
        Assert.Throws<IOException>(() => Resolver(settings, Live, new("same", "", "archive")));
    }

    [Fact]
    public void An_explicit_root_without_identity_accepts_only_direct_device_files()
    {
        var resolve = new Settings { BackupRoot = Old }.ArchivePathResolver(Live);
        Assert.Equal(Path.Combine(Live, "Device7", "a.txt"), resolve(Path.Combine(Live, "Device7", "a.txt")));
        Assert.Throws<IOException>(() => resolve(Path.Combine(Old, "Device7", "a.txt")));
    }

    [Theory]
    [InlineData("Device7/../Device8/a.txt")]
    [InlineData("Device0/a.txt")]
    [InlineData("Other/a.txt")]
    [InlineData("Device7/.bestcam_archive")]
    public void Unsafe_paths_are_not_mapped(string relative)
    {
        var resolve = Resolver(Config, Live, new("same", "", "archive"));
        Assert.Throws<IOException>(() => resolve(Path.Combine(Old, relative)));
    }

    [Fact]
    public void A_link_inside_the_live_archive_is_not_followed()
    {
        Directory.CreateDirectory(Live);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        FileCopierTests.MakeDirectoryLink(Path.Combine(Live, "Device7"), outside);
        var resolve = Resolver(Config, Live, new("same", "", "archive"));
        try { Assert.Throws<IOException>(() => resolve(Path.Combine(Old, "Device7", "a.txt"))); }
        finally { Directory.Delete(Path.Combine(Live, "Device7")); }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
