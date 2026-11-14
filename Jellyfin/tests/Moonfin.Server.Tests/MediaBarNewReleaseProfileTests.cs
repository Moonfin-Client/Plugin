using System.Collections.Generic;
using System.Text.Json;
using Moonfin.Server.Api;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The media bar's new release settings travel in the synced profile, and the rating the bar
/// filters new movies on is read from the MDBList cache only.
/// </summary>
public class MediaBarNewReleaseProfileTests
{
    [Fact]
    public void TheFieldsSurviveARoundTrip()
    {
        const string sent = "{\"mediaBarNewReleaseKinds\":\"newSeason,newMovie\",\"mediaBarNewMovieBasis\":\"dateAdded\"," +
            "\"mediaBarNewMovieDays\":120,\"mediaBarNewMovieMaxAgeMonths\":12,\"mediaBarNewMovieMinRating\":65," +
            "\"mediaBarFollowedFirst\":false,\"mediaBarMixTypes\":true,\"mediaBarNewMovieDetail\":false,\"mediaBarNewMovieTaglineOnly\":true," +
            "\"mediaBarUpcomingEpisodes\":true,\"mediaBarUpcomingMovies\":false,\"mediaBarUpcomingDays\":7}";

        var json = JsonSerializer.Serialize(JsonSerializer.Deserialize<MoonfinSettingsProfile>(sent));

        Assert.Contains("\"mediaBarNewReleaseKinds\":\"newSeason,newMovie\"", json);
        Assert.Contains("\"mediaBarNewMovieBasis\":\"dateAdded\"", json);
        Assert.Contains("\"mediaBarNewMovieDays\":120", json);
        Assert.Contains("\"mediaBarNewMovieMaxAgeMonths\":12", json);
        Assert.Contains("\"mediaBarNewMovieMinRating\":65", json);
        Assert.Contains("\"mediaBarFollowedFirst\":false", json);
        Assert.Contains("\"mediaBarMixTypes\":true", json);
        Assert.Contains("\"mediaBarNewMovieDetail\":false", json);
        Assert.Contains("\"mediaBarNewMovieTaglineOnly\":true", json);
        Assert.Contains("\"mediaBarUpcomingEpisodes\":true", json);
        Assert.Contains("\"mediaBarUpcomingMovies\":false", json);
        Assert.Contains("\"mediaBarUpcomingDays\":7", json);
    }

    [Fact]
    public void ImdbComesFirst()
    {
        var ratings = new List<MdbListRating>
        {
            new() { Source = "tmdb", Score = 64 },
            new() { Source = "imdb", Value = 5.0 },
        };

        Assert.Equal(5.0, MdbListController.CachedScore(ratings));
    }

    [Fact]
    public void TmdbThenTraktScoresAreOutOfTen()
    {
        Assert.Equal(6.4, MdbListController.CachedScore(new List<MdbListRating> { new() { Source = "tmdb", Score = 64 } }));
        Assert.Equal(8.0, MdbListController.CachedScore(new List<MdbListRating> { new() { Source = "trakt", Score = 80 } }));
    }

    [Fact]
    public void NoUsableSourceGivesNothing()
    {
        Assert.Null(MdbListController.CachedScore(null));
        Assert.Null(MdbListController.CachedScore(new List<MdbListRating> { new() { Source = "metacritic" } }));
    }
}
