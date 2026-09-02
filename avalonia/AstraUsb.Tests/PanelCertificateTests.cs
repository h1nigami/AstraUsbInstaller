using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Сертификат панели. Станция выпускает его себе сама, поэтому проверяется,
/// что он годен для сервера, живёт достаточно долго и не выпускается заново на
/// каждый запуск: иначе браузер ругался бы каждый день.
/// </summary>
public sealed class PanelCertificateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-cert-").FullName;
    private readonly string _root;

    public PanelCertificateTests()
    {
        _root = AppPaths.Root;
        AppPaths.Root = _dir;
    }

    [Fact]
    public void The_station_issues_a_certificate_for_itself()
    {
        var certificate = PanelCertificate.Load();

        Assert.NotNull(certificate);
        Assert.True(certificate.HasPrivateKey);
        Assert.True(File.Exists(PanelCertificate.FilePath));
    }

    [Fact]
    public void It_is_not_reissued_on_every_start()
    {
        var first = PanelCertificate.Load();
        var second = PanelCertificate.Load();

        // Иначе браузер ругался бы на новый сертификат при каждом запуске.
        Assert.Equal(first!.Thumbprint, second!.Thumbprint);
    }

    [Fact]
    public void It_lives_long_enough_for_a_station()
    {
        var certificate = PanelCertificate.Load();

        // Станции ставят надолго и за сроками не следят.
        Assert.True(certificate!.NotAfter > DateTime.Now.AddYears(4));
        Assert.True(certificate.NotBefore < DateTime.Now);
    }

    [Fact]
    public void The_fingerprint_is_readable_by_a_human()
    {
        var fingerprint = PanelCertificate.Fingerprint();

        // По нему оператор сверяет, к своей ли станции подключился.
        Assert.Contains(' ', fingerprint);
        Assert.Equal(PanelCertificate.Load()!.Thumbprint,
            fingerprint.Replace(" ", ""));
    }

    [Fact]
    public void A_broken_file_does_not_stop_the_station()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(PanelCertificate.FilePath, "это не сертификат");

        // Панель поднимется без шифрования, но станция работать не перестанет.
        Assert.Null(PanelCertificate.Load());
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
