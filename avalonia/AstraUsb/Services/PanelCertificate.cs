using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AstraUsb.Services;

/// <summary>
/// Сертификат веб-панели.
///
/// Панель может работать по защищённому соединению, и сертификат станция
/// выпускает себе сама: покупать его на каждую станцию незачем, а собственного
/// удостоверяющего центра у объекта обычно нет.
///
/// Плата за это известна: браузер при первом входе предупредит, что
/// сертификату он не доверяет. Зато пароль и записи не идут по сети открытым
/// текстом, а отпечаток сертификата виден в настройках, и по нему вход можно
/// сверить.
/// </summary>
public static class PanelCertificate
{
    /// <summary>Живёт пять лет: станции ставят надолго и не следят за сроками.</summary>
    private static readonly TimeSpan Life = TimeSpan.FromDays(365 * 5);

    public static string FilePath => Path.Combine(AppPaths.DataDir, "panel.pfx");

    /// <summary>
    /// Отдаёт сертификат станции, выпуская его при первом обращении.
    /// Возвращает null, если выпустить не удалось: тогда панель поднимется
    /// по открытому соединению, а причина попадёт в журнал падений.
    /// </summary>
    public static X509Certificate2? Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var stored = new X509Certificate2(FilePath, "", X509KeyStorageFlags.Exportable);

                // Просроченный выпускаем заново: иначе панель перестанет
                // отвечать в день, о котором никто не помнит.
                if (stored.NotAfter > DateTime.Now.AddDays(30))
                    return stored;
            }

            return Issue();
        }
        catch (Exception e)
        {
            CrashLog.Write("сертификат панели", e);
            return null;
        }
    }

    /// <summary>Отпечаток: по нему оператор сверяет, к своей ли станции подключился.</summary>
    public static string Fingerprint()
    {
        try
        {
            var certificate = Load();
            if (certificate is null)
                return "";

            var thumb = certificate.Thumbprint ?? "";
            return string.Join(' ', Enumerable
                .Range(0, thumb.Length / 4)
                .Select(i => thumb.Substring(i * 4, 4)));
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static X509Certificate2 Issue()
    {
        using var key = RSA.Create(2048);

        var name = Dns.GetHostName();
        var request = new CertificateRequest(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature
                                      | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        // Имена, по которым к станции обращаются: своё имя в сети и localhost
        // для проверки на самой станции.
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.Add(Life));

        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllBytes(FilePath, certificate.Export(X509ContentType.Pfx));

        // Закрытый ключ лежит рядом с базой, поэтому файл читает только
        // владелец: на Astra это root, от которого работает служба.
        Restrict(FilePath);

        return new X509Certificate2(FilePath, "", X509KeyStorageFlags.Exportable);
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Права не выставились: файловая система может их не поддерживать.
        }
    }
}
