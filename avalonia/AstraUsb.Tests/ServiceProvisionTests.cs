using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Решение об авто-настройке CMS-сервера при подключении A11
/// (<see cref="ServiceProvisioner"/>) без реального USB — подставной
/// <see cref="FakeUsb"/> считает вызовы и подсказывает ответ.
/// </summary>
public sealed class ServiceProvisionTests
{
    private sealed class FakeUsb : IServiceUsb
    {
        public int DeviceCount { get; set; } = 1;
        public int CountCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public Func<string, ushort, bool>? OnWrite { get; set; }
        public Exception? ThrowOnWrite { get; set; }
        public Exception? ThrowOnCount { get; set; }

        public int Count()
        {
            CountCalls++;
            if (ThrowOnCount is not null)
                throw ThrowOnCount;
            return DeviceCount;
        }

        public ServiceFrame.CmsServer? ReadServer()
        {
            ReadCalls++;
            return null;
        }

        public bool WriteServer(string ip, ushort port)
        {
            WriteCalls++;
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;
            return OnWrite?.Invoke(ip, port) ?? true;
        }
    }

    private static Settings HostSettings(string host = "192.168.0.9", int port = 6608) =>
        new() { CmsServerHost = host, CmsServerPort = port };

    [Fact]
    public void Empty_host_does_not_touch_usb_at_all()
    {
        var usb = new FakeUsb();
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(new Settings { CmsServerHost = "" });

        Assert.Null(status);
        Assert.Equal(0, usb.CountCalls);
        Assert.Equal(0, usb.ReadCalls);
        Assert.Equal(0, usb.WriteCalls);
    }

    [Fact]
    public void No_device_reports_nothing()
    {
        var usb = new FakeUsb { DeviceCount = 0 };
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings());

        Assert.Null(status);
        Assert.Equal(0, usb.WriteCalls);
    }

    [Fact]
    public void More_than_one_device_reports_and_does_not_read_or_write()
    {
        var usb = new FakeUsb { DeviceCount = 2 };
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings());

        Assert.Equal("Для настройки подключите только один A11 по USB.", status);
        Assert.Equal(0, usb.ReadCalls);
        Assert.Equal(0, usb.WriteCalls);
    }

    [Fact]
    public void One_device_with_a_mismatch_writes_once_and_reports_success()
    {
        var usb = new FakeUsb();
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings());

        Assert.Equal("Сервер 192.168.0.9:6608 записан. Отключите USB и перезагрузите A11.", status);
        Assert.Equal(1, usb.WriteCalls);
    }

    [Fact]
    public void One_device_already_matching_reports_without_writing_again()
    {
        var usb = new FakeUsb { OnWrite = (_, _) => false };
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings());

        Assert.Equal("Сервер 192.168.0.9:6608 уже настроен.", status);
    }

    [Fact]
    public void A_second_step_with_the_device_still_present_does_not_write_again()
    {
        var usb = new FakeUsb();
        var provisioner = new ServiceProvisioner(usb);
        var settings = HostSettings();

        var first = provisioner.Step(settings);
        var second = provisioner.Step(settings);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, usb.WriteCalls);
    }

    [Fact]
    public void Unplugging_clears_the_remembered_attempt_so_the_next_presence_configures_again()
    {
        var usb = new FakeUsb();
        var provisioner = new ServiceProvisioner(usb);
        var settings = HostSettings();

        provisioner.Step(settings);
        usb.DeviceCount = 0;
        provisioner.Step(settings);
        usb.DeviceCount = 1;
        var third = provisioner.Step(settings);

        Assert.NotNull(third);
        Assert.Equal(2, usb.WriteCalls);
    }

    [Fact]
    public void An_exception_is_reported_once_and_not_retried_while_the_device_stays_present()
    {
        var usb = new FakeUsb { ThrowOnWrite = new IOException("устройство не отвечает") };
        var provisioner = new ServiceProvisioner(usb);
        var settings = HostSettings();

        var first = provisioner.Step(settings);
        var second = provisioner.Step(settings);

        Assert.Equal("USB: устройство не отвечает", first);
        Assert.Null(second);
        Assert.Equal(1, usb.WriteCalls);
    }

    [Fact]
    public void A_libusb_failure_on_count_is_reported_as_a_usb_error_and_does_not_throw()
    {
        var usb = new FakeUsb { ThrowOnCount = new DllNotFoundException("libusb-1.0.so.0") };
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings());

        Assert.Equal("USB: libusb-1.0.so.0", status);
        Assert.Equal(0, usb.WriteCalls);
    }

    [Fact]
    public void An_invalid_host_is_reported_without_writing()
    {
        var usb = new FakeUsb();
        var provisioner = new ServiceProvisioner(usb);

        var status = provisioner.Step(HostSettings("не-адрес"));

        Assert.Equal("Адрес сервера в настройках неверен.", status);
        Assert.Equal(0, usb.WriteCalls);
    }
}
