using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class SeasonalSelectorTests
{
    private static readonly IReadOnlyList<SeasonalHoliday> Holidays =
        SeasonalRules.LoadEmbedded(typeof(SeasonalRules).Assembly);

    private static string[] Ids(DateTime date, string? country, IEnumerable<string>? hidden = null) =>
        SeasonalSelector.Active(Holidays, date, country, hidden).Select(a => a.Holiday.Id).ToArray();

    [Fact]
    public void ACanadianViewerInEarlyOctoberGetsThanksgivingBeforeHalloween()
    {
        Assert.Equal(new[] { "thanksgiving", "halloween" }, Ids(new DateTime(2026, 10, 8), "CA"));
    }

    [Fact]
    public void AnAmericanViewerInEarlyOctoberOnlyGetsHalloween()
    {
        Assert.Equal(new[] { "halloween" }, Ids(new DateTime(2026, 10, 8), "US"));
    }

    [Fact]
    public void NoCountryMeansOnlyTheHolidaysEveryoneObserves()
    {
        Assert.Equal(new[] { "halloween" }, Ids(new DateTime(2026, 10, 8), null));
        Assert.Empty(Ids(new DateTime(2026, 11, 24), null));
    }

    [Fact]
    public void CountryCodesAreNotCaseSensitive()
    {
        Assert.Contains("thanksgiving", Ids(new DateTime(2026, 11, 24), "us"));
    }

    [Fact]
    public void ThanksgivingHandsOverToChristmasTheNextDay()
    {
        Assert.Equal(new[] { "thanksgiving" }, Ids(new DateTime(2026, 11, 26), "US"));
        Assert.Equal(new[] { "christmas" }, Ids(new DateTime(2026, 11, 27), "US"));
    }

    [Fact]
    public void ChristmasHandsOverToNewYear()
    {
        Assert.Equal(new[] { "christmas" }, Ids(new DateTime(2026, 12, 26), "GB"));
        Assert.Equal(new[] { "newYear" }, Ids(new DateTime(2026, 12, 27), "GB"));
        Assert.Equal(new[] { "newYear" }, Ids(new DateTime(2027, 1, 1), "GB"));
        Assert.Empty(Ids(new DateTime(2027, 1, 2), "GB"));
    }

    [Fact]
    public void EasterFollowsTheComputus()
    {
        Assert.Equal(new[] { "easter" }, Ids(new DateTime(2026, 3, 29), "DE"));
        Assert.Equal(new[] { "easter" }, Ids(new DateTime(2026, 4, 6), "DE"));
        Assert.Empty(Ids(new DateTime(2026, 4, 7), "DE"));
    }

    [Fact]
    public void AHiddenHolidayIsSkipped()
    {
        Assert.Equal(new[] { "halloween" }, Ids(new DateTime(2026, 10, 8), "CA", new[] { "Thanksgiving" }));
        Assert.Empty(Ids(new DateTime(2026, 6, 15), "US", new[] { "pride" }));
    }

    [Fact]
    public void TheLunarHolidaysFollowTheirTablesInTheCountriesThatKeepThem()
    {
        Assert.Equal(new[] { "diwali" }, Ids(new DateTime(2026, 11, 8), "IN"));
        Assert.Empty(Ids(new DateTime(2026, 11, 8), "US"));
        Assert.Equal(new[] { "lunarNewYear" }, Ids(new DateTime(2028, 1, 26), "SG"));
        Assert.Equal(new[] { "lunarNewYear", "valentines" }, Ids(new DateTime(2027, 2, 6), "VN"));
        Assert.Equal(new[] { "valentines" }, Ids(new DateTime(2027, 2, 6), "GB"));
    }

    [Fact]
    public void ShorterWindowsComeFirst()
    {
        var active = SeasonalSelector.Active(Holidays, new DateTime(2026, 10, 8), "CA");

        Assert.Equal(7, active[0].LengthDays);
        Assert.Equal(31, active[1].LengthDays);
    }
}
