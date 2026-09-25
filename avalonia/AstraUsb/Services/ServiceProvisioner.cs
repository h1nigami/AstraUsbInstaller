namespace AstraUsb.Services;

/// <summary>
/// Авто-настройка CMS-сервера при подключении A11 по USB: перенос
/// <c>VendorUsb.Configure</c> из viewer. Решение — что делать на этом опросе —
/// чистая функция без таймера и без UI, поэтому проверяется подставным
/// <see cref="IServiceUsb"/>; таймер и поток опроса живут у вызывающего.
/// </summary>
public sealed class ServiceProvisioner(IServiceUsb usb)
{
    /// <summary>
    /// Уже пробовали настроить текущее подключение. Сбрасывается, когда
    /// устройство пропадает: присутствие и есть граница «сеанса».
    /// </summary>
    private bool _attempted;

    /// <summary>
    /// Итог последней попытки для этого подключения. Пока устройство не
    /// пропало, повторный опрос отдаёт его же — иначе строка на экране гасла
    /// бы каждые 1.5 с, хотя ничего не изменилось.
    /// </summary>
    private string? _lastStatus;

    /// <summary>
    /// Один шаг опроса. Пустой host выключает функцию совсем — USB не трогаем.
    /// Само обращение к USB (в том числе <see cref="IServiceUsb.Count"/>) может
    /// бросить что угодно, от нехватки прав до отсутствия libusb на станции —
    /// это фоновый опрос, и такой сбой не должен ронять программу, поэтому вся
    /// работа с <see cref="usb"/> идёт под одним try/catch.
    /// </summary>
    public string? Step(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.CmsServerHost))
            return null;

        try
        {
            var count = usb.Count();
            if (count == 0)
            {
                _attempted = false;
                _lastStatus = null;
                return null;
            }

            if (count > 1)
                return "Для настройки подключите только один A11 по USB.";

            if (_attempted)
                return _lastStatus;
            _attempted = true;

            if (!ServiceFrame.IsDottedIPv4(settings.CmsServerHost)
                || settings.CmsServerPort is < 1 or > 65535)
                return _lastStatus = "Адрес сервера в настройках неверен.";

            var port = (ushort)settings.CmsServerPort;
            var wrote = usb.WriteServer(settings.CmsServerHost, port);
            return _lastStatus = wrote
                ? $"Сервер {settings.CmsServerHost}:{port} записан. Отключите USB и перезагрузите A11."
                : $"Сервер {settings.CmsServerHost}:{port} уже настроен.";
        }
        catch (Exception e)
        {
            return _lastStatus = $"USB: {e.Message}";
        }
    }
}
