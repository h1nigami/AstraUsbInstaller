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

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SecondaryTabsLayoutTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-secondary-layout-").FullName;
    private const string LongName = "Константинопольский Александр Александрович";
    private const string LongDepartment = "Управление охраны / Отдел оперативного реагирования / Северо-западное подразделение";

    public SecondaryTabsLayoutTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Secondary_tab_actions_remain_inside_the_window_after_resizing(int tab)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, tab);
            FillRows(model);
            var failures = new List<string>();
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                foreach (var button in window.FindControl<TabControl>("Tabs")!.GetVisualDescendants()
                             .OfType<Button>().Where(button => button.IsEffectivelyVisible))
                {
                    var rect = InWindow(button, window);
                    if (rect.Left < 0 || rect.Top < 0 || rect.Right > window.ClientSize.Width + 1
                        || rect.Bottom > window.ClientSize.Height - 29)
                        failures.Add($"{size}: {button.Content}: {rect}");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { Close(model); }
    }

    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Long_directory_values_do_not_paint_over_adjacent_columns(int tab)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, tab);
            FillRows(model);
            var failures = new List<string>();
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                var lists = window.FindControl<TabControl>("Tabs")!.GetVisualDescendants().OfType<ListBox>()
                    .Where(list => list.IsEffectivelyVisible);
                foreach (var text in lists.SelectMany(list => list.GetVisualDescendants().OfType<TextBlock>())
                             .Where(text => text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text)))
                {
                    if (text.TextLayout.Width > text.Bounds.Width + 1
                        || text.TextLayout.Height > text.Bounds.Height + 1)
                        failures.Add($"{size}: {text.Text}: текст {text.TextLayout.Width:F1}x{text.TextLayout.Height:F1}, поле {text.Bounds.Size}");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Directory_confirmation_messages_stay_inside_their_panel()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, 3);
            var staff = new StaffDirectory(AppPaths.Database);
            var department = staff.AddDepartment(LongDepartment);
            staff.AddEmployee(LongName, "001234567890", "+7 (343) 123-45-67 доб. 12345", departmentId: department);
            model.Staff.Reload();
            model.Staff.Selected = model.Staff.Employees.Single();
            model.Staff.DeleteEmployeeCommand.Execute(null);
            var deletedEmployeeHint = model.Staff.Hint;
            var failures = new List<string>();
            foreach (var hint in new[] { deletedEmployeeHint })
            {
                model.Staff.Hint = hint;
                foreach (var size in Sizes())
                {
                    await Resize(window, size);
                    var text = window.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.IsEffectivelyVisible && text.Text == hint);
                    var rect = InWindow(text, window);
                    if (text.TextLayout.Width > text.Bounds.Width + 1 || text.TextLayout.Height > text.Bounds.Height + 1
                        || rect.Right > window.ClientSize.Width + 1 || rect.Bottom > window.ClientSize.Height - 29)
                        failures.Add($"{size}: {hint}: текст {text.TextLayout.Width:F1}x{text.TextLayout.Height:F1}, поле {rect}");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Long_log_messages_remain_readable_after_resizing()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, 4);
            FillRows(model);
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                var message = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.IsEffectivelyVisible && text.Text == model.Log.Entries[0].Message);
                Assert.All(message.TextLayout.TextLines, line =>
                    Assert.False(line.HasCollapsed || line.HasOverflowed,
                        $"{size}: длинное сообщение журнала обрезано"));
                Assert.True(message.TextLayout.Height <= message.Bounds.Height + 1,
                    $"{size}: текст {message.TextLayout.Height}, строка {message.Bounds.Height}");
            }
        }
        finally { Close(model); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Selected_long_employee_or_department_is_readable_in_the_combo_box(int tab)
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, tab);
            FillRows(model);
            model.Devices.Employees.Clear();
            model.Devices.Employees.Add(new Employee(2, "12345", LongName, "", "", null, true));
            model.Devices.EmployeeInput = model.Devices.Employees[0];
            model.Search.Departments.Clear();
            model.Search.Departments.Add(new DepartmentRow { Id = 1, Path = LongDepartment });
            model.Search.Department = model.Search.Departments[0];
            if (tab == 1) model.Search.ToggleFiltersCommand.Execute(null);
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                var combo = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(box => box.IsEffectivelyVisible && ReferenceEquals(box.ItemsSource,
                        tab == 1 ? (object)model.Search.Departments : model.Devices.Employees));
                var text = combo.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.IsEffectivelyVisible && text.Text == (tab == 2 ? LongName : LongDepartment));
                Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 1,
                    $"{size}: текст {text.TextLayout.WidthIncludingTrailingWhitespace:F1}, поле {text.Bounds.Width:F1}");
                Assert.True(text.TextLayout.Height <= text.Bounds.Height + 1,
                    $"{size}: текст {text.TextLayout.Height:F1}, поле {text.Bounds.Height:F1}");
                Assert.All(text.TextLayout.TextLines, line => Assert.False(line.HasOverflowed || line.HasCollapsed,
                    $"{size}: выбранное значение {text.Text} обрезано"));
                var textRect = InWindow(text, window);
                var comboRect = InWindow(combo, window);
                Assert.True(textRect.Left >= comboRect.Left && textRect.Right <= comboRect.Right + 1
                    && textRect.Top >= comboRect.Top && textRect.Bottom <= comboRect.Bottom + 1,
                    $"{size}: текст {textRect}, поле {comboRect}");
                foreach (var button in window.FindControl<TabControl>("Tabs")!.GetVisualDescendants()
                             .OfType<Button>().Where(button => button.IsEffectivelyVisible))
                    Assert.True(InWindow(button, window).Bottom <= window.ClientSize.Height - 29,
                        $"{size}: кнопка {button.Content} вышла за нижнюю границу вкладки");
            }
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Staff_page_uses_the_full_width_without_department_controls()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, 3);
            foreach (var size in Sizes())
            {
                await Resize(window, size);
                var list = window.GetVisualDescendants().OfType<ListBox>()
                    .Single(control => control.IsEffectivelyVisible);
                Assert.Same(model.Staff.Employees, list.ItemsSource);
                Assert.True(list.Bounds.Width >= size.Width - 24);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), control => control.IsEffectivelyVisible);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.IsEffectivelyVisible && text.Text == "ОТДЕЛЫ");
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
                Assert.Contains(buttons, button => Equals(button.Content, "Добавить сотрудника"));
                Assert.Contains(buttons, button => Equals(button.Content, "Удалить"));
                Assert.DoesNotContain(buttons, button => Equals(button.Content, "Уволить"));
            }
        }
        finally { Close(model); }
    }

    [AvaloniaFact]
    public async Task Staff_form_adds_edits_and_deletes_a_card_without_a_department()
    {
        using var model = new MainWindowViewModel(() => []);
        var window = Open(model);
        try
        {
            await SelectTab(window, model, 3);
            await Resize(window, new Size(800, 600));
            var fields = window.GetVisualDescendants().OfType<TextBox>()
                .Where(field => field.IsEffectivelyVisible).ToDictionary(field => field.Watermark!);
            fields["из регистратора"].Text = "10482";
            fields["Фамилия Имя Отчество"].Text = LongName;
            fields["+7 …"].Text = "+7 900 123-45-67";
            Click("Добавить сотрудника");
            var added = Assert.Single(model.Staff.Employees);
            Assert.Null(added.DepartmentId);
            Assert.Equal("10482", added.PersonnelNo);
            Assert.Equal(LongName, added.FullName);

            fields["Фамилия Имя Отчество"].Text = "Иванов Иван Иванович";
            Click("Сохранить изменения");
            Assert.Equal("Иванов Иван Иванович", Assert.Single(model.Staff.Employees).FullName);

            Click("Удалить");
            Assert.Empty(model.Staff.Employees);
            Assert.Empty(new StaffDirectory(AppPaths.Database).Employees());
            Assert.False(model.Staff.DeleteEmployeeCommand.CanExecute(null));

            void Click(string label)
            {
                Pump(window);
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.IsEffectivelyVisible && Equals(button.Content, label));
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Pump(window);
            }
        }
        finally { Close(model); }
    }

    private static void FillRows(MainWindowViewModel model)
    {
        model.Devices.Devices.Clear();
        model.Devices.Devices.Add(new DeviceRow { Id = 64001, Name = "Регистратор северо-западного патруля № 3",
            Employee = LongName, Department = LongDepartment, LastSeen = "20.10.26 18:35", Label = "RECORDER_BACKUP" });
        model.Staff.Employees.Clear();
        model.Staff.Employees.Add(new Employee(1, "001234567890", LongName, "+7 (343) 123-45-67 доб. 12345", "", 1, true));
        model.Log.Entries.Clear();
        model.Log.Entries.Add(new LogRow { At = "20.10.26 18:35:00", Kind = "настройки",
            Message = $"Камера 64001 закреплена за {LongName}, подразделение {LongDepartment}" });
    }

    private static async Task SelectTab(Window window, MainWindowViewModel model, int index)
    {
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = index;
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
        await model.Devices.Reload();
        await model.Log.Reload();
        await Task.Yield();
        Pump(window);
    }

    private static IEnumerable<Size> Sizes() => new[] { new Size(780, 560), new Size(800, 600),
        new Size(1024, 768), new Size(1280, 800), new Size(780, 560), new Size(800, 600) };

    private static async Task Resize(Window window, Size size)
    {
        window.Width = size.Width;
        window.Height = size.Height;
        var platform = window.PlatformImpl!;
        platform.GetType().GetMethod("Resize")!.Invoke(platform, [size, WindowResizeReason.User]);
        await Task.Yield();
        Pump(window);
        Assert.Equal(size, window.ClientSize);
    }

    private static MainWindow Open(MainWindowViewModel model)
    {
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = 800, Height = 600 };
        window.Show();
        return window;
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Rect InWindow(Control control, Window window) => new(
        control.TranslatePoint(default, window)!.Value,
        control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!.Value);

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
