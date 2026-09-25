using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Кодек кадра сервисного USB-протокола регистратора A11 сверяется побайтно
/// с эталонными векторами, снятыми с живого устройства 24.09.2026
/// (.superpowers/sdd/2026-09-24-service-mode-usb-native/golden.txt).
/// </summary>
public sealed class ServiceFrameTests
{
    private const ushort CmdLogin = 0x01;
    private const ushort CmdServer = 0x0D;

    private static readonly byte[] LoginReply = Hex("7e 04 00 01 00 00 00 7b 0d 0a");
    private static readonly byte[] LoginRequest = Hex("7e 09 00 01 00 00 30 30 30 30 30 30 76 0d 0a");
    private static readonly byte[] ServerRequest = Hex("7e 09 00 0d 00 00 30 30 30 30 30 30 7a 0d 0a");

    private static readonly byte[] ServerReply = Hex("""
        7e 42 00 0d 00 00 00 02 01 75 6b 6e 6f 77 6e 00 00 00 00 00 00
        75 6b 6e 6f 77 6e 00 00 00 00 00 00
        31 39 32 2e 31 36 38 2e 30 2e 39 00 00 00 00 00 00 00 00 00 00
        00 00 00 00 00 00 00 00 00 00 00
        d0 19 00 00 d9 0d 0a
        """);

    private static byte[] Hex(string s) => Convert.FromHexString(
        string.Concat(s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Request_matches_the_golden_login_frame()
    {
        Assert.Equal(LoginRequest, ServiceFrame.Request(CmdLogin, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Request_matches_the_golden_server_read_frame()
    {
        Assert.Equal(ServerRequest, ServiceFrame.Request(CmdServer, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseReply_returns_empty_data_for_the_golden_login_reply()
    {
        Assert.Empty(ServiceFrame.ParseReply(CmdLogin, LoginReply));
    }

    [Fact]
    public void ParseReply_returns_the_62_server_bytes_from_the_golden_reply()
    {
        var data = ServiceFrame.ParseReply(CmdServer, ServerReply);

        Assert.Equal(62, data.Length);
        Assert.Equal(ServerReply[7..69], data);
    }

    [Fact]
    public void ParseReply_ignores_trailing_zero_padding_from_the_1024_byte_device_buffer()
    {
        var padded = new byte[1024];
        ServerReply.CopyTo(padded, 0);

        var data = ServiceFrame.ParseReply(CmdServer, padded);

        Assert.Equal(ServerReply[7..69], data);
    }

    [Fact]
    public void ParseReply_rejects_a_broken_checksum()
    {
        var broken = (byte[])LoginReply.Clone();
        broken[7] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => ServiceFrame.ParseReply(CmdLogin, broken));
    }

    [Fact]
    public void ParseReply_rejects_a_nonzero_status()
    {
        // Тот же кадр логина, но статус=1 и пересчитанный XOR.
        var frame = Hex("7e 04 00 01 00 00 01 7a 0d 0a");

        Assert.Throws<InvalidDataException>(() => ServiceFrame.ParseReply(CmdLogin, frame));
    }

    [Fact]
    public void ParseReply_rejects_a_reply_for_a_different_command()
    {
        Assert.Throws<InvalidDataException>(() => ServiceFrame.ParseReply(CmdServer, LoginReply));
    }

    [Fact]
    public void ParseReply_rejects_a_missing_crlf_terminator()
    {
        var broken = (byte[])LoginReply.Clone();
        broken[8] = 0x00;

        Assert.Throws<InvalidDataException>(() => ServiceFrame.ParseReply(CmdLogin, broken));
    }

    [Fact]
    public void ReadServer_parses_the_golden_server_structure()
    {
        var data = ServiceFrame.ParseReply(CmdServer, ServerReply);

        var server = ServiceFrame.ReadServer(data);

        Assert.Equal(2, server.Type);
        Assert.True(server.Enabled);
        Assert.Equal("uknown", server.Domain1);
        Assert.Equal("uknown", server.Domain2);
        Assert.Equal("192.168.0.9", server.Ip);
        Assert.Equal((ushort)6608, server.Port);
    }

    [Fact]
    public void WriteServer_changes_only_type_enabled_ip_and_port()
    {
        var current = ServiceFrame.ReadServer(ServiceFrame.ParseReply(CmdServer, ServerReply));

        var written = ServiceFrame.WriteServer(current, "10.0.0.5", 6608);

        Assert.Equal(62, written.Length);
        var server = ServiceFrame.ReadServer(written);
        Assert.Equal(2, server.Type);
        Assert.True(server.Enabled);
        Assert.Equal("10.0.0.5", server.Ip);
        Assert.Equal((ushort)6608, server.Port);
        Assert.Equal("uknown", server.Domain1);
        Assert.Equal("uknown", server.Domain2);
        // Домены и резерв — те же байты, что были в исходной структуре.
        Assert.Equal(current.Raw[2..26], written[2..26]);
        Assert.Equal(current.Raw[60..62], written[60..62]);
    }

    [Fact]
    public void WriteServer_rejects_an_ip_that_is_not_dotted_ipv4()
    {
        var current = ServiceFrame.ReadServer(ServiceFrame.ParseReply(CmdServer, ServerReply));

        Assert.Throws<ArgumentException>(() => ServiceFrame.WriteServer(current, "not-an-ip", 6608));
    }

    [Fact]
    public void WriteServer_rejects_an_ip_longer_than_31_characters()
    {
        var current = ServiceFrame.ReadServer(ServiceFrame.ParseReply(CmdServer, ServerReply));
        var tooLong = string.Concat(Enumerable.Repeat("1", 32));

        Assert.Throws<ArgumentException>(() => ServiceFrame.WriteServer(current, tooLong, 6608));
    }
}
