using System.Text.Json;
using Moonfin.Server.Models;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The media bar's new release settings travel in the synced profile.
/// </summary>
public class MediaBarNewReleaseProfileTests
{
    [Fact]
    public void TheFieldsSurviveARoundTrip()
    {
        const string sent = "{\"mediaBarNewReleaseKinds\":\"newSeason,newMovie\"," +
            "\"mediaBarNewMovieDays\":120,\"mediaBarNewMovieDetail\":false," +
            "\"mediaBarFollowedFirst\":false,\"mediaBarMixTypes\":true}";

        var json = JsonSerializer.Serialize(JsonSerializer.Deserialize<MoonfinSettingsProfile>(sent));

        Assert.Contains("\"mediaBarNewReleaseKinds\":\"newSeason,newMovie\"", json);
        Assert.Contains("\"mediaBarNewMovieDays\":120", json);
        Assert.Contains("\"mediaBarNewMovieDetail\":false", json);
        Assert.Contains("\"mediaBarFollowedFirst\":false", json);
        Assert.Contains("\"mediaBarMixTypes\":true", json);
    }

    [Fact]
    public void AnOlderProfileLeavesThemUnset()
    {
        // Unset, not false, so the admin default applies.
        var profile = JsonSerializer.Deserialize<MoonfinSettingsProfile>("{}")!;

        Assert.Null(profile.MediaBarFollowedFirst);
        Assert.Null(profile.MediaBarMixTypes);
    }
}
