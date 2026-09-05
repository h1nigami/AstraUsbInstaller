using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AstraUsb.Tests.TestAppBuilder))]

namespace AstraUsb.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[Collection("Каталог данных")]
public sealed class MainWindowTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-window-").FullName;

    public MainWindowTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaFact]
    public void Closing_the_window_requires_the_exit_password()
    {
        var window = new MainWindow(new MainWindowViewModel(() => []));
        var model = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(model.PasswordVisible);
            model.AccountInput = PasswordGate.DefaultAccount;
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            Assert.False(window.IsVisible);
        }
        finally { model.Dispose(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Working_inside_a_tab_renews_the_access_timeout(bool keyboard)
    {
        var window = new MainWindow(new MainWindowViewModel(() => []));
        var model = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            var access = new AccessGuard(10);
            access.Unlock(DateTime.Now.AddMinutes(-9));
            typeof(MainWindowViewModel).GetField("_access", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, access);
            if (keyboard)
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            else
                window.MouseDown(new Point(200, 200), MouseButton.Left);

            Assert.True(access.Check(DateTime.Now.AddMinutes(2)));
        }
        finally
        {
            model.AccountInput = PasswordGate.DefaultAccount;
            model.PasswordInput = PasswordGate.Default();
            model.ExitCommand.Execute(null);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            model.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task All_protected_tabs_render_after_login()
    {
        var window = new MainWindow(new MainWindowViewModel(() => []));
        var model = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("Tabs")!;
            tabs.SelectedIndex = 1;
            Assert.Equal(0, tabs.SelectedIndex);
            Assert.True(model.PasswordVisible);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);

            for (var i = 1; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                await Task.Yield();
                window.UpdateLayout();
                Assert.Equal(i, tabs.SelectedIndex);
                Assert.False(model.PasswordVisible);
                Assert.True(window.IsVisible);
            }
        }
        finally
        {
            model.ExitCommand.Execute(null);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            model.Dispose();
        }
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
