using System.Collections.Generic;
using System.Linq;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The ratings endpoint keeps the profile's sources in the profile's order, however each is spelled.
/// </summary>
public class MdbListRatingFilterTests
{
    private static List<MdbListRating> AllRatings() => new()
    {
        new MdbListRating { Source = "imdb", Value = 8.1, Score = 81 },
        new MdbListRating { Source = "myanimelist", Value = 8.6, Score = 86 },
        new MdbListRating { Source = "metacriticuser", Value = 7.9, Score = 79 },
        new MdbListRating { Source = "popcorn", Score = 92 },
    };

    [Fact]
    public void OneSourceUnderTwoSpellingsComesBackOnce()
    {
        // The TV's picker added myanimelist next to the dashboard's myAnimeList.
        var result = RatingsService.FilterAndOrderRatings(
            AllRatings(), new List<string> { "myAnimeList", "imdb", "myanimelist" });

        Assert.Equal(new[] { "myAnimeList", "imdb" }, result.Select(r => r.Source));
    }

    [Fact]
    public void BothRtAudienceIdsComeBackOnce()
    {
        var result = RatingsService.FilterAndOrderRatings(
            AllRatings(), new List<string> { "tomatoes_audience", "rtAudience" });

        var only = Assert.Single(result);
        Assert.Equal("tomatoes_audience", only.Source);
        Assert.Equal(92, only.Score);
    }

    [Fact]
    public void CamelCaseIdsStillFindTheirRating()
    {
        var result = RatingsService.FilterAndOrderRatings(
            AllRatings(), new List<string> { "metacriticUser", "myAnimeList" });

        Assert.Equal(new[] { "metacriticUser", "myAnimeList" }, result.Select(r => r.Source));
        Assert.Equal(79, result[0].Score);
    }
}
