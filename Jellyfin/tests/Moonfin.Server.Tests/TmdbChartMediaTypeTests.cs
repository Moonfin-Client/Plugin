using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// TMDB's movie and TV charts leave media_type off their results, so the chart path
/// is all that says a result is a show. Labelling one a movie sends a tap to the
/// movie with the same id, which is a different title.
/// </summary>
public class TmdbChartMediaTypeTests
{
    [Theory]
    [InlineData("tv/popular")]
    [InlineData("tv/top_rated")]
    [InlineData("tv/airing_today")]
    [InlineData("tv/on_the_air")]
    [InlineData("trending/tv/day")]
    [InlineData("discover/tv?with_keywords=207317")]
    public void ATvChartHoldsShows(string path)
    {
        Assert.Equal("tv", CustomRowFetchService.ChartMediaType(path));
    }

    [Theory]
    [InlineData("movie/popular")]
    [InlineData("movie/now_playing")]
    [InlineData("trending/movie/week")]
    [InlineData("discover/movie?with_keywords=207317&watch_region=tv")]
    public void AMovieChartHoldsMovies(string path)
    {
        Assert.Equal("movie", CustomRowFetchService.ChartMediaType(path));
    }
}
