using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Pins the separation between TMDB network ids and production company ids.
/// Without it the second of the two cached wins, and an unrelated item then
/// renders the wrong studio logo.
/// </summary>
public class StudioLogoNamespaceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(49)]
    [InlineData(213)]
    public void ANetworkNeverSharesACacheIdWithTheCompanyOfTheSameNumber(int tmdbId)
    {
        Assert.NotEqual(tmdbId, StudioLogoFetchService.NetworkCacheId(tmdbId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(49)]
    [InlineData(213)]
    public void ANetworkNeverSharesALogoFileWithTheCompanyOfTheSameNumber(int tmdbId)
    {
        var company = StudioLogoCacheService.ImageFileName(tmdbId);
        var network = StudioLogoCacheService.ImageFileName(
            StudioLogoFetchService.NetworkCacheId(tmdbId));

        Assert.NotEqual(company, network);
    }

    [Fact]
    public void CompanyLogosKeepTheirExistingFileNames()
    {
        Assert.Equal("49.png", StudioLogoCacheService.ImageFileName(49));
    }

    [Fact]
    public void NetworkLogoFilesArePrefixedRatherThanNegated()
    {
        var name = StudioLogoCacheService.ImageFileName(
            StudioLogoFetchService.NetworkCacheId(49));

        Assert.Equal("n49.png", name);
    }
}
