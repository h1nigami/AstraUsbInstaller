using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Разбор таблицы монтирования. По ней станция решает, смонтирована ли карта и
/// кем: чужое монтирование трогать нельзя, а своё нужно отпустить.
/// </summary>
public sealed class MountTableTests
{
    [Fact]
    public void A_line_splits_into_device_point_type_and_options()
    {
        var entry = Assert.Single(MountTable.Parse(
            ["/dev/sdb1 /media/best/CAM vfat rw,nosuid,nodev,relatime 0 0"]));

        Assert.Equal("/dev/sdb1", entry.Device);
        Assert.Equal("/media/best/CAM", entry.MountPoint);
        Assert.Equal("vfat", entry.FileSystem);
        Assert.False(entry.ReadOnly);
    }

    [Fact]
    public void A_space_in_the_label_survives_the_parsing()
    {
        // Ядро экранирует пробел как \040, и наивное деление по пробелу
        // разорвало бы путь пополам.
        var entry = Assert.Single(MountTable.Parse(
            [@"/dev/sdb1 /media/best/BODY\040CAM vfat rw,relatime 0 0"]));

        Assert.Equal("/media/best/BODY CAM", entry.MountPoint);
    }

    [Fact]
    public void Read_only_mounts_are_recognised()
    {
        var entry = Assert.Single(MountTable.Parse(
            ["/dev/sdb1 /media/cam vfat ro,relatime 0 0"]));

        Assert.True(entry.ReadOnly);
    }

    [Fact]
    public void A_read_only_option_is_not_confused_with_a_similar_one()
    {
        var entry = Assert.Single(MountTable.Parse(
            ["/dev/sdb1 /media/cam vfat rw,errors=remount-ro 0 0"]));

        Assert.False(entry.ReadOnly);
    }

    [Fact]
    public void Broken_and_empty_lines_are_skipped()
    {
        var found = MountTable.Parse([
            "",
            "мусор",
            "/dev/sdb1 /media/cam vfat rw 0 0",
        ]);

        Assert.Single(found);
    }

    [Theory]
    [InlineData(@"без\040экранирования", "без экранирования")]
    [InlineData(@"таб\011здесь", "таб\tздесь")]
    [InlineData(@"обратный\134слэш", @"обратный\слэш")]
    [InlineData("обычный", "обычный")]
    [InlineData(@"не\9восьмеричное", @"не\9восьмеричное")]
    public void Octal_escapes_turn_back_into_characters(string field, string expected)
    {
        Assert.Equal(expected, MountTable.Unescape(field));
    }

    [Fact]
    public void Our_own_mount_points_are_told_apart_from_the_desktops()
    {
        Assert.True(MountManager.IsOurs("/mnt/usb_backup/sdb1"));
        Assert.True(MountManager.IsOurs(MountManager.MountBase));
        Assert.False(MountManager.IsOurs("/media/best/CAM"));
        Assert.False(MountManager.IsOurs(""));
        Assert.False(MountManager.IsOurs(null));
    }

    [Fact]
    public void A_lookalike_path_is_not_taken_for_ours()
    {
        // Чужой каталог с похожим именем не должен считаться нашим: иначе
        // станция размонтировала бы то, что ей не принадлежит.
        Assert.False(MountManager.IsOurs("/mnt/usb_backup_old/sdb1"));
    }
}
