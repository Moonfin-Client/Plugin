using System.Text.Json;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// IMDb chart titles get their backdrop from the TMDB find call that already resolves their
/// rating, instead of reusing the poster.
/// </summary>
public sealed class ImdbChartBackdropTests
{
    private const string Find = """
        {
          "movie_results": [{"id": 400155, "title": "Hotel Transylvania 3: Summer Vacation", "backdrop_path": "/ht3.jpg"}],
          "tv_results": [{"id": 1399, "name": "Game of Thrones", "backdrop_path": null}]
        }
        """;

    private static CustomRowItem Chart(string type = "Movie", string? backdrop = null) => new()
    {
        Name = "Hotel Transylvania 3: Summer Vacation",
        Type = type,
        PosterUrl = "https://m.media-amazon.com/images/M/poster.jpg",
        BackdropUrl = backdrop,
        ProviderIds = new CustomRowItemProviderIds { Imdb = "tt5220122" }
    };

    [Fact]
    public void TheFindResultCarriesTheBackdrop()
    {
        using var doc = JsonDocument.Parse(Find);

        Assert.Equal(("movie", 400155L, (string?)"/ht3.jpg"), TmdbRatingService.FirstMatch(doc.RootElement, "movie_results", "movie"));
        Assert.Equal(("tv", 1399L, (string?)null), TmdbRatingService.FirstMatch(doc.RootElement, "tv_results", "tv"));
    }

    [Fact]
    public void AnImdbTitleGetsItsTmdbIdAndBackdrop()
    {
        var item = Chart();

        TmdbRatingService.ApplyFound(item, "movie", 400155, "/ht3.jpg");

        Assert.Equal("400155", item.ProviderIds.Tmdb);
        Assert.Equal("/ht3.jpg", item.BackdropUrl);
    }

    [Fact]
    public void ABackdropOfItsOwnIsKeptButThePosterIsReplaced()
    {
        var own = Chart(backdrop: "/own.jpg");
        var poster = Chart(backdrop: "https://m.media-amazon.com/images/M/poster.jpg");

        TmdbRatingService.ApplyFound(own, "movie", 400155, "/ht3.jpg");
        TmdbRatingService.ApplyFound(poster, "movie", 400155, "/ht3.jpg");

        Assert.Equal("/own.jpg", own.BackdropUrl);
        Assert.Equal("/ht3.jpg", poster.BackdropUrl);
    }

    [Fact]
    public void AShowTmdbFilesAsAMovieKeepsNoTmdbId()
    {
        var item = Chart(type: "Series");

        TmdbRatingService.ApplyFound(item, "movie", 400155, "/ht3.jpg");

        Assert.Null(item.ProviderIds.Tmdb);
        Assert.Equal("/ht3.jpg", item.BackdropUrl);
    }

    [Fact]
    public void AFindCachedBeforeBackdropsIsAskedAgainOnce()
    {
        var old = JsonSerializer.Deserialize<TmdbRatingEntry>("""{"mediaType": "movie", "tmdbId": 400155, "cachedAt": "2026-10-01T00:00:00+00:00"}""")!;
        var unknown = JsonSerializer.Deserialize<TmdbRatingEntry>("""{"cachedAt": "2026-10-01T00:00:00+00:00"}""")!;
        var current = new TmdbRatingEntry { MediaType = "movie", TmdbId = 400155, BackdropChecked = true };

        Assert.False(TmdbRatingService.IsCurrentFind(old));
        Assert.True(TmdbRatingService.IsCurrentFind(unknown));
        Assert.True(TmdbRatingService.IsCurrentFind(current));
    }
}
