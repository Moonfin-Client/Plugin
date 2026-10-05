using System.Linq;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class SeasonalKeywordMatcherTests
{
    private static readonly SeasonalHoliday Christmas = SeasonalRules
        .LoadEmbedded(typeof(SeasonalRules).Assembly)
        .Single(h => h.Id == "christmas");

    [Fact]
    public void AHolidayExposesItsKeywordIdsAndNames()
    {
        Assert.Contains(1991, SeasonalKeywordMatcher.KeywordIds(Christmas));
        Assert.DoesNotContain(3335, SeasonalKeywordMatcher.KeywordIds(Christmas));
        Assert.Contains("santa claus", SeasonalKeywordMatcher.KeywordNames(Christmas));
    }

    [Fact]
    public void TheRulesFileCarriesEveryLaunchHolidayWithIdsAndNames()
    {
        var holidays = SeasonalRules.LoadEmbedded(typeof(SeasonalRules).Assembly);

        Assert.Equal(
            new[] { "newYear", "valentines", "easter", "pride", "halloween", "thanksgiving", "christmas", "lunarNewYear", "diwali" },
            holidays.Select(h => h.Id).ToArray());
        Assert.All(holidays, h => Assert.NotEmpty(h.Keywords));
        Assert.All(holidays.SelectMany(h => h.Keywords), k =>
        {
            Assert.True(k.Id > 0);
            Assert.False(string.IsNullOrWhiteSpace(k.Name));
        });
    }
}
