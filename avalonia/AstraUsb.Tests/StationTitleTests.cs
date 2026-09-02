using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Как станция называет себя в веб-панели. Шаблон панели показывает модель и
/// точку («BC-10 · Пермская 10»), а точку задают при установке: на объекте
/// станций несколько, и различают их по этой подписи.
/// </summary>
public sealed class StationTitleTests
{
    [Fact]
    public void The_place_is_added_after_the_model()
    {
        Assert.Equal("BC-10 · Пермская 10", StationTitle.Compose("BC-10", "Пермская 10"));
    }

    [Fact]
    public void Without_a_place_the_model_stands_alone()
    {
        Assert.Equal("BC-10", StationTitle.Compose("BC-10", ""));
        Assert.Equal("BC-10", StationTitle.Compose("BC-10", "   "));
    }

    [Fact]
    public void The_system_is_named_the_way_it_names_itself()
    {
        var label = StationTitle.System();

        // Подпись идёт в шапку панели рядом с часами, поэтому она короткая
        // и непустая на любой системе.
        Assert.NotEmpty(label);
        Assert.True(label.Length <= 40, label);
    }
}
