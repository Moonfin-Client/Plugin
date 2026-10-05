using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SeasonalSuggestionDetailsTests
{
    private const string Details = """
        {
          "overview": "A boy rides a train to the North Pole.",
          "vote_average": 6.74,
          "runtime": 100,
          "genres": [{"id": 16, "name": "Animation"}, {"id": 12, "name": "Adventure"}],
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
        var item = new CustomRowItem { Id = 10719, BackdropUrl = "/discover.jpg" };
        using var doc = JsonDocument.Parse(Details);

        SeasonalRowService.ApplyDetails(item, doc.RootElement, "en");

        Assert.Equal("A boy rides a train to the North Pole.", item.Overview);
        Assert.Equal(6.7, item.Rating);
        Assert.Equal(100 * TimeSpan.TicksPerMinute, item.RunTimeTicks);
        Assert.Equal(["Animation", "Adventure"], item.Genres);
        Assert.Equal("/titled.jpg", item.BackdropUrl);
    }

    [Fact]
    public void ABackdropWithoutTheTitleKeepsTheOneDiscoverGave()
    {
        var item = new CustomRowItem { Id = 10719, BackdropUrl = "/discover.jpg" };
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
