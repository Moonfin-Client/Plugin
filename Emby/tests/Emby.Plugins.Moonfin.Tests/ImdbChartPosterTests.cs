using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// IMDb chart posters are handed to clients at a fixed width instead of as the original.
/// </summary>
public sealed class ImdbChartPosterTests
{
    [Fact]
    public void TheOriginalIsAskedForAtThePosterWidth()
    {
        Assert.Equal(
            "https://m.media-amazon.com/images/M/MV5BMDAyY2Fh@._V1_UX500_.jpg",
            ImdbChartFetcher.SizedPosterUrl("https://m.media-amazon.com/images/M/MV5BMDAyY2Fh@._V1_.jpg"));
    }

    [Fact]
    public void AUrlWithoutTheTransformBlockIsLeftAlone()
    {
        Assert.Equal("https://m.media-amazon.com/images/M/poster.jpg", ImdbChartFetcher.SizedPosterUrl("https://m.media-amazon.com/images/M/poster.jpg"));
        Assert.Equal("https://m.media-amazon.com/images/M/MV5B@._V1_UX200_.jpg", ImdbChartFetcher.SizedPosterUrl("https://m.media-amazon.com/images/M/MV5B@._V1_UX200_.jpg"));
    }

    [Fact]
    public void NothingStaysNothing()
    {
        Assert.Null(ImdbChartFetcher.SizedPosterUrl(null));
        Assert.Equal("", ImdbChartFetcher.SizedPosterUrl(""));
    }
}
