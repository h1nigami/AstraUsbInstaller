using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public async Task An_unexpected_button_error_is_visible_and_can_be_dismissed()
    {
        var window = new MainWindow(new MainWindowViewModel(() => []));
        var model = (MainWindowViewModel)window.DataContext!;
        window.Show();
        try
        {
            var layoutButton = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content, "Список"));
            layoutButton.Click += (_, _) => throw new IOException("SYSTEM_DETAILS_BUTTON_TEST");
            Dispatcher.UIThread.Post(() => layoutButton.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
            window.UpdateLayout();

            var visibleText = string.Join(" ", window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            Assert.Contains("Не удалось выполнить действие", visibleText);
            Assert.DoesNotContain("SYSTEM_DETAILS_BUTTON_TEST", visibleText);
            Assert.Contains("SYSTEM_DETAILS_BUTTON_TEST", File.ReadAllText(CrashLog.FilePath));
            var dismiss = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content, "Понятно") && b.IsEffectivelyVisible);
            Click(window, dismiss);
            Assert.False(dismiss.IsEffectivelyVisible);
            Assert.True(window.IsVisible);
        }
        finally
        {
            model.ExitCommand.Execute(null);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            model.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Expanded_search_filters_leave_results_and_actions_on_screen()
    {
        var model = new MainWindowViewModel(() => []);
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = 800, Height = 600 };
        window.Show();
        try
        {
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            await Task.Yield();
            window.UpdateLayout();
            var filters = window.GetVisualDescendants().OfType<Button>()
                .Single(b => Equals(b.Content, model.Search.FiltersLabel));
            Click(window, filters);
            Assert.True(model.Search.FiltersExpanded);
            window.UpdateLayout();
            var results = window.GetVisualDescendants().OfType<ListBox>()
                .Single(b => ReferenceEquals(b.ItemsSource, model.Search.Results));
            Assert.True(results.Bounds.Height >= 60, $"Высота результатов: {results.Bounds.Height}");
            foreach (var label in new[] { "Смотреть", model.Search.ExportLabel, model.Search.DeleteLabel })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
                var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), window)!.Value.Y;
                Assert.True(bottom <= window.ClientSize.Height - 30, $"Кнопка {label} заканчивается на {bottom}");
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

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
