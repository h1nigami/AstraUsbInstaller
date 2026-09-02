using AstraUsb.Services;
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
