namespace AstraUsb.Services;

/// <summary>Числительные в подписях, которые видит оператор.</summary>
public static class Numerals
{
    /// <summary>
    /// Число с существительным в нужной форме: 1 файл, 2 файла, 5 файлов.
    /// Одиннадцать и двенадцать берут форму множественного числа, поэтому
    /// вторая цифра проверяется отдельно.
    /// </summary>
    public static string Plural(long count, string one, string few, string many)
    {
        var tens = Math.Abs(count) % 100;
        var unit = tens % 10;

        var form = tens is >= 11 and <= 14 ? many
            : unit == 1 ? one
            : unit is >= 2 and <= 4 ? few
            : many;

        return $"{count} {form}";
    }
}
