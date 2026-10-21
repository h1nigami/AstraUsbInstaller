using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace AstraUsb.Tests;

public sealed class FontReadabilityTests
{
    [AvaloniaTheory]
    [InlineData("FontBody", 400)]
    [InlineData("FontBody", 600)]
    [InlineData("FontHeading", 700)]
    [InlineData("FontHeading", 800)]
    public void Embedded_font_uses_the_requested_readable_weight(string resource, int weight)
    {
        var family = Assert.IsType<FontFamily>(Application.Current!.FindResource(resource));
        var typeface = new Typeface(family, weight: (FontWeight)weight).GlyphTypeface;

        Assert.Equal(weight, (int)typeface.Weight);
    }

    [AvaloniaFact]
    public void Empty_field_hint_has_readable_contrast()
    {
        var input = new TextBox { Watermark = "любое" };
        var window = new Window { Content = input };
        window.Show();
        try
        {
            var hint = input.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.IsEffectivelyVisible && text.Text == "любое");
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(hint.Foreground);
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(input.Background);
            var alpha = foreground.Color.A / 255.0 * foreground.Opacity * hint.Opacity;
            var ink = Color.FromRgb(
                (byte)(foreground.Color.R * alpha + background.Color.R * (1 - alpha)),
                (byte)(foreground.Color.G * alpha + background.Color.G * (1 - alpha)),
                (byte)(foreground.Color.B * alpha + background.Color.B * (1 - alpha)));
            var contrast = (Luminance(background.Color) + .05) / (Luminance(ink) + .05);
            Assert.True(contrast >= 4.5, $"Контраст подсказки {foreground.Color}: {contrast:F2}:1");
        }
        finally { window.Close(); }
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
