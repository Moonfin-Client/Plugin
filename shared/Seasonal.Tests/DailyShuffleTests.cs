using System;
using System.Linq;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class DailyShuffleTests
{
    private static readonly string[] Titles = Enumerable.Range(1, 40).Select(i => "Movie " + i).ToArray();

    [Fact]
    public void TheSameUserGetsTheSameOrderAllDay()
    {
        var morning = DailyShuffle.Seed("user-1", new DateTime(2026, 12, 20, 8, 0, 0));
        var evening = DailyShuffle.Seed("user-1", new DateTime(2026, 12, 20, 22, 30, 0));

        Assert.Equal(morning, evening);
        Assert.Equal(DailyShuffle.Shuffle(Titles, morning), DailyShuffle.Shuffle(Titles, evening));
    }

    [Fact]
    public void TheOrderChangesTheNextDayAndBetweenUsers()
    {
        var today = DailyShuffle.Seed("user-1", new DateTime(2026, 12, 20));
        var tomorrow = DailyShuffle.Seed("user-1", new DateTime(2026, 12, 21));
        var someoneElse = DailyShuffle.Seed("user-2", new DateTime(2026, 12, 20));

        Assert.NotEqual(DailyShuffle.Shuffle(Titles, today), DailyShuffle.Shuffle(Titles, tomorrow));
        Assert.NotEqual(DailyShuffle.Shuffle(Titles, today), DailyShuffle.Shuffle(Titles, someoneElse));
    }

    [Fact]
    public void AShuffleKeepsEveryItemOnce()
    {
        var shuffled = DailyShuffle.Shuffle(Titles, DailyShuffle.Seed("user-1", new DateTime(2026, 12, 20)));

        Assert.Equal(Titles.OrderBy(t => t), shuffled.OrderBy(t => t));
        Assert.NotEqual(Titles, shuffled);
    }
}
