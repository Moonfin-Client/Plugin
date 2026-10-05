using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class SeasonalCountryTests
{
    [Theory]
    [InlineData("us", "GB", "US")]
    [InlineData(" CA ", null, "CA")]
    [InlineData(null, "GB", "GB")]
    [InlineData("", "de", "DE")]
    [InlineData(null, "US", null)]
    [InlineData(null, null, null)]
    [InlineData("ZZ", "GB", null)]
    [InlineData("USA", "GB", "GB")]
    [InlineData("1A", null, null)]
    public void ResolvesTheRequestedCodeThenTheServersUnlessThatIsTheDefault(string? requested, string? server, string? expected)
    {
        Assert.Equal(expected, SeasonalCountry.Resolve(requested, server));
    }
}
