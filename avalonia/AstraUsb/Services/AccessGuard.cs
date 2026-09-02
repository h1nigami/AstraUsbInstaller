namespace AstraUsb.Services;

/// <summary>
/// Держит доступ к закрытым разделам открытым, пока станцией пользуются.
///
/// Оператор вводит пароль один раз и работает, а забытый открытым раздел сам
/// закрывается после простоя. Время передаётся снаружи: так поведение
/// проверяется без ожидания.
/// </summary>
public sealed class AccessGuard
{
    private readonly TimeSpan _timeout;
    private DateTime _until;

    /// <param name="timeoutMinutes">Минуты простоя до закрытия; 0 не закрывает.</param>
    public AccessGuard(int timeoutMinutes)
    {
        _timeout = TimeSpan.FromMinutes(Math.Max(timeoutMinutes, 0));
    }

    public bool Unlocked { get; private set; }

    /// <summary>Пароль принят: раздел открыт.</summary>
    public void Unlock(DateTime now)
    {
        Unlocked = true;
        _until = now + _timeout;
    }

    /// <summary>Оператор что-то сделал: отсчёт простоя начинается заново.</summary>
    public void Touch(DateTime now)
    {
        if (Unlocked)
            _until = now + _timeout;
    }

    /// <summary>Закрывает доступ сразу, не дожидаясь простоя.</summary>
    public void Lock()
    {
        Unlocked = false;
        _until = default;
    }

    /// <summary>
    /// Проверяет доступ и закрывает его, если простой затянулся. Возвращает
    /// true, пока раздел открыт.
    /// </summary>
    public bool Check(DateTime now)
    {
        if (!Unlocked)
            return false;

        // Нулевой таймаут означает, что оператор просил не закрывать.
        if (_timeout == TimeSpan.Zero)
            return true;

        if (now >= _until)
            Lock();

        return Unlocked;
    }
}
