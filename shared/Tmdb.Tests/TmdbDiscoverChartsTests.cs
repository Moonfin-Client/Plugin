using Moonfin.Tmdb;
using Xunit;

namespace Moonfin.Tmdb.Tests;

public sealed class TmdbDiscoverChartsTests
{
    private static readonly DateTime Today = new(2026, 11, 12);

    [Fact]
    public void AServiceChartReadsMoviesAndShowsInTheRegion()
    {
        var paths = TmdbDiscoverCharts.Paths("provider_hbo_max", "gb", Today)!;

        Assert.Equal(2, paths.Length);
        Assert.StartsWith("discover/movie?", paths[0]);
        Assert.StartsWith("discover/tv?", paths[1]);
        Assert.All(paths, p => Assert.Contains("with_watch_providers=1899|384&watch_region=GB", p));
    }

    [Fact]
    public void NewDigitalReleasesCoverTheLastSixtyDays()
    {
        var path = Assert.Single(TmdbDiscoverCharts.Paths("new_digital_movies", null, Today)!);

        Assert.Contains("region=US", path);
        Assert.Contains("release_date.gte=2026-09-13&release_date.lte=2026-11-12", path);
    }

    [Theory]
    [InlineData("provider_hulu")]
    [InlineData("provider_")]
    [InlineData("trending")]
    public void UnknownChartsHaveNoPath(string name) => Assert.Null(TmdbDiscoverCharts.Paths(name, "US", Today));

    [Theory]
    [InlineData("de", "DE")]
    [InlineData("UK", "GB")]
    [InlineData("FR", "US")]
    [InlineData("u1", "US")]
    [InlineData(null, "US")]
    public void JustWatchFallsBackToTheUs(string? region, string country) =>
        Assert.Equal(country, TmdbDiscoverCharts.JustWatchCountry(region));

    [Fact]
    public void ARegionMustBeTwoLetters() => Assert.Equal("US", TmdbDiscoverCharts.Region("USA&x=1"));
}
