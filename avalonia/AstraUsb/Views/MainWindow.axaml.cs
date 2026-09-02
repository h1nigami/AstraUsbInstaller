using AstraUsb.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        // Ошибка в одном обработчике не должна закрывать киоск: станция
        // работает без присмотра, и упавшее окно означает остановленный сбор.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Services.CrashLog.Write("ошибка в интерфейсе", e.Exception);
            e.Handled = true;
        };

        var vm = new MainWindowViewModel();
        vm.ExitRequested += Close;
        DataContext = vm;

        var tabs = this.FindControl<TabControl>("Tabs")!;
        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;
        var accountBox = this.FindControl<TextBox>("AccountBox")!;

        // Экранная клавиатура пишет в то поле, где стоит курсор: полей два,
        // а клавиатура под ними одна.
        accountBox.GotFocus += (_, _) => vm.EditingAccount = true;
        passwordBox.GotFocus += (_, _) => vm.EditingAccount = false;

        // Все разделы, кроме «Загрузки», закрыты паролем. Списки в них
        // читаются заново при каждом переходе: пока оператор смотрел на
        // загрузку, камеры и сотрудники успевают появиться.
        tabs.SelectionChanged += (_, e) =>
        {
            // Событие всплывает от вложенных списков: у каждого раздела свой
            // список с выбором, и перечитывание их содержимого снова меняет
            // выбор. Без этой проверки обработчик вызывал сам себя до
            // переполнения стека, а такое падение не попадает даже в журнал.
            if (!ReferenceEquals(e.Source, tabs))
                return;

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
            vm.Search.ReloadDepartments();
        };

        // Нажатие по окну отсека открывает его карточку. Обработчик висит на
        // окне, а не в шаблоне: компоновок три, и в каждой окно устроено
        // по-своему, а данные под курсором одни и те же.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (vm.PasswordVisible || vm.BayVisible)
                return;

            if ((e.Source as Control)?.DataContext is PortViewModel port)
                vm.OpenBayCommand.Execute(port);
        }, RoutingStrategies.Tunnel);

        vm.AccessGranted += index => tabs.SelectedIndex = index;
        vm.AccessExpired += () => tabs.SelectedIndex = 0;

        // Пароль вводят сразу, без лишнего попадания по полю: станция
        // сенсорная, и промах по нему стоит оператору времени.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.PasswordVisible) || !vm.PasswordVisible)
                return;

            // Учётная запись обычно та же, что в прошлый раз, поэтому курсор
            // сразу в пароле.
            vm.EditingAccount = false;
            vm.KeysUpper = false;
            passwordBox.Focus();
        };

        Closed += (_, _) => vm.Dispose();
    }
}
