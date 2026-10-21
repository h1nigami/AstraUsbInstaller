using System.Reflection;
using AstraUsb.Services;
using AstraUsb.ViewModels;
using AstraUsb.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class ExportFolderPickerTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-picker-").FullName;

    public ExportFolderPickerTests()
    {
        AppPaths.Root = _dir;
        AppPaths.EnsureCreated();
        new Settings { AlarmSound = false }.Save();
    }

    [AvaloniaTheory]
    [InlineData("select")]
    [InlineData("cancel")]
    [InlineData("error")]
    [InlineData("busy")]
    public async Task Destination_button_opens_folder_picker_without_starting_export(string result)
    {
        using var model = new MainWindowViewModel(() => []);
        model.Search.ExportTarget = Path.Combine(_dir, "Прежняя папка");
        model.Search.Exporting = result == "busy";
        var initialHint = model.Search.Hint;
        var selected = Directory.CreateDirectory(Path.Combine(_dir, "Флешка с записями")).FullName;
        var window = new MainWindow(model) { WindowState = WindowState.Normal };
        window.Show();
        try
        {
            window.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
            var provider = DispatchProxy.Create<IStorageProvider, FolderPicker>();
            var picker = (FolderPicker)provider;
            picker.Result = result;
            picker.Path = selected;
            typeof(TopLevel).GetField("_storageProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, provider);

            var button = window.FindControl<Button>("ChooseExportFolder");
            Assert.NotNull(button);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(result != "busy", picker.Opened);
            Assert.Equal(result == "select" ? selected : Path.Combine(_dir, "Прежняя папка"), model.Search.ExportTarget);
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == model.Search.ExportTarget);
            Assert.Equal(result == "busy", model.Search.Exporting);
            Assert.Equal(initialHint, model.Search.Hint);
            Assert.Empty(Directory.EnumerateFileSystemEntries(selected));
            Assert.Equal(result == "error", model.ErrorVisible);
            var results = window.GetVisualDescendants().OfType<ListBox>()
                .Single(list => ReferenceEquals(list.ItemsSource, model.Search.Results));
            Assert.True(results.Bounds.Height >= 240);
            Assert.True(button.TranslatePoint(new Avalonia.Point(button.Bounds.Width, button.Bounds.Height), window)!.Value.X
                <= window.ClientSize.Width);
        }
        finally
        {
            model.ExitCommand.Execute(null);
            model.PasswordInput = PasswordGate.Default();
            model.ConfirmPasswordCommand.Execute(null);
        }
    }

    public class FolderPicker : DispatchProxy
    {
        public string Result = "";
        public string Path = "";
        public bool Opened;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_CanPickFolder") return true;
            if (method.Name != nameof(IStorageProvider.OpenFolderPickerAsync)) throw new NotSupportedException(method.Name);
            Opened = true;
            if (Result == "error") throw new IOException("Диалог недоступен");
            var folder = DispatchProxy.Create<IStorageFolder, Folder>();
            ((Folder)folder).Path = new Uri(Path);
            return Task.FromResult<IReadOnlyList<IStorageFolder>>(Result == "select" ? [folder] : []);
        }
    }

    public class Folder : DispatchProxy
    {
        public Uri Path = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Path" => Path,
            "Dispose" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }
}
