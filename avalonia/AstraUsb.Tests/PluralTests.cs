using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Числительные в подписях на плитке. Оператор видит их постоянно, а формы
/// на 11 и на 21 расходятся, поэтому правило проверяется отдельно.
/// </summary>
public sealed class PluralTests
{
    [Theory]
    [InlineData(0, "0 файлов")]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(4, "4 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(12, "12 файлов")]
    [InlineData(14, "14 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(22, "22 файла")]
    [InlineData(25, "25 файлов")]
    [InlineData(101, "101 файл")]
    [InlineData(111, "111 файлов")]
    [InlineData(1002, "1002 файла")]
    public void Numerals_agree_with_the_noun(long count, string expected)
    {
        Assert.Equal(expected, BackupService.Plural(count, "файл", "файла", "файлов"));
    }
}
