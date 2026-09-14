using AstraUsb.Services;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Присмотр за томом архива. Самая дорогая ошибка станции такая: диск не
/// смонтировался, записи ушли в пустой каталог на системном разделе, а
/// оператор увидел «готово».
/// </summary>
public sealed class ArchiveGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-archive-").FullName;

    [Fact]
    public void An_unmarked_folder_is_not_an_archive()
    {
        var root = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(root);

        Assert.False(ArchiveGuard.Available(root));
    }

    [Fact]
    public void Marking_makes_the_volume_usable()
    {
        var root = Path.Combine(_dir, "archive");

        Assert.True(ArchiveGuard.Mark(root));
        Assert.True(ArchiveGuard.Available(root));
    }

    [Fact]
    public void A_missing_folder_is_not_an_archive()
    {
        Assert.False(ArchiveGuard.Available(Path.Combine(_dir, "нет-такой")));
        Assert.False(ArchiveGuard.Available(null));
        Assert.False(ArchiveGuard.Available(""));
    }

    [Fact]
    public void The_marker_counts_as_a_service_file()
    {
        // Иначе метка уедет в архив вместе с записями и попадёт в поиск.
        Assert.True(Markers.IsService(Markers.Archive));
        Assert.True(Markers.IsService(Markers.CardId));
        Assert.True(Markers.IsService(Markers.LegacyId));
        Assert.False(Markers.IsService("VID_00231.MP4"));
        Assert.False(Markers.IsService(null));
    }

    [Fact]
    public void The_disk_holding_the_archive_is_not_a_source()
    {
        var media = Path.Combine(_dir, "media", "BCDATA");
        var archive = Path.Combine(media, "archive");

        Assert.True(ArchiveGuard.IsArchiveMedia(media, archive));
        Assert.True(ArchiveGuard.IsArchiveMedia(media, media));
    }

    [Fact]
    public void A_camera_card_is_still_a_source()
    {
        var camera = Path.Combine(_dir, "media", "CAM");
        var archive = Path.Combine(_dir, "media", "BCDATA", "archive");

        Assert.False(ArchiveGuard.IsArchiveMedia(camera, archive));
        Assert.False(ArchiveGuard.IsArchiveMedia(null, archive));
        Assert.False(ArchiveGuard.IsArchiveMedia(camera, null));
    }

    [Fact]
    public void A_lookalike_path_is_not_taken_for_the_archive_disk()
    {
        var media = Path.Combine(_dir, "media", "CAM");
        var archive = Path.Combine(_dir, "media", "CAM_OLD", "archive");

        Assert.False(ArchiveGuard.IsArchiveMedia(media, archive));
    }

    [Theory]
    [InlineData("Device1", true)]
    [InlineData("Device999", true)]
    [InlineData("Device0", false)]
    [InlineData("DeviceX", false)]
    [InlineData("Device+1", false)]
    [InlineData("Device 1", false)]
    [InlineData("lost+found", false)]
    public void Ownership_targets_only_direct_device_folders(string name, bool accepted)
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "archive with spaces")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var command = OwnershipCommand(root, folder);

        if (!accepted)
        {
            Assert.Null(command);
            return;
        }
        Assert.NotNull(command);
        Assert.Equal("chown", command.FileName);
        Assert.False(command.UseShellExecute);
        Assert.Equal(new[] { "-R", $"--reference={root}", "--", folder }, command.ArgumentList);
    }

    [Fact]
    public void Ownership_rejects_outside_nested_missing_and_file_targets()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "archive")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "Device1")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "Device1", "Device2")).FullName;
        var file = Path.Combine(root, "Device3");
        File.WriteAllText(file, "x");

        Assert.Null(OwnershipCommand(root, outside));
        Assert.Null(OwnershipCommand(root, nested));
        Assert.Null(OwnershipCommand(root, root));
        Assert.Null(OwnershipCommand(root, Path.Combine(root, "Device4")));
        Assert.Null(OwnershipCommand(root, file));
    }

    [Fact]
    public void Ownership_rejects_a_symbolic_device_folder()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Directory.CreateDirectory(Path.Combine(_dir, "archive")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        var link = Path.Combine(root, "Device1");
        Directory.CreateSymbolicLink(link, outside);

        Assert.Null(OwnershipCommand(root, link));
    }

    private static ProcessStartInfo? OwnershipCommand(string root, string folder)
    {
        var method = typeof(ArchiveGuard).GetMethod("OwnershipCommand", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (ProcessStartInfo?)method.Invoke(null, [root, folder]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
