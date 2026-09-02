using System.Net.Sockets;

namespace AstraUsb.Services;

/// <summary>
/// Проверка внешнего сервера базы данных.
///
/// Станция работает на своей базе рядом с программой, и задание это допускает:
/// внешний сервер настраивают только там, где он есть. Драйвер чужой базы в
/// сборку не входит, поэтому проверяется то, что можно проверить честно:
/// отвечает ли сервер на своём порту. Дальше этого станция пока не идёт, и в
/// разделе настроек об этом сказано прямо.
/// </summary>
public static class SqlProbe
{
    /// <summary>Порты по умолчанию для баз, которые встречаются на объектах.</summary>
    public static int DefaultPort(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "postgresql" or "postgres" => 5432,
        "mssql" or "sqlserver" => 1433,
        _ => 3306,
    };

    /// <summary>Отвечает ли сервер на этом адресе и порту.</summary>
    public static async Task<string> CheckAsync(string host, int port, TimeSpan timeout)
    {
        var address = host.Trim();
        if (address.Length == 0)
            return "не указан адрес сервера";

        if (port is <= 0 or > 65535)
            return "порт вне допустимых значений";

        try
        {
            using var client = new TcpClient();
            using var cancel = new CancellationTokenSource(timeout);

            await client.ConnectAsync(address, port, cancel.Token);
            return $"сервер отвечает: {address}:{port}";
        }
        catch (OperationCanceledException)
        {
            return "сервер не ответил вовремя";
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode switch
            {
                SocketError.HostNotFound => "адрес сервера не разрешается",
                SocketError.ConnectionRefused => "порт закрыт: служба базы не слушает его",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "сеть не достаёт до сервера",
                _ => e.Message,
            };
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
