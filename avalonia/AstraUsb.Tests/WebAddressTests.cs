using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Адреса веб-панели. Панель слушает на самой станции, и заходят на неё по
/// её адресу в локальной сети: без этой подсказки оператору адрес взять
/// негде.
/// </summary>
public sealed class WebAddressTests
{
    [Fact]
    public void A_secured_panel_is_addressed_over_https()
    {
        Assert.Equal("https://192.168.0.28:8443",
            WebAddress.Link("192.168.0.28", 8443, ssl: true));
    }

    [Fact]
    public void An_open_panel_is_addressed_over_http()
    {
        Assert.Equal("http://10.0.0.5:8080",
            WebAddress.Link("10.0.0.5", 8080, ssl: false));
    }

    [Fact]
    public void The_station_lists_the_addresses_it_answers_on()
    {
        var addresses = WebAddress.Local();

        // Обратная петля в список не идёт: по ней на станцию заходят только
        // с самой станции, а панель нужна с телефона рядом.
        Assert.DoesNotContain("127.0.0.1", addresses);
        Assert.All(addresses, a => Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", a));
    }
}
