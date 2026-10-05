using System;
using System.Text.Json;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class SeasonalSuggestionDetailsTests
{
    private const string Details = """
        {
          "overview": "Three witches return to Salem.",
          "vote_average": 7.08,
          "runtime": 96,
          "genres": [{"id": 35, "name": "Comedy"}, {"id": 14, "name": "Fantasy"}],
          "images": {
            "backdrops": [
              {"iso_639_1": null, "file_path": "/plain.jpg"},
              {"iso_639_1": "en", "file_path": "/titled.jpg"}
            ]
          }
        }
        """;

    [Fact]
    public void DetailsFillWhatTheFocusedCardShows()
    {
        var item = new CustomRowItem { Id = 10439, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse(Details);

        SeasonalRowService.ApplyDetails(item, doc.RootElement, "en");

        Assert.Equal("Three witches return to Salem.", item.Overview);
        Assert.Equal(7.1, item.Rating);
        Assert.Equal(96 * TimeSpan.TicksPerMinute, item.RunTimeTicks);
        Assert.Equal(new[] { "Comedy", "Fantasy" }, item.Genres);
        Assert.Equal("/titled.jpg", item.BackdropUrl);
    }

    [Fact]
    public void ABackdropWithoutTheTitleKeepsTheOneDiscoverGave()
    {
        var item = new CustomRowItem { Id = 10439, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse("""{"runtime": 0, "images": {"backdrops": [{"iso_639_1": null, "file_path": "/plain.jpg"}]}}""");

        SeasonalRowService.ApplyDetails(item, doc.RootElement, "en");

        Assert.Equal("/discover.jpg", item.BackdropUrl);
        Assert.Null(item.RunTimeTicks);
        Assert.Null(item.Genres);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void TheMetadataLanguageIsATwoLetterCode(string? configured, string expected)
    {
        Assert.Equal(expected, SeasonalRowService.MetadataLanguage(configured));
    }
}
