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
            "\"mediaBarNewMovieDays\":120,\"mediaBarNewMovieDetail\":false}";

        var json = JsonSerializer.Serialize(JsonSerializer.Deserialize<MoonfinSettingsProfile>(sent));

        Assert.Contains("\"mediaBarNewReleaseKinds\":\"newSeason,newMovie\"", json);
        Assert.Contains("\"mediaBarNewMovieDays\":120", json);
        Assert.Contains("\"mediaBarNewMovieDetail\":false", json);
    }
}
