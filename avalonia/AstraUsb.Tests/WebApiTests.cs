using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Пути, которые панель принимает от браузера. Запись отдаётся только из
/// архива: всё, что вышло за его корень, должно отсекаться.
/// </summary>
public sealed class WebApiTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "archive"));

    [Fact]
    public void A_path_inside_the_archive_is_resolved()
    {
        Assert.Equal(Path.Combine(Root, "Device1", "a.mp4"), WebApi.Inside(Root, "Device1/a.mp4"));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("Device1/../../secret.txt")]
    [InlineData("")]
    public void A_path_outside_the_archive_is_refused(string path)
    {
        Assert.Null(WebApi.Inside(Root, path));
    }
}
