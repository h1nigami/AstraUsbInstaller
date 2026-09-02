using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Версия программы. Её видно в разделе «О программе» и по ней станция решает,
/// обновляться ли. Сети на объекте может не быть неделями, поэтому версия
/// всегда берётся с самой станции, а не спрашивается у сервера.
/// </summary>
[Collection("Каталог данных")]
public sealed class VersionInfoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-version-").FullName;
    private readonly string _root;

    public VersionInfoTests()
    {
        _root = AppPaths.Root;
        AppPaths.Root = _dir;
    }

    [Fact]
    public void The_release_file_gives_the_tag_and_the_date()
    {
        File.WriteAllText(AppPaths.VersionFile, "v2.0 2026-09-02\n");

        Assert.Equal("v2.0", VersionInfo.Tag());
        Assert.Equal("версия 2.0 от 02.09.26", VersionInfo.Label());
    }

    [Fact]
    public void Without_the_file_the_version_comes_from_the_build()
    {
        // Сборка из исходников файла не содержит, а версию всё равно надо
        // показать: без сети её больше взять неоткуда.
        var tag = VersionInfo.Tag();

        Assert.StartsWith("v", tag);
        Assert.Matches(@"^v\d+\.\d+", tag);
        Assert.Contains("версия", VersionInfo.Label());
        Assert.DoesNotContain("неизвестна", VersionInfo.Label());
    }

    [Fact]
    public void A_broken_file_does_not_hide_the_version()
    {
        File.WriteAllText(AppPaths.VersionFile, "мусор");

        Assert.Equal(VersionInfo.Build(), VersionInfo.Tag());
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
