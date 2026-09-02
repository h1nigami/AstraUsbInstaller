using System.Runtime.InteropServices;

namespace AstraUsb.Services;

/// <summary>
/// Как станция называет себя снаружи.
///
/// В панели рядом стоят модель и точка: на объекте станций несколько, и по
/// одной модели их не различить. Точку задают при установке, и если её не
/// задали, остаётся одна модель.
/// </summary>
public static class StationTitle
{
    public const string Model = "BC-10";

    public static string Compose(string model, string place)
    {
        var where = place.Trim();
        return where.Length == 0 ? model : $"{model} · {where}";
    }

    /// <summary>
    /// Короткая подпись системы для шапки панели. Задание требует показывать,
    /// на чём работает станция: пути и тома выглядят по-разному, и оператору
    /// нужно понимать, что он читает.
    /// </summary>
    public static string System()
    {
        var description = RuntimeInformation.OSDescription.Trim();

        if (description.Length == 0)
            return OperatingSystem.IsWindows() ? "Windows" : "Linux";

        // Полное описание бывает длинным («Linux 6.1.0-astra ... #1 SMP»),
        // а в шапке для него места нет: берём начало до подробностей сборки.
        var cut = description.Split(' ', '#')[0];
        var label = cut.Length is > 0 and <= 40 ? cut : description[..Math.Min(40, description.Length)];

        return label;
    }
}
