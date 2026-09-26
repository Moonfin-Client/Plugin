using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The ratings endpoint keeps the profile's sources in the profile's order, however each is spelled.
/// </summary>
public class MdbListRatingFilterTests
{
    private static List<MdbListRating> AllRatings() =>
    [
        new() { Source = "imdb", Value = 8.1, Score = 81 },
        new() { Source = "myanimelist", Value = 8.6, Score = 86 },
        new() { Source = "metacriticuser", Value = 7.9, Score = 79 },
        new() { Source = "popcorn", Score = 92 },
    ];

    [Fact]
    public void OneSourceUnderTwoSpellingsComesBackOnce()
    {
        // The TV's picker added myanimelist next to the dashboard's myAnimeList.
        var result = MdbListController.FilterAndOrderRatings(
            AllRatings(), ["myAnimeList", "imdb", "myanimelist"]);

        Assert.Equal(["myAnimeList", "imdb"], result.Select(r => r.Source));
    }

    [Fact]
    public void BothRtAudienceIdsComeBackOnce()
    {
        var result = MdbListController.FilterAndOrderRatings(
            AllRatings(), ["tomatoes_audience", "rtAudience"]);

        var only = Assert.Single(result);
        Assert.Equal("tomatoes_audience", only.Source);
        Assert.Equal(92, only.Score);
    }

    [Fact]
    public void CamelCaseIdsStillFindTheirRating()
    {
        var result = MdbListController.FilterAndOrderRatings(
            AllRatings(), ["metacriticUser", "myAnimeList"]);

        Assert.Equal(["metacriticUser", "myAnimeList"], result.Select(r => r.Source));
        Assert.Equal(79, result[0].Score);
    }
}
