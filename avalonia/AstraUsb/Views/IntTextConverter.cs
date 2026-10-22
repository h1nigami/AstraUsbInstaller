using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace AstraUsb.Views;

/// <summary>
/// Целое число в поле ввода. Пустое поле читается как 0: без этого стёртый
/// текст давал в поле ошибку «Could not convert '' to Int32» вместо числа.
/// </summary>
public sealed class IntTextConverter : IValueConverter
{
    public static readonly IntTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text))
            return 0;
        return int.TryParse(text, NumberStyles.Integer, culture, out var number)
            ? number
            : new BindingNotification(new FormatException("нужно целое число"), BindingErrorType.DataValidationError);
    }
}
