using System.Net;

namespace AstraUsb.Services;

/// <summary>Что вышло из попытки отправки.</summary>
/// <param name="Ok">Файл на сервере.</param>
/// <param name="Message">Что сказать оператору.</param>
public sealed record FtpResult(bool Ok, string Message);

/// <summary>
/// Отправка собранных записей на сервер по FTP.
///
/// Отправка идёт после того, как файл лёг в архив, и никогда вместо архива:
/// сеть может пропасть, а записи должны остаться на станции. Поэтому файл
/// сначала попадает в очередь, а уже из неё уходит наружу.
///
/// Реализовано на встроенном клиенте платформы, без сторонних библиотек: на
/// станцию ставится самодостаточная сборка, и каждая зависимость там это
/// лишние мегабайты и лишний повод для несовместимости.
/// </summary>
public static class FtpSender
{
    /// <summary>Проверяет, отвечает ли сервер и принимает ли он учётную запись.</summary>
    public static FtpResult Test(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FtpHost))
            return new FtpResult(false, "не указан адрес сервера");

        try
        {
            var request = Create(settings, "");
            request.Method = WebRequestMethods.Ftp.ListDirectory;

            using var response = (FtpWebResponse)request.GetResponse();
            return new FtpResult(true, "подключение установлено");
        }
        catch (WebException e)
        {
            return new FtpResult(false, Explain(e));
        }
        catch (Exception e)
        {
            return new FtpResult(false, UserError.Report("Не удалось проверить подключение к FTP", e));
        }
    }

    /// <summary>Отправляет один файл. Имя на сервере совпадает с именем в архиве.</summary>
    public static FtpResult Send(Settings settings, string path)
    {
        if (!File.Exists(path))
            return new FtpResult(false, "файла больше нет в архиве");

        try
        {
            var request = Create(settings, Path.GetFileName(path));
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.UseBinary = true;
            request.ContentLength = new FileInfo(path).Length;

            using (var source = File.OpenRead(path))
            using (var target = request.GetRequestStream())
            {
                source.CopyTo(target);
            }

            using var response = (FtpWebResponse)request.GetResponse();
            return new FtpResult(true, "файл отправлен");
        }
        catch (WebException e)
        {
            return new FtpResult(false, Explain(e));
        }
        catch (Exception e)
        {
            return new FtpResult(false, UserError.Report("Не удалось отправить файл на FTP", e));
        }
    }

    private static FtpWebRequest Create(Settings settings, string name)
    {
        var host = settings.FtpHost.Trim().TrimEnd('/');
        if (!host.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            host = "ftp://" + host;

        var port = settings.FtpPort is > 0 and < 65536 ? settings.FtpPort : 21;
        var folder = settings.FtpFolder.Trim().Trim('/');
        var path = string.Join('/', new[] { folder, name }.Where(p => p.Length > 0));

#pragma warning disable SYSLIB0014
        // Встроенный клиент объявлен устаревшим, но замены в платформе нет, а
        // сторонняя библиотека на станции это лишние мегабайты в сборке.
        var request = (FtpWebRequest)WebRequest.Create($"{host}:{port}/{path}");
#pragma warning restore SYSLIB0014

        request.Credentials = new NetworkCredential(settings.FtpUser, settings.FtpPassword);
        request.EnableSsl = settings.FtpSsl;
        request.UsePassive = true;
        request.KeepAlive = false;
        request.Timeout = 20_000;
        request.ReadWriteTimeout = 60_000;
        return request;
    }

    /// <summary>Переводит ошибку клиента в то, что понятно оператору.</summary>
    private static string Explain(WebException error)
    {
        var fallback = UserError.Report("Не удалось выполнить обмен с FTP", error);
        if (error.Response is FtpWebResponse response)
        {
            return response.StatusCode switch
            {
                FtpStatusCode.NotLoggedIn => "учётная запись или пароль не подошли",
                FtpStatusCode.ActionNotTakenFileUnavailable => "сервер не принял путь или файл",
                FtpStatusCode.ActionNotTakenInsufficientSpace => "на сервере нет места",
                _ => fallback,
            };
        }

        return error.Status switch
        {
            WebExceptionStatus.NameResolutionFailure => "адрес сервера не разрешается",
            WebExceptionStatus.ConnectFailure => "сервер не отвечает",
            WebExceptionStatus.Timeout => "сервер не ответил вовремя",
            WebExceptionStatus.TrustFailure => "сертификат сервера не принят",
            _ => fallback,
        };
    }
}
