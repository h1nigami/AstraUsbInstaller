using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class LayoutStabilityTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-layout-").FullName;

    public LayoutStabilityTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false, BayCount = 10, BaysPerRow = 5 }.Save();
    }

    [AvaloniaFact]
    public async Task Tabs_stay_on_one_row_after_repeated_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, 800, 600);
        try
        {
            foreach (var width in new[] { 800, 780, 1280, 780, 800 })
            {
                Resize(window, new Size(width, 600));
                await Task.Yield();
                Pump(window);
                var tabs = window.FindControl<TabControl>("Tabs")!.GetVisualDescendants().OfType<TabItem>().ToArray();
                Assert.Equal(6, tabs.Length);
                Assert.All(tabs, tab => Assert.Equal(tabs[0].Bounds.Top, tab.Bounds.Top));
                Assert.All(tabs, tab => Assert.True(RectInWindow(tab, window).Right <= window.ClientSize.Width));
            }
        }
        finally { Close(model); }
    }

    [AvaloniaTheory]
    [InlineData(780, 560, false)]
    [InlineData(780, 560, true)]
    [InlineData(800, 600, false)]
    [InlineData(800, 600, true)]
    [InlineData(1280, 800, false)]
    [InlineData(1280, 800, true)]
    public async Task Search_shows_a_complete_field_row_and_keeps_results_and_actions(int width, int height, bool expanded)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, width, height);
        try
        {
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            model.Search.From = "13.10.2026";
            model.Search.PersonnelNo = "10482";
            if (expanded) model.Search.ToggleFiltersCommand.Execute(null);
            await Task.Yield();
            Pump(window);
            var field = window.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.IsEffectivelyVisible && box.Text == (expanded ? "10482" : "13.10.2026"));
            var group = field.GetVisualAncestors().OfType<StackPanel>().First();
            var scroll = field.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.InRange(field.Bounds.Height, 28, 32);
            Assert.True(scroll.Viewport.Height >= group.Bounds.Height,
                $"Область полей {scroll.Viewport.Height}, полная строка {group.Bounds.Height}");
            var results = window.GetVisualDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, model.Search.Results));
            var minimum = height >= 800 ? 400 : height >= 600 ? (expanded ? 200 : 240) : (expanded ? 160 : 200);
            Assert.True(results.Bounds.Height >= minimum,
                $"Для данных нужно не меньше {minimum} px, получено {results.Bounds.Height}");
            foreach (var label in new[] { "Смотреть", model.Search.ExportLabel, model.Search.DeleteLabel })
            {
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
                Assert.True(RectInWindow(button, window).Bottom <= window.ClientSize.Height - 30);
            }
        }
        finally { Close(model); }
    }

    [AvaloniaTheory]
    [InlineData(780, 560)]
    [InlineData(800, 600)]
    [InlineData(1280, 800)]
    public async Task Bay_text_has_room_for_percentage_and_the_full_safety_hint(int width, int height)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, width, height);
        try
        {
            var port = model.Ports[0];
            port.CameraId = "64001";
            port.State = PortState.Copying;
            port.Progress = .68;
            await Task.Yield();
            Pump(window);
            var bay = Bay(window, port);
            var texts = bay.GetVisualDescendants().OfType<TextBlock>().ToArray();
            var state = texts.Single(t => t.Text == "Копирование");
            var percent = texts.Single(t => t.Text == "68%");
            var hint = texts.Single(t => t.Text == "Не извлекайте регистратор");
            Assert.False(RectInWindow(state, window).Inflate(3).Intersects(RectInWindow(percent, window)));
            Assert.All(hint.TextLayout.TextLines, line => Assert.False(line.HasCollapsed || line.HasOverflowed));
            Assert.True(RectInWindow(hint, window).Bottom <= RectInWindow(bay, window).Bottom);
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Search_fits_at_least_eight_complete_results_at_800_by_600()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, 800, 600);
        try
        {
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            for (var i = 0; i < 20; i++)
            {
                var path = Path.Combine(_dir, $"VID_{i:000}.mp4");
                model.Search.Results.Add(new FoundFile
                {
                    Row = new ArchiveRow(new CollectedFile(64001, path, 1024, null, DateTime.Now), "64001", "", "", ""),
                    Camera = "64001", FileName = Path.GetFileName(path), Path = path,
                    Size = "1 МБ", Kind = "видео", Employee = "Иванов И.И.",
                    ShotAt = "20.10 17:03", CollectedAt = "20.10 17:10",
                });
            }
            await Task.Yield();
            Pump(window);
            var results = window.GetVisualDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, model.Search.Results));
            var viewport = RectInWindow(results, window);
            var visible = results.GetVisualDescendants().OfType<ListBoxItem>()
                .Count(item => item.IsEffectivelyVisible && RectInWindow(item, window).Top >= viewport.Top
                    && RectInWindow(item, window).Bottom <= viewport.Bottom);
            Assert.True(visible >= 8, $"Целиком видно {visible} строк, область данных {viewport.Height} px");
        }
        finally { Close(model); }
    }

    [AvaloniaTheory]
    [InlineData("grid", .68, PortState.Copying)]
    [InlineData("list", .37, PortState.Copying)]
    [InlineData("grid", 1, PortState.Done)]
    public async Task Progress_matches_the_fraction_at_every_card_width(string layout, double fraction, PortState state)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model, 800, 600);
        try
        {
            model.SetLayoutCommand.Execute(layout);
            var port = model.Ports[0];
            port.CameraId = "64001";
            port.State = state;
            port.Progress = fraction;
            foreach (var width in new[] { 800, 780, 1280, 800 })
            {
                Resize(window, new Size(width, 600));
                await Task.Yield();
                await Task.Delay(500);
                Pump(window);
                var fill = Bay(window, port).GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("fill") || border.Name == "PART_Indicator");
                var track = (Control)fill.GetVisualParent()!;
                Assert.InRange(fill.Bounds.Width / track.Bounds.Width, fraction - .02, fraction + .02);
            }
        }
        finally { Close(model); }
    }

    private static Border Bay(Window window, PortViewModel port) => window.GetVisualDescendants().OfType<Border>()
        .Single(b => b.Classes.Contains("bay") && b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, port));

    private static Rect RectInWindow(Control control, Window window) => new(
        control.TranslatePoint(default, window)!.Value,
        control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value);

    private static MainWindow Open(MainWindowViewModel model, int width, int height)
    {
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = width, Height = height };
        window.Show();
        Resize(window, new Size(width, height));
        return window;
    }

    private static void Resize(Window window, Size size)
    {
        window.Width = size.Width;
        window.Height = size.Height;
        var platform = window.PlatformImpl!;
        platform.GetType().GetMethod("Resize")!.Invoke(platform, [size, WindowResizeReason.User]);
        Pump(window);
        Assert.Equal(size, window.ClientSize);
    }

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
