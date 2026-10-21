using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SettingsLayoutTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-settings-layout-").FullName;

    public SettingsLayoutTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaTheory]
    [InlineData(780, 560)]
    [InlineData(800, 600)]
    [InlineData(1024, 768)]
    [InlineData(1280, 800)]
    public async Task Every_settings_field_and_action_is_reachable_without_horizontal_clipping(int width, int height)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, width, height);
        var problems = new List<string>();
        try
        {
            foreach (var section in new[] { "station", "access", "ftp", "sql", "web", "about" })
            {
                model.Settings.Section = section;
                model.Settings.ResetArmed = true;
                await Task.Yield();
                Pump(window);
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                    .Single(s => s.Classes.Contains("fade") && s.IsEffectivelyVisible);
                var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                var controls = scroll.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && (c is Button { Command: not null } || c is CheckBox
                        || c is TextBox && c.Classes.Contains("form")
                        || c is ComboBox && c.Classes.Contains("form"))).ToArray();
                Assert.NotEmpty(controls);
                Assert.True(viewport.Bounds.Height >= 52);
                foreach (var control in controls)
                {
                    control.BringIntoView();
                    Pump(window);
                    var bounds = RelativeRect(control, viewport);
                    var label = control is ContentControl content ? content.Content : control is TextBox text ? text.Text : control.GetType().Name;
                    if (bounds.Left < -1 || bounds.Right > viewport.Bounds.Width + 1)
                        problems.Add($"{section}: {label}, по горизонтали {bounds}, область {viewport.Bounds}");
                    if (bounds.Top < -1 || bounds.Bottom > viewport.Bounds.Height + 1)
                        problems.Add($"{section}: {label}, по вертикали {bounds}, область {viewport.Bounds}");
                }
                if (section == "about")
                {
                    model.Settings.ResetArmed = false;
                    Pump(window);
                    var reset = scroll.GetVisualDescendants().OfType<Button>()
                        .Single(b => Equals(b.Content, "Сбросить до заводских"));
                    reset.BringIntoView();
                    Pump(window);
                    var bounds = RelativeRect(reset, viewport);
                    Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.Bounds.Width + 1
                        && bounds.Top >= -1 && bounds.Bottom <= viewport.Bounds.Height + 1);
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Settings_labels_and_long_statuses_stay_readable_after_repeated_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, 800, 600);
        var problems = new List<string>();
        try
        {
            foreach (var section in new[] { "station", "access", "ftp", "sql", "web", "about" })
            {
                model.Settings.Section = section;
                model.Settings.FtpState = "Не удалось отправить записи на сервер: проверьте доступность сервера, имя учётной записи и папку хранения архива. Файлы остаются в очереди.";
                model.Settings.SqlState = "Не удалось установить соединение с сервером внешней базы: проверьте параметры подключения и доступность сервера в локальной сети.";
                model.Settings.WebState = "Параметры веб-панели сохранены. Перезапустите программу, чтобы новые настройки защищённого соединения вступили в силу.";
                model.Settings.WebLinks.Clear();
                model.Settings.WebLinks.Add("https://[2001:0db8:85a3:0000:0000:8a2e:0370:7334]:65535");
                model.Settings.WebSsl = true;
                model.Settings.WebFingerprint = "SHA256 " + new string('A', 95);
                model.Settings.BackupRoot = Path.Combine(_dir, new string('p', 220));
                model.Settings.Hint = "Не удалось выбрать папку архива. Проверьте путь, подключение диска и права на запись: "
                    + model.Settings.BackupRoot;
                foreach (var size in new[] { new Size(800, 600), new Size(780, 560), new Size(1280, 800), new Size(1024, 768), new Size(780, 560), new Size(800, 600) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    var platform = window.PlatformImpl!;
                    platform.GetType().GetMethod("Resize")!.Invoke(platform, [size, WindowResizeReason.User]);
                    await Task.Yield();
                    Pump(window);
                    Assert.Equal(size, window.ClientSize);
                    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
                        .Single(s => s.Classes.Contains("fade") && s.IsEffectivelyVisible);
                    var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
                    Assert.True(viewport.Bounds.Height >= 52, $"{section}, {size}: область ниже высоты поля");
                    foreach (var text in scroll.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
                    {
                        if (text.GetVisualAncestors().Any(a => a is TextBox || a is ComboBox)) continue;
                        var bounds = RelativeRect(text, viewport);
                        if (bounds.Left < -1 || bounds.Right > viewport.Bounds.Width + 1
                            || text.TextLayout.Width > text.Bounds.Width + 1
                            || text.TextLayout.Height > text.Bounds.Height + 1
                            || text.TextLayout.TextLines.Any(line => line.HasCollapsed || line.HasOverflowed))
                            problems.Add($"{section}, {size}: {text.Text}, {bounds}, текст {text.TextLayout.Width}×{text.TextLayout.Height}, область {viewport.Bounds}");
                    }
                    foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav")))
                    {
                        foreach (var text in button.GetVisualDescendants().OfType<TextBlock>())
                            if (text.TextLayout.Width > text.Bounds.Width + 1 || text.TextLayout.Height > text.Bounds.Height + 1)
                                problems.Add($"меню, {size}: {text.Text}, {text.Bounds}, текст {text.TextLayout.Width}×{text.TextLayout.Height}");
                    }
                    var status = window.GetVisualDescendants().OfType<TextBlock>()
                        .Single(t => t.IsEffectivelyVisible && t.Text == model.Settings.Hint);
                    Assert.All(status.TextLayout.TextLines, line => Assert.False(line.HasCollapsed || line.HasOverflowed));
                    var statusBounds = RelativeRect(status, window);
                    Assert.True(statusBounds.Right <= window.ClientSize.Width && statusBounds.Bottom <= window.ClientSize.Height - 25);
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }
        finally { Close(model); }
    }

    private static MainWindow Open(MainWindowViewModel model, int width, int height)
    {
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = width, Height = height };
        window.Show();
        Pump(window);
        Assert.Equal(new Size(width, height), window.ClientSize);
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 5;
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
        return window;
    }

    private static Rect RelativeRect(Control control, Visual relative) => new(
        control.TranslatePoint(default, relative)!.Value, control.Bounds.Size);

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Close(MainWindowViewModel model)
    {
        model.ExitCommand.Execute(null);
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
