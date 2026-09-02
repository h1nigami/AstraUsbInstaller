namespace AstraUsb.Services;

/// <summary>Что панель просит сделать со станцией.</summary>
public enum StationAction
{
    /// <summary>Обслужить отсек первым.</summary>
    Prioritize,

    /// <summary>Отменить загрузку: только зарядка.</summary>
    ChargeOnly,

    /// <summary>Вернуть отсек в обычную работу.</summary>
    Resume,

    /// <summary>Перезапустить станцию.</summary>
    Restart,
}

/// <summary>Одна просьба панели.</summary>
public sealed record StationCommand(StationAction Action, int Slot);

/// <summary>
/// Просьбы, пришедшие снаружи.
///
/// Веб-запрос идёт в своём потоке и не может трогать доску сбора: она
/// принадлежит потоку интерфейса. Поэтому панель складывает просьбы сюда, а
/// окно разбирает их при очередном опросе носителей. Задержка до двух секунд
/// для приоритета и отмены загрузки незаметна, зато нет ни одной блокировки.
/// </summary>
public static class StationCommands
{
    private static readonly Queue<StationCommand> Pending = new();
    private static readonly object Lock = new();

    /// <summary>Больше этого числа просьб не копим: значит их никто не разбирает.</summary>
    private const int Limit = 64;

    public static void Request(StationAction action, int slot)
    {
        lock (Lock)
        {
            if (Pending.Count >= Limit)
                return;

            Pending.Enqueue(new StationCommand(action, slot));
        }
    }

    /// <summary>Забирает накопленное. Возвращает пустой список, если просьб нет.</summary>
    public static IReadOnlyList<StationCommand> Take()
    {
        lock (Lock)
        {
            if (Pending.Count == 0)
                return [];

            var taken = Pending.ToArray();
            Pending.Clear();
            return taken;
        }
    }
}
