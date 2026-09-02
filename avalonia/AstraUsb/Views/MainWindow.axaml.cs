using AstraUsb.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AstraUsb.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // На киоске окно занимает весь экран. На машине разработчика экран
        // крупнее целевого, поэтому ASTRA_WINDOWED открывает окно ровно в
        // размер станции, иначе вид на 800x600 не оценить.
        if (Environment.GetEnvironmentVariable("ASTRA_WINDOWED") == "1")
        {
            WindowState = WindowState.Normal;
            Width = 800;
            Height = 600;
        }

        var vm = new MainWindowViewModel();
        vm.ExitRequested += Close;
        DataContext = vm;

        var tabs = this.FindControl<TabControl>("Tabs")!;
        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;

        // Все разделы, кроме «Загрузки», закрыты паролем. Списки в них
        // читаются заново при каждом переходе: пока оператор смотрел на
        // загрузку, камеры и сотрудники успевают появиться.
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedIndex > 0 && !vm.AccessAllowed)
            {
                var wanted = tabs.SelectedIndex;
                tabs.SelectedIndex = 0;
                vm.AskForTab(wanted);
                return;
            }

            vm.NoteActivity();
            vm.Devices.Reload();
            vm.Staff.Reload();
            vm.Log.Reload();
        };

        vm.AccessGranted += index => tabs.SelectedIndex = index;
        vm.AccessExpired += () => tabs.SelectedIndex = 0;

        // Пароль вводят сразу, без лишнего попадания по полю: станция
        // сенсорная, и промах по нему стоит оператору времени.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.PasswordVisible) && vm.PasswordVisible)
                passwordBox.Focus();
        };

        Closed += (_, _) => vm.Dispose();
    }
}
