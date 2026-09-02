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
        // размер станции, иначе вид на 1024x600 не оценить.
        if (Environment.GetEnvironmentVariable("ASTRA_WINDOWED") == "1")
        {
            WindowState = WindowState.Normal;
            Width = 1024;
            Height = 600;
        }

        var vm = new MainWindowViewModel();
        vm.ExitRequested += Close;
        DataContext = vm;

        Closed += (_, _) => vm.Dispose();
    }
}
