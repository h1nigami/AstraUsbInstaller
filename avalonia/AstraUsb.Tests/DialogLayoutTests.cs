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
public sealed class DialogLayoutTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-dialog-layout-").FullName;

    public DialogLayoutTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaFact]
    public async Task Bay_details_stay_readable_after_repeated_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            var port = model.Ports[0];
            port.State = PortState.Copying;
            port.CameraId = "64001";
            port.PersonnelNo = "00001234567890123456";
            port.Employee = "Константинопольский Александр Александрович";
            port.Department = "Отдел обеспечения безопасности и оперативного сопровождения перевозок";
            port.FilesLine = "Скопировано 12345 из 45678 файлов, осталось 12 часов 45 минут";
            port.Progress = .68;
            model.OpenBayCommand.Execute(port);
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                AssertReadable(Text(window, port.Employee), window);
                AssertReadable(Text(window, port.Department), window);
                AssertReadable(Text(window, port.PersonnelLine), window);
                AssertReadable(Text(window, port.FilesLine), window);
                AssertOnScreen(Button(window, "Закрыть"), window);
                AssertOnScreen(Text(window, port.Employee).GetVisualAncestors().OfType<Border>().First(), window);
            }
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Viewer_note_and_controls_stay_readable_after_repeated_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            model.Search.ViewerVisible = true;
            model.Search.ViewerTitle = "64001_20261020_170314_сопровождение_автомобильной_колонны_по_маршруту_Екатеринбург_Нижний_Тагил.txt";
            model.Search.ViewerNote = "1,4 ГБ, снято 20.10.2026 17:03:14, без воспроизведения звука на станции, откройте запись в системе";
            model.Search.ViewerText = string.Join(Environment.NewLine,
                Enumerable.Repeat("Запись журнала регистратора: проверка содержимого и прокрутки в окне просмотра.", 40));
            model.Search.ViewerLength = 3661;
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                AssertReadable(Text(window, model.Search.ViewerTitle), window);
                AssertReadable(Text(window, model.Search.ViewerNote), window);
                AssertOnScreen(Button(window, "Закрыть"), window);
                AssertOnScreen(Button(window, "Открыть в системе"), window);
                AssertOnScreen(Button(window, "< 10 с"), window);
                AssertOnScreen(Button(window, "10 с >"), window);
                AssertReadable(Text(window, model.Search.ViewerTimeLabel), window);
            }
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Password_keyboard_and_actions_stay_inside_window_after_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            model.ExitCommand.Execute(null);
            model.PasswordError = "Учётная запись или пароль не подошли. Проверьте регистр символов и повторите ввод.";
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                foreach (var key in window.GetVisualDescendants().OfType<Button>()
                             .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("key")))
                    AssertOnScreen(key, window);
                AssertOnScreen(window.FindControl<TextBox>("AccountBox")!, window);
                AssertOnScreen(window.FindControl<TextBox>("PasswordBox")!, window);
                AssertOnScreen(Button(window, "Войти"), window);
                AssertOnScreen(Button(window, "Отмена"), window);
            }
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Update_wait_and_error_message_keep_their_actions_accessible_after_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            model.OfflineWaiting = true;
            model.OfflineStatus = "Не удалось прочитать файл обновления с флешки. Проверьте архив и контрольную сумму, затем подключите носитель повторно.";
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                AssertReadable(Text(window, model.OfflineStatus), window);
                AssertOnScreen(Button(window, "Отмена"), window);
            }
            model.OfflineWaiting = false;
            model.ErrorMessage = "Не удалось выполнить действие. Проверьте подключение носителя и повторите попытку. Если ошибка повторится, сохраните диагностику в разделе журнала станции.";
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                AssertReadable(Text(window, model.ErrorMessage), window);
                AssertOnScreen(Button(window, "Понятно"), window);
            }
        }
        finally { Close(model); }
    }

    private static IEnumerable<Size> Sizes() =>
        new[] { new Size(780, 560), new(800, 600), new(1024, 768), new(1280, 800), new(780, 560), new(800, 600) };

    private static MainWindow Open(MainWindowViewModel model)
    {
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = 800, Height = 600 };
        window.Show();
        return window;
    }

    private static async Task Resize(Window window, Size size)
    {
        window.Width = size.Width;
        window.Height = size.Height;
        var platform = window.PlatformImpl!;
        platform.GetType().GetMethod("Resize")!.Invoke(platform, [size, WindowResizeReason.User]);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(size, window.ClientSize);
    }

    private static TextBlock Text(Window window, string text) => window.GetVisualDescendants().OfType<TextBlock>()
        .Single(t => t.IsEffectivelyVisible && t.Text == text);

    private static Button Button(Window window, string label) => window.GetVisualDescendants().OfType<Button>()
        .Single(b => b.IsEffectivelyVisible && Equals(b.Content, label));

    private static void AssertReadable(TextBlock text, Window window)
    {
        AssertOnScreen(text, window);
        Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 1,
            $"Ширина текста «{text.Text}»: {text.TextLayout.WidthIncludingTrailingWhitespace}, поле {text.Bounds.Width}");
        Assert.True(text.TextLayout.Height <= text.Bounds.Height + 1,
            $"Высота текста «{text.Text}»: {text.TextLayout.Height}, поле {text.Bounds.Height}");
        Assert.All(text.TextLayout.TextLines, line => Assert.False(line.HasOverflowed || line.HasCollapsed,
            $"Обрезана строка «{text.Text}» при размере {window.ClientSize}; поле {text.Bounds.Size}"));
    }

    private static void AssertOnScreen(Control control, Window window)
    {
        var topLeft = control.TranslatePoint(default, window)!.Value;
        var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value;
        Assert.True(topLeft.X >= 0 && topLeft.Y >= 0 && bottomRight.X <= window.ClientSize.Width
                    && bottomRight.Y <= window.ClientSize.Height,
            $"Элемент {control.GetType().Name} ({topLeft}, {bottomRight}) вне окна {window.ClientSize}");
    }

    private static void Close(MainWindowViewModel model)
    {
        model.ErrorMessage = "";
        model.OfflineWaiting = false;
        model.CloseBayCommand.Execute(null);
        model.Search.CloseViewerCommand.Execute(null);
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
