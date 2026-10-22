using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

public class NumberBoxTests
{
    // Стёртое числовое поле должно читаться как 0, а не показывать в себе исключение.
    [AvaloniaFact]
    public void Clearing_a_number_box_leaves_no_error()
    {
        AppPaths.Root = Path.Combine(Path.GetTempPath(), "int-box-" + Guid.NewGuid().ToString("N"));
        AppPaths.EnsureCreated();
        using var model = new MainWindowViewModel(() => []);
        var window = new MainWindow(model) { WindowState = WindowState.Normal, Width = 800, Height = 600 };
        window.Show();
        window.FindControl<TabControl>("Tabs")!.SelectedIndex = 5;
        model.PasswordInput = PasswordGate.Default();
        model.ConfirmPasswordCommand.Execute(null);
        model.Settings.Section = "station";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var boxes = window.GetVisualDescendants().OfType<TextBox>()
            .Where(b => b.IsEffectivelyVisible && (string?)b.Tag == "num").ToArray();
        Assert.NotEmpty(boxes);
        foreach (var box in boxes)
        {
            box.Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(DataValidationErrors.GetErrors(box) ?? []);
        }
        Assert.Equal(0, model.Settings.KeepDays);
    }

    // Анимация списка не должна ронять отрисовку: однажды стиль без аниматора
    // оставил окно пустым.
    [AvaloniaFact]
    public void Opening_a_combo_box_does_not_break_rendering()
    {
        var combo = new ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0 };
        var window = new Window { Content = combo };
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AstraUsb/"))
        {
            Source = new Uri("avares://AstraUsb/Themes/Controls.axaml"),
        });
        window.Show();
        combo.IsDropDownOpen = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(combo.IsDropDownOpen);
    }
}
