using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Открытый раздел закрывается сам после простоя: станция стоит в общем
/// помещении, и забытая открытой настройка равна её отсутствию.
/// </summary>
public sealed class AccessGuardTests
{
    private static readonly DateTime Start = new(2026, 9, 2, 12, 0, 0);

    [Fact]
    public void Closed_until_the_password_is_entered()
    {
        var guard = new AccessGuard(10);

        Assert.False(guard.Unlocked);
        Assert.False(guard.Check(Start));
    }

    [Fact]
    public void Open_right_after_the_password()
    {
        var guard = new AccessGuard(10);
        guard.Unlock(Start);

        Assert.True(guard.Check(Start.AddMinutes(9)));
    }

    [Fact]
    public void Idle_time_closes_it()
    {
        var guard = new AccessGuard(10);
        guard.Unlock(Start);

        Assert.False(guard.Check(Start.AddMinutes(10)));
        Assert.False(guard.Unlocked);
    }

    [Fact]
    public void Work_pushes_the_deadline_back()
    {
        var guard = new AccessGuard(10);
        guard.Unlock(Start);

        guard.Touch(Start.AddMinutes(9));

        Assert.True(guard.Check(Start.AddMinutes(18)));
        Assert.False(guard.Check(Start.AddMinutes(19)));
    }

    [Fact]
    public void Work_on_a_closed_section_does_not_reopen_it()
    {
        var guard = new AccessGuard(10);

        guard.Touch(Start);

        Assert.False(guard.Check(Start));
    }

    [Fact]
    public void Zero_timeout_keeps_it_open()
    {
        var guard = new AccessGuard(0);
        guard.Unlock(Start);

        Assert.True(guard.Check(Start.AddHours(8)));
    }

    [Fact]
    public void Locking_takes_effect_at_once()
    {
        var guard = new AccessGuard(10);
        guard.Unlock(Start);

        guard.Lock();

        Assert.False(guard.Check(Start));
    }
}
