using System.Text;

namespace AstraUsb.Services;

/// <summary>
/// Кодек кадра сервисного USB-протокола регистратора A11 и разбор структуры
/// CMS-сервера внутри него. Чистые функции над байтами — без USB и без UI,
/// транспорт (libusb) живёт отдельно, за этим кодеком.
///
/// Кадр: 7E · длина(2, LE, = payload+3) · команда(2, LE) · резерв(1, =0) ·
/// payload · XOR(1, от 7E до конца payload) · 0D 0A.
/// В запросе payload = "000000" + данные; в ответе payload = статус(1, 0=ок) + данные.
/// </summary>
public static class ServiceFrame
{
    private const byte StartByte = 0x7E;
    private const int HeaderLength = 6; // 7E + len16 + cmd16 + резерв
    private const int FrameOverhead = HeaderLength + 3; // + xor + 0D + 0A

    private const int TypeOffset = 0;
    private const int EnabledOffset = 1;
    private const int Domain1Offset = 2;
    private const int Domain1Length = 12;
    private const int Domain2Offset = 14;
    private const int Domain2Length = 12;
    private const int IpOffset = 26;
    private const int IpLength = 32;
    private const int PortOffset = 58;
    private const int ServerDataLength = 62;
    private const int ServerDataMinLength = 60;

    /// <summary>Собирает кадр: 7E, длина, команда, резерв, payload, XOR, 0D 0A.</summary>
    public static byte[] Build(ushort cmd, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + FrameOverhead];
        frame[0] = StartByte;
        WriteUInt16(frame, 1, (ushort)(payload.Length + 3));
        WriteUInt16(frame, 3, cmd);
        frame[5] = 0;
        payload.CopyTo(frame.AsSpan(HeaderLength));

        var xorEnd = HeaderLength + payload.Length;
        frame[xorEnd] = Xor(frame.AsSpan(0, xorEnd));
        frame[xorEnd + 1] = 0x0D;
        frame[xorEnd + 2] = 0x0A;
        return frame;
    }

    /// <summary>Кадр запроса: payload = служебный префикс "000000" + данные.</summary>
    public static byte[] Request(ushort cmd, ReadOnlySpan<byte> data)
    {
        var payload = new byte[6 + data.Length];
        "000000"u8.CopyTo(payload);
        data.CopyTo(payload.AsSpan(6));
        return Build(cmd, payload);
    }

    /// <summary>
    /// Разбирает кадр ответа и возвращает данные без байта статуса. Хвостовые
    /// нулевые байты после 0D 0A (остаток 1024-байтного буфера устройства)
    /// игнорируются. Бросает <see cref="InvalidDataException"/> на любом
    /// несоответствии протоколу: не 7E, заявленная длина не помещается в
    /// буфер, чужая команда, битый XOR, нет 0D 0A, статус не равен нулю.
    /// </summary>
    public static byte[] ParseReply(ushort cmd, ReadOnlySpan<byte> raw)
    {
        if (raw.Length < FrameOverhead || raw[0] != StartByte)
            throw new InvalidDataException("Некорректный кадр: нет начала 7E");

        var declaredLen = ReadUInt16(raw, 1);
        if (declaredLen < 3)
            throw new InvalidDataException("Некорректный кадр: неверная длина");

        var payloadLength = declaredLen - 3;
        var frameLength = HeaderLength + payloadLength + 3;
        if (raw.Length < frameLength)
            throw new InvalidDataException("Некорректный кадр: заявленная длина не помещается в буфер");

        if (ReadUInt16(raw, 3) != cmd)
            throw new InvalidDataException("Некорректный кадр: ответ на другую команду");

        var xorEnd = HeaderLength + payloadLength;
        if (raw[xorEnd] != Xor(raw[..xorEnd]))
            throw new InvalidDataException("Некорректный кадр: неверная контрольная сумма");

        if (raw[xorEnd + 1] != 0x0D || raw[xorEnd + 2] != 0x0A)
            throw new InvalidDataException("Некорректный кадр: нет завершения 0D 0A");

        if (payloadLength < 1)
            throw new InvalidDataException("Некорректный кадр: нет байта статуса");

        var status = raw[HeaderLength];
        if (status != 0)
            throw new InvalidDataException($"Устройство вернуло ошибку, статус {status}");

        return raw.Slice(HeaderLength + 1, payloadLength - 1).ToArray();
    }

    private static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte result = 0;
        foreach (var b in bytes)
            result ^= b;
        return result;
    }

    private static void WriteUInt16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> buf, int offset) =>
        (ushort)(buf[offset] | (buf[offset + 1] << 8));

    /// <summary>
    /// Сервер CMS регистратора (данные 0x0D-ответа после статуса / 0x0C-запроса).
    /// <see cref="Raw"/> хранит исходные 60-62 байта структуры как есть — домены
    /// и резерв могут содержать данные за пределами строки до NUL, и
    /// <see cref="WriteServer"/> берёт их отсюда, чтобы не терять неизвестные байты.
    /// </summary>
    public sealed record CmsServer(byte Type, bool Enabled, string Domain1, string Domain2,
        string Ip, ushort Port, byte[] Raw);

    /// <summary>Разбирает данные структуры сервера (минимум 60 байт, обычно 62).</summary>
    public static CmsServer ReadServer(ReadOnlySpan<byte> replyData)
    {
        if (replyData.Length < ServerDataMinLength)
            throw new InvalidDataException("Некорректный ответ: структура сервера короче 60 байт");

        return new CmsServer(
            Type: replyData[TypeOffset],
            Enabled: replyData[EnabledOffset] != 0,
            Domain1: ReadAsciiZ(replyData.Slice(Domain1Offset, Domain1Length)),
            Domain2: ReadAsciiZ(replyData.Slice(Domain2Offset, Domain2Length)),
            Ip: ReadAsciiZ(replyData.Slice(IpOffset, IpLength)),
            Port: ReadUInt16(replyData, PortOffset),
            Raw: replyData.ToArray());
    }

    /// <summary>
    /// Собирает 62 байта данных для команды записи сервера: копия
    /// <paramref name="current"/>.Raw с типом=2, включением=1 и заменёнными
    /// IP+портом; домены и прочие байты остаются как были прочитаны.
    /// </summary>
    public static byte[] WriteServer(CmsServer current, string ip, ushort port)
    {
        if (ip.Length > IpLength - 1 || !IsDottedIPv4(ip))
            throw new ArgumentException($"Некорректный IP-адрес сервера: {ip}", nameof(ip));

        var data = new byte[ServerDataLength];
        current.Raw.AsSpan(0, Math.Min(current.Raw.Length, ServerDataLength)).CopyTo(data);

        data[TypeOffset] = 2;
        data[EnabledOffset] = 1;
        Array.Clear(data, IpOffset, IpLength);
        Encoding.ASCII.GetBytes(ip).CopyTo(data, IpOffset);
        WriteUInt16(data, PortOffset, port);

        return data;
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul < 0 ? field : field[..nul]);
    }

    /// <summary>
    /// Правило адреса, общее с <see cref="ServiceProvisioner"/>: точечная IPv4.
    /// Каждая часть — 1-3 ASCII-цифры без знака и пробелов (int.TryParse их
    /// пропускает, а адрес с ними для регистратора не годится).
    /// </summary>
    internal static bool IsDottedIPv4(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || part.Any(c => c is < '0' or > '9'))
                return false;
            if (int.Parse(part) > 255)
                return false;
        }
        return true;
    }
}
