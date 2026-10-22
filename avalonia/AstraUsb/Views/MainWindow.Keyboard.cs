using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace AstraUsb.Views;

/// <summary>
/// Экранная клавиатура полей разделов. Станция сенсорная, физической клавиатуры
/// нет. Клавиатура одна на все поля: она появляется при фокусе в TextBox и
/// пишет прямо в него. Поле с Tag="num" получает цифровую раскладку, Tag="en"
/// и пароли начинают с латиницы. Окно входа держит свою клавиатуру.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] RuRows = ["1234567890", "йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю.-"];
    private static readonly string[] EnRows = ["1234567890", "qwertyuiop", "asdfghjkl@", "zxcvbnm.-_"];
    private static readonly string[] NumRows = ["1234567890", ".:-+/ "];

    private TextBox? _target;
    private bool _russian = true;
    private bool _upper;

    private Control _osk = null!;
    private Panel _keys = null!;

    private void InitKeyboard(Control osk, Panel keys)
    {
        (_osk, _keys) = (osk, keys);
        // Фокус приходит от вложенных полей, поэтому слушаем на окне.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { Name: not ("AccountBox" or "PasswordBox") } box)
            {
                _target = box;
                _russian = box.PasswordChar == '\0' && (string?)box.Tag != "en";
                _upper = false;
                Build(osk, keys);
                ShowOsk(true);
                // Строка клавиатуры сжимает раздел: поле могло уехать за край.
                DispatcherTimer.RunOnce(() => box.BringIntoView(), TimeSpan.FromMilliseconds(230));
            }
            else if (e.Source is not null)
            {
                _target = null;
                ShowOsk(false);
            }
        }, RoutingStrategies.Bubble);

        // Смена раздела или окно входа убирают клавиатуру вместе с полем.
        var tabs = this.FindControl<TabControl>("Tabs")!;
        tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, tabs))
                ShowOsk(false);
        };
    }

    // Появление: высота строки растёт от нуля вместе с проявлением клавиш, чтобы
    // раздел над ней уезжал плавно, а не прыгал на кадр. Скрытие убирает строку
    // только после затухания.
    private void ShowOsk(bool show)
    {
        var osk = _osk;
        if (show == (osk.Opacity > 0))
            return;
        if (show)
        {
            osk.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                osk.Opacity = 1;
                osk.RenderTransform = TransformOperations.Parse("translateY(0px)");
                osk.Height = FullHeight();
            }, DispatcherPriority.Background);
            return;
        }
        osk.Opacity = 0;
        osk.Height = 0;
        osk.RenderTransform = TransformOperations.Parse("translateY(36px)");
        DispatcherTimer.RunOnce(() =>
        {
            if (osk.Opacity == 0)
                osk.IsVisible = false;
        }, TimeSpan.FromMilliseconds(220));
    }

    private double FullHeight()
    {
        _keys.Measure(Size.Infinity);
        var osk = (Border)_osk;
        return _keys.DesiredSize.Height + osk.Padding.Top + osk.Padding.Bottom + osk.BorderThickness.Top;
    }

    private void Build(Control osk, Panel keys)
    {
        keys.Children.Clear();
        var numeric = (string?)_target?.Tag == "num";
        var rows = numeric ? NumRows : _russian ? RuRows : EnRows;

        foreach (var row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var ch in row)
            {
                var text = _upper ? char.ToUpperInvariant(ch).ToString() : ch.ToString();
                line.Children.Add(Key(ch == ' ' ? "пробел" : text, () => Type(text), ch == ' ' ? 78 : 56));
            }
            keys.Children.Add(line);
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (!numeric)
        {
            tools.Children.Add(Key("Регистр", () => { _upper = !_upper; Build(osk, keys); }, 88, _upper));
            tools.Children.Add(Key(_russian ? "RU → EN" : "EN → RU", () => { _russian = !_russian; Build(osk, keys); }, 88));
            tools.Children.Add(Key("пробел", () => Type(" "), 110));
        }
        tools.Children.Add(Key("Стереть", Erase, 88));
        tools.Children.Add(Key("Очистить", () => { if (_target is { } t) t.Text = ""; }, 88));
        tools.Children.Add(Key("Скрыть", () => { ShowOsk(false); _target = null; }, 88));
        keys.Children.Add(tools);
        if (osk.Opacity > 0)
            osk.Height = FullHeight();
    }

    // Focusable=False: нажатие по клавише не забирает курсор из поля.
    private static Button Key(string label, Action press, double width, bool on = false)
    {
        var button = new Button
        {
            Content = label, Width = width, Height = 34, MinHeight = 34, Margin = new(2),
            Padding = new(0), FontSize = 15, Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (on)
            button.Classes.Add("primary");
        button.Click += (_, _) => press();
        return button;
    }

    private void Type(string text)
    {
        if (_target is not { } box)
            return;
        var (start, end) = Selection(box);
        var current = box.Text ?? "";
        box.Text = current[..start] + text + current[end..];
        box.CaretIndex = start + text.Length;
    }

    private void Erase()
    {
        if (_target is not { } box)
            return;
        var (start, end) = Selection(box);
        if (start == end && start > 0)
            start--;
        var current = box.Text ?? "";
        box.Text = current[..start] + current[end..];
        box.CaretIndex = start;
    }

    private static (int Start, int End) Selection(TextBox box)
    {
        var length = box.Text?.Length ?? 0;
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, length);
        return start == end ? (Math.Clamp(box.CaretIndex, 0, length), Math.Clamp(box.CaretIndex, 0, length)) : (start, end);
    }
}
