using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("AstraUsb.Tests")]

namespace AstraUsb.Services;

/// <summary>Сервисный USB-доступ к регистратору A11: чтение и запись CMS-сервера.</summary>
public interface IServiceUsb
{
    /// <summary>Число подключённых регистраторов (VID 4255 / PID 0001).</summary>
    int Count();

    /// <summary>Читает сервер CMS. null — регистратор не подключён.</summary>
    ServiceFrame.CmsServer? ReadServer();

    /// <summary>
    /// Пишет IP и порт CMS-сервера с проверкой чтением после записи.
    /// Возвращает false, если параметры уже совпадали и запись не потребовалась.
    /// </summary>
    bool WriteServer(string ip, ushort port);
}

/// <summary>
/// Протокол сервисного режима поверх <see cref="ServiceFrame"/>: логин, чтение
/// и защищённая запись сервера. Обмен кадрами и подсчёт подключённых
/// регистраторов приходят снаружи, поэтому логику можно проверить без
/// реального USB — <see cref="ServiceUsb"/> подключает сюда libusb.
/// </summary>
internal sealed class ServiceProtocol(Func<byte[], byte[]> exchange, Func<int> count)
{
    private const ushort CmdLogin = 0x01;
    private const ushort CmdReadServer = 0x0D;
    private const ushort CmdWriteServer = 0x0C;
    private const int MaxReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public ServiceFrame.CmsServer? ReadServer()
    {
        var connected = count();
        if (connected == 0)
            return null;
        if (connected > 1)
            throw new IOException("Подключено больше одного регистратора");

        Login();
        return ReadCurrentServer();
    }

    public bool WriteServer(string ip, ushort port)
    {
        if (count() != 1)
            throw new IOException("Для записи должен быть подключён один регистратор");

        Login();
        var current = ReadCurrentServer();
        if (Matches(current, ip, port))
            return false;

        // Сразу после команды IN бывает отдаёт старый ответ предыдущей
        // команды (A11, 25.09.2026): если подтверждение записи не
        // разобралось, саму запись не повторяем и ошибкой это ещё не
        // считаем — решает чтение ниже, оно уже ретраится само.
        try
        {
            ExchangeOnce(CmdWriteServer, ServiceFrame.WriteServer(current, ip, port));
        }
        catch (InvalidDataException)
        {
        }

        var after = ReadCurrentServer();
        if (!Matches(after, ip, port))
            throw new IOException("регистратор не подтвердил новые параметры чтением");

        return true;
    }

    private static bool Matches(ServiceFrame.CmsServer server, string ip, ushort port) =>
        server.Type == 2 && server.Enabled && server.Ip == ip && server.Port == port;

    private void Login() => ReadWithRetry(CmdLogin, ReadOnlySpan<byte>.Empty, data => data);

    // Разбор структуры внутри повторов: сразу после записи регистратор бывает
    // не готов и отдаёт старый ответ или кадр чтения без данных (A11, 25.09.2026).
    private ServiceFrame.CmsServer ReadCurrentServer() =>
        ReadWithRetry(CmdReadServer, ReadOnlySpan<byte>.Empty, data => ServiceFrame.ReadServer(data));

    /// <summary>
    /// Как у viewer: если ответ не разобрался (устройство "не ответило"),
    /// повторяет чтение до 3 попыток с паузой в 1 секунду. Запись не
    /// повторяется — см. <see cref="ExchangeOnce"/>.
    /// </summary>
    private T ReadWithRetry<T>(ushort cmd, ReadOnlySpan<byte> data, Func<byte[], T> parse)
    {
        var request = ServiceFrame.Request(cmd, data);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return parse(ServiceFrame.ParseReply(cmd, exchange(request)));
            }
            catch (InvalidDataException) when (attempt < MaxReadAttempts)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private byte[] ExchangeOnce(ushort cmd, ReadOnlySpan<byte> data) =>
        ServiceFrame.ParseReply(cmd, exchange(ServiceFrame.Request(cmd, data)));
}

/// <summary>P/Invoke-транспорт libusb для сервисного протокола A11.</summary>
public sealed class ServiceUsb : IServiceUsb
{
    private const ushort VendorId = 0x4255;
    private const ushort ProductId = 0x0001;

    public int Count()
    {
        var rc = Libusb.libusb_init(out var ctx);
        if (rc < 0)
            throw new IOException($"Не удалось инициализировать USB: код {rc}");
        try
        {
            return WithDeviceList(ctx, (list, n) => MatchingDevices(list, n).Count());
        }
        finally
        {
            Libusb.libusb_exit(ctx);
        }
    }

    public ServiceFrame.CmsServer? ReadServer()
    {
        using var session = new Session();
        return new ServiceProtocol(session.Exchange, Count).ReadServer();
    }

    public bool WriteServer(string ip, ushort port)
    {
        using var session = new Session();
        return new ServiceProtocol(session.Exchange, Count).WriteServer(ip, port);
    }

    private static T WithDeviceList<T>(IntPtr ctx, Func<IntPtr, int, T> body)
    {
        var count = (long)Libusb.libusb_get_device_list(ctx, out var list);
        if (count < 0)
            throw new IOException($"Не удалось получить список USB-устройств: код {count}");
        try
        {
            return body(list, (int)count);
        }
        finally
        {
            Libusb.libusb_free_device_list(list, 1);
        }
    }

    private static IEnumerable<IntPtr> MatchingDevices(IntPtr list, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var device = Marshal.ReadIntPtr(list, i * IntPtr.Size);
            if (Libusb.libusb_get_device_descriptor(device, out var descriptor) == 0
                && descriptor.idVendor == VendorId && descriptor.idProduct == ProductId)
                yield return device;
        }
    }

    /// <summary>
    /// Одно открытие устройства на публичный вызов: открывается лениво при
    /// первом обмене (чтобы при отказе по числу устройств USB не трогался
    /// вовсе) и закрывается в конце вызова через using.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private IntPtr ctx;
        private IntPtr handle;
        private bool opened;

        public byte[] Exchange(byte[] frame)
        {
            if (!opened)
                Open();
            return Libusb.Transfer(handle, frame);
        }

        private void Open()
        {
            var rc = Libusb.libusb_init(out ctx);
            if (rc < 0)
                throw new IOException($"Не удалось инициализировать USB: код {rc}");

            // libusb_open принимает указатель из списка устройств, а
            // libusb_free_device_list(unref=1) может его освободить — открываем
            // устройство внутри того же прохода по списку, до освобождения.
            var (foundOne, openRc, openHandle) = WithDeviceList(ctx, (list, n) =>
            {
                var matches = MatchingDevices(list, n).ToArray();
                if (matches.Length != 1)
                    return (false, 0, IntPtr.Zero);
                var openResult = Libusb.libusb_open(matches[0], out var deviceHandle);
                return (true, openResult, deviceHandle);
            });

            if (!foundOne)
            {
                Libusb.libusb_exit(ctx);
                throw new IOException("подключите один A11");
            }
            if (openRc < 0)
            {
                Libusb.libusb_exit(ctx);
                throw new IOException($"Не удалось открыть USB-устройство: код {openRc}");
            }
            handle = openHandle;

            // Код возврата не проверяем: ядро может не поддерживать эту функцию
            // или драйвер уже не занят — оба случая не мешают claim_interface.
            Libusb.libusb_set_auto_detach_kernel_driver(handle, 1);

            rc = Libusb.libusb_claim_interface(handle, 0);
            if (rc < 0)
            {
                Libusb.libusb_close(handle);
                Libusb.libusb_exit(ctx);
                throw new IOException($"Не удалось занять интерфейс USB: код {rc}");
            }

            opened = true;
        }

        public void Dispose()
        {
            if (!opened)
                return;
            Libusb.libusb_release_interface(handle, 0);
            Libusb.libusb_close(handle);
            Libusb.libusb_exit(ctx);
        }
    }

    /// <summary>Тонкая обёртка над libusb-1.0 — без протокольной логики.</summary>
    private static class Libusb
    {
        private const string Lib = "libusb-1.0.so.0";
        private const byte RequestTypeOut = 0x40;
        private const byte RequestTypeIn = 0xC0;
        private const byte RequestOut = 0x5A;
        private const byte RequestIn = 0x59;
        private const ushort Value = 0xAA;
        private const uint TransferTimeoutMs = 1000;
        private const int MaxReadTransfers = 6;
        private const int ErrorTimeout = -7; // LIBUSB_ERROR_TIMEOUT: ответа пока нет, не ошибка

        /// <summary>
        /// OUT-кадр команды, пауза 200 мс, затем IN по 1024 байт, пока не
        /// накопится целый кадр (по заявленной в заголовке длине, до 6 попыток,
        /// пауза 100 мс между пустыми чтениями).
        /// </summary>
        public static byte[] Transfer(IntPtr handle, byte[] frame)
        {
            var sent = libusb_control_transfer(handle, RequestTypeOut, RequestOut, Value, 0,
                frame, (ushort)frame.Length, TransferTimeoutMs);
            if (sent < 0)
                throw new IOException($"Ошибка отправки команды USB: код {sent}");
            if (sent != frame.Length)
                throw new IOException($"Отправлена только часть кадра USB: {sent} из {frame.Length} байт");

            Thread.Sleep(200);

            var reply = new List<byte>();
            for (var attempt = 0; attempt < MaxReadTransfers; attempt++)
            {
                var buffer = new byte[1024];
                var read = libusb_control_transfer(handle, RequestTypeIn, RequestIn, Value, 0,
                    buffer, (ushort)buffer.Length, TransferTimeoutMs);
                if (read < 0 && read != ErrorTimeout)
                    throw new IOException($"Ошибка чтения ответа USB: код {read}");

                if (read > 0)
                    reply.AddRange(buffer.AsSpan(0, read).ToArray());

                if (FrameComplete(reply))
                    return reply.ToArray();

                if (read <= 0)
                    Thread.Sleep(100);
            }

            throw new IOException("Регистратор не ответил по USB");
        }

        /// <summary>
        /// Кадр целиком получен, когда накоплено заявленных в заголовке
        /// len16+6 байт (7E + len16(2) + [cmd+резерв+payload] + xor + 0D 0A)
        /// и последние два байта на этой границе — 0D 0A.
        /// </summary>
        private static bool FrameComplete(List<byte> reply)
        {
            if (reply.Count < 3 || reply[0] != 0x7E)
                return false;

            var len16 = reply[1] | (reply[2] << 8);
            var frameLength = 3 + len16 + 3;
            return reply.Count >= frameLength
                && reply[frameLength - 2] == 0x0D
                && reply[frameLength - 1] == 0x0A;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DeviceDescriptor
        {
            public byte bLength;
            public byte bDescriptorType;
            public ushort bcdUSB;
            public byte bDeviceClass;
            public byte bDeviceSubClass;
            public byte bDeviceProtocol;
            public byte bMaxPacketSize0;
            public ushort idVendor;
            public ushort idProduct;
            public ushort bcdDevice;
            public byte iManufacturer;
            public byte iProduct;
            public byte iSerialNumber;
            public byte bNumConfigurations;
        }

        [DllImport(Lib)]
        public static extern int libusb_init(out IntPtr ctx);

        [DllImport(Lib)]
        public static extern void libusb_exit(IntPtr ctx);

        [DllImport(Lib)]
        public static extern IntPtr libusb_get_device_list(IntPtr ctx, out IntPtr list);

        [DllImport(Lib)]
        public static extern void libusb_free_device_list(IntPtr list, int unrefDevices);

        [DllImport(Lib)]
        public static extern int libusb_get_device_descriptor(IntPtr device, out DeviceDescriptor descriptor);

        [DllImport(Lib)]
        public static extern int libusb_open(IntPtr device, out IntPtr handle);

        [DllImport(Lib)]
        public static extern void libusb_close(IntPtr handle);

        [DllImport(Lib)]
        public static extern int libusb_set_auto_detach_kernel_driver(IntPtr handle, int enable);

        [DllImport(Lib)]
        public static extern int libusb_claim_interface(IntPtr handle, int interfaceNumber);

        [DllImport(Lib)]
        public static extern int libusb_release_interface(IntPtr handle, int interfaceNumber);

        [DllImport(Lib)]
        public static extern int libusb_control_transfer(IntPtr handle, byte requestType, byte request,
            ushort value, ushort index, byte[] data, ushort length, uint timeout);
    }
}
