using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// MDBList's JustWatch chart lists its rows under results, names the year "year" and leaves an
/// unmatched title without ids, which can't become a card.
/// </summary>
public class JustWatchChartTests
{
    private const string Chart = """
        { "media_type": "show", "count": 3, "results": [
          { "rank": 1, "title": "The Body", "year": 2026, "mediatype": "show", "matched": true,
            "ids": { "imdb": "tt1", "tmdb": 101, "tvdb": 5 } },
          { "rank": 2, "title": "Nobody Knows", "year": 2025, "mediatype": "show", "matched": false, "ids": {} },
          { "rank": 3, "title": "VisionQuest", "year": 2026, "mediatype": "show", "matched": true,
            "ids": { "tmdb": 202 } } ] }
        """;

    [Fact]
    public void KeepsMatchedTitlesInChartOrderWithTheirYears()
    {
        var items = CustomRowFetchService.ParseJustWatchChart(Chart, "show");

        Assert.Equal(new[] { "The Body", "VisionQuest" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.Equal("Series", i.Type));
        Assert.Equal(new int?[] { 2026, 2026 }, items.Select(i => i.ProductionYear));
        Assert.Equal(new int?[] { 1, 2 }, items.Select(i => i.Rank));
        Assert.Equal("202", items[1].ProviderIds?.Tmdb);
    }
}
