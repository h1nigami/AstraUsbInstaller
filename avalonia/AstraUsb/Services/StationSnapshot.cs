namespace AstraUsb.Services;

/// <summary>Состояние одного отсека для показа снаружи.</summary>
public sealed record BaySnapshot(
    int Slot,
    string State,
    string Camera,
    string Employee,
    string Department,
    string Files,
    int Percent);

/// <summary>Состояние станции целиком, каким его видит веб-панель.</summary>
public sealed record StationState(
    DateTime At,
    string Version,
    int Copying,
    int Done,
    int Failed,
    int Free,
    bool NetworkUp,
    bool FtpEnabled,
    string FtpState,
    string ArchiveLabel,
    long ArchiveTotalBytes,
    long ArchiveFreeBytes,
    string Trouble,
    IReadOnlyList<BaySnapshot> Bays);

/// <summary>
/// Снимок состояния станции, доступный из другого потока.
///
/// Доска сбора живёт в модели окна и принадлежит потоку интерфейса: обращаться
/// к ней из веб-запроса нельзя. Поэтому окно кладёт сюда готовый снимок при
/// каждом опросе носителей, а панель только читает его.
/// </summary>
public static class StationSnapshot
{
    private static StationState _state = Empty();

    /// <summary>Последний снимок. Никогда не null, чтобы панель не падала.</summary>
    public static StationState Current => Volatile.Read(ref _state);

    public static void Publish(StationState state) => Volatile.Write(ref _state, state);

    private static StationState Empty() => new(
        DateTime.Now, "", 0, 0, 0, 0, false, false, "", "", 0, 0, "", []);
}
