using System;
using System.Collections.Generic;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class SeasonalCalendarTests
{
    [Theory]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2029, 4, 1)]
    [InlineData(2030, 4, 21)]
    public void EasterMatchesThePublishedDates(int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), SeasonalCalendar.Easter(year));
    }

    [Theory]
    [InlineData(2026, 11, 26)]
    [InlineData(2027, 11, 25)]
    [InlineData(2028, 11, 23)]
    public void UsThanksgivingIsTheFourthThursdayOfNovember(int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), SeasonalCalendar.NthWeekday(year, 11, DayOfWeek.Thursday, 4));
    }

    [Theory]
    [InlineData(2026, 10, 12)]
    [InlineData(2027, 10, 11)]
    public void CanadianThanksgivingIsTheSecondMondayOfOctober(int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), SeasonalCalendar.NthWeekday(year, 10, DayOfWeek.Monday, 2));
    }

    [Fact]
    public void ChristmasStartsTheDayAfterUsThanksgiving()
    {
        var rule = new DateRule { Kind = "nthWeekday", Month = 11, Weekday = "thursday", N = 4, OffsetDays = 1 };

        Assert.Equal(new DateTime(2026, 11, 27), SeasonalCalendar.Resolve(rule, 2026));
    }

    [Fact]
    public void AWindowThatEndsBeforeItStartsRunsIntoTheNextYear()
    {
        var window = new SeasonalWindow
        {
            Start = new DateRule { Kind = "fixed", Month = 12, Day = 27 },
            End = new DateRule { Kind = "fixed", Month = 1, Day = 1 }
        };

        var (start, end) = SeasonalCalendar.Window(window, 2026)!.Value;
        Assert.Equal(new DateTime(2026, 12, 27), start);
        Assert.Equal(new DateTime(2027, 1, 1), end);

        Assert.True(SeasonalCalendar.TryActive(window, new DateTime(2026, 12, 30, 20, 15, 0), out _, out _));
        Assert.True(SeasonalCalendar.TryActive(window, new DateTime(2027, 1, 1), out var activeStart, out _));
        Assert.Equal(new DateTime(2026, 12, 27), activeStart);
        Assert.False(SeasonalCalendar.TryActive(window, new DateTime(2027, 1, 2), out _, out _));
    }

    [Fact]
    public void ADatesRuleReadsTheWindowsTableAndShiftsFromIt()
    {
        var window = new SeasonalWindow
        {
            Dates = new Dictionary<string, string> { ["2026"] = "02-17", ["2027"] = "02-06" },
            Start = new DateRule { Kind = "dates", OffsetDays = -5 },
            End = new DateRule { Kind = "dates", OffsetDays = 7 }
        };

        var (start, end) = SeasonalCalendar.Window(window, 2026)!.Value;
        Assert.Equal(new DateTime(2026, 2, 12), start);
        Assert.Equal(new DateTime(2026, 2, 24), end);
        Assert.True(SeasonalCalendar.TryActive(window, new DateTime(2027, 2, 13), out var activeStart, out _));
        Assert.Equal(new DateTime(2027, 2, 1), activeStart);
    }

    [Fact]
    public void AYearTheTableLeavesOutHasNoWindow()
    {
        var window = new SeasonalWindow
        {
            Dates = new Dictionary<string, string> { ["2026"] = "11-08" },
            Start = new DateRule { Kind = "dates", OffsetDays = -6 },
            End = new DateRule { Kind = "dates", OffsetDays = 2 }
        };

        Assert.Null(SeasonalCalendar.Window(window, 2027));
        Assert.False(SeasonalCalendar.TryActive(window, new DateTime(2027, 11, 1), out _, out _));
        Assert.Null(SeasonalCalendar.Resolve(window.Start, 2026));
    }

    [Fact]
    public void AnUnknownWeekdayIsRefusedRatherThanGuessed()
    {
        var rule = new DateRule { Kind = "nthWeekday", Month = 11, Weekday = "someday", N = 4 };

        Assert.Throws<InvalidOperationException>(() => SeasonalCalendar.Resolve(rule, 2026));
    }
}
