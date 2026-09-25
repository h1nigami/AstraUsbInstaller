using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Протокол сервисного режима (<see cref="ServiceProtocol"/>) без реального
/// USB: обмен кадрами подменён делегатом, который записывает отправленные
/// кадры и отдаёт заранее заданные ответы. libusb здесь не участвует —
/// станция разработки Windows, устройства нет, транспорт проверяется
/// на Astra Linux отдельно.
/// </summary>
public sealed class ServiceUsbTests
{
    private static readonly byte[] LoginReply = ServiceFrame.Build(0x01, [0]);
    private static readonly byte[] WriteAckReply = ServiceFrame.Build(0x0C, [0]);

    private static readonly ServiceFrame.CmsServer BlankServer =
        new(0, false, "", "", "", 0, new byte[62]);

    private static byte[] ServerReplyFrame(string ip, ushort port)
    {
        var data = ServiceFrame.WriteServer(BlankServer, ip, port);
        return ServiceFrame.Build(0x0D, [0, .. data]);
    }

    private static ushort CmdOf(byte[] frame) => (ushort)(frame[3] | (frame[4] << 8));

    private sealed class FakeUsb
    {
        private readonly Queue<byte[]> replies;
        public List<byte[]> Sent { get; } = [];
        public int DeviceCount { get; init; } = 1;

        public FakeUsb(params byte[][] replies) => this.replies = new Queue<byte[]>(replies);

        public byte[] Exchange(byte[] frame)
        {
            Sent.Add(frame);
            return replies.Dequeue();
        }

        public int Count() => DeviceCount;
    }

    [Fact]
    public void WriteServer_does_not_send_write_when_settings_already_match()
    {
        var fake = new FakeUsb(LoginReply, ServerReplyFrame("192.168.0.9", 6608));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        var written = protocol.WriteServer("192.168.0.9", 6608);

        Assert.False(written);
        Assert.Equal(2, fake.Sent.Count);
        Assert.DoesNotContain(fake.Sent, f => CmdOf(f) == 0x0C);
    }

    [Fact]
    public void WriteServer_sends_write_then_reads_again_when_settings_differ()
    {
        var fake = new FakeUsb(
            LoginReply,
            ServerReplyFrame("192.168.0.9", 6608),
            WriteAckReply,
            ServerReplyFrame("10.0.0.5", 7000));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        var written = protocol.WriteServer("10.0.0.5", 7000);

        Assert.True(written);
        Assert.Equal([0x01, 0x0D, 0x0C, 0x0D], fake.Sent.Select(CmdOf).ToArray());
    }

    /// <summary>
    /// Снято на A11 25.09.2026: сразу после записи чтение может вернуть
    /// старый ответ (на запись) или кадр чтения без данных — регистратор ещё
    /// не обработал команду. Такое чтение повторяется, а не валит запись.
    /// </summary>
    [Fact]
    public void WriteServer_retries_read_after_write_when_device_answers_stale_or_short()
    {
        var fake = new FakeUsb(
            LoginReply,
            ServerReplyFrame("192.168.0.9", 6608),
            WriteAckReply,
            WriteAckReply,
            ServiceFrame.Build(0x0D, [0]),
            ServerReplyFrame("10.0.0.5", 7000));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        var written = protocol.WriteServer("10.0.0.5", 7000);

        Assert.True(written);
        Assert.Equal([0x01, 0x0D, 0x0C, 0x0D, 0x0D, 0x0D], fake.Sent.Select(CmdOf).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void WriteServer_throws_before_any_exchange_when_device_count_is_not_one(int deviceCount)
    {
        var fake = new FakeUsb { DeviceCount = deviceCount };
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        Assert.Throws<IOException>(() => protocol.WriteServer("10.0.0.5", 7000));
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void WriteServer_throws_when_read_after_write_does_not_match()
    {
        var fake = new FakeUsb(
            LoginReply,
            ServerReplyFrame("192.168.0.9", 6608),
            WriteAckReply,
            ServerReplyFrame("192.168.0.9", 6608));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        Assert.Throws<IOException>(() => protocol.WriteServer("10.0.0.5", 7000));
    }

    /// <summary>
    /// Снято на A11 25.09.2026: сразу после команды IN может вернуть старый
    /// ответ предыдущей команды, тут — битый кадр на месте подтверждения
    /// записи. Запись из-за этого не повторяется и сама по себе не считается
    /// ошибкой — судит уже чтение после записи (оно ретраится само).
    /// </summary>
    [Fact]
    public void WriteServer_does_not_retry_a_garbage_write_ack_and_fails_when_read_after_write_does_not_confirm()
    {
        var fake = new FakeUsb(
            LoginReply,
            ServerReplyFrame("192.168.0.9", 6608),
            [0xFF],
            ServerReplyFrame("192.168.0.9", 6608));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        Assert.Throws<IOException>(() => protocol.WriteServer("10.0.0.5", 7000));
        Assert.Equal(4, fake.Sent.Count);
        Assert.Single(fake.Sent, f => CmdOf(f) == 0x0C);
    }

    [Fact]
    public void WriteServer_does_not_retry_a_garbage_write_ack_and_succeeds_when_read_after_write_confirms()
    {
        var fake = new FakeUsb(
            LoginReply,
            ServerReplyFrame("192.168.0.9", 6608),
            [0xFF],
            ServerReplyFrame("10.0.0.5", 7000));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        var written = protocol.WriteServer("10.0.0.5", 7000);

        Assert.True(written);
        Assert.Equal(4, fake.Sent.Count);
        Assert.Single(fake.Sent, f => CmdOf(f) == 0x0C);
    }

    [Fact]
    public void ReadServer_returns_null_when_no_device_is_connected()
    {
        var fake = new FakeUsb { DeviceCount = 0 };
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        Assert.Null(protocol.ReadServer());
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void ReadServer_throws_when_more_than_one_device_is_connected()
    {
        var fake = new FakeUsb { DeviceCount = 2 };
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        Assert.Throws<IOException>(() => protocol.ReadServer());
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public void ReadServer_retries_when_reply_parsing_fails_then_succeeds()
    {
        var fake = new FakeUsb(LoginReply, [0xFF], ServerReplyFrame("192.168.0.9", 6608));
        var protocol = new ServiceProtocol(fake.Exchange, fake.Count);

        var server = protocol.ReadServer();

        Assert.Equal("192.168.0.9", server!.Ip);
        Assert.Equal(3, fake.Sent.Count);
    }
}
