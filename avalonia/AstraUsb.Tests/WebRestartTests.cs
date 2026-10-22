using System.Net;
using System.Net.Sockets;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class WebRestartTests
{
    // Сохранение настроек панели перезапускает её. Остановка сервера в потоке
    // окна вешала программу, поэтому команда обязана завершиться, а не ждать.
    [AvaloniaFact]
    public async Task Saving_web_settings_twice_restarts_the_panel_without_hanging()
    {
        AppPaths.Root = Path.Combine(Path.GetTempPath(), "web-restart-" + Guid.NewGuid().ToString("N"));
        AppPaths.EnsureCreated();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var model = new MainWindowViewModel(() => []);
        model.Settings.WebEnabled = true;
        model.Settings.WebSsl = false;
        model.Settings.WebPort = port;

        await model.Settings.SaveWebCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("работает", model.Settings.WebState);

        await model.Settings.SaveWebCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("работает", model.Settings.WebState);

        model.Settings.WebEnabled = false;
        await model.Settings.SaveWebCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("панель выключена", model.Settings.WebState);
    }
}
