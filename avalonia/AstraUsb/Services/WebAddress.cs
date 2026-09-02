using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AstraUsb.Services;

/// <summary>
/// По каким адресам отвечает веб-панель.
///
/// Панель это сервер внутри самой программы: отдельной машины под неё нет,
/// и заходят на неё по адресу станции в той сети, куда она включена.
/// Оператору этот адрес взять больше негде, поэтому станция показывает его
/// сама, рядом с выключателем панели.
/// </summary>
public static class WebAddress
{
    /// <summary>Ссылка на панель по одному адресу.</summary>
    public static string Link(string host, int port, bool ssl) =>
        $"{(ssl ? "https" : "http")}://{host}:{port}";

    /// <summary>
    /// Адреса станции в сети. Обратная петля не годится: по ней панель видна
    /// только с самой станции, а нужна она с телефона рядом с ней.
    /// </summary>
    public static IReadOnlyList<string> Local()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .ToArray();
        }
        catch (Exception)
        {
            // Список сетевых устройств бывает недоступен: тогда адрес
            // подскажет тот, кто ставил станцию.
            return [];
        }
    }

    /// <summary>Готовые ссылки на панель или пустой список, если сети нет.</summary>
    public static IReadOnlyList<string> Links(int port, bool ssl) =>
        Local().Select(host => Link(host, port, ssl)).ToArray();
}
