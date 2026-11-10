using Xunit;

namespace Moonfin.CustomRows.Tests;

public class TitleIdsTests
{
    [Fact]
    public void TmdbIdsMatchWithinTheSameKind() =>
        Assert.Equal(0, new TitleIds(false, "603", null, null).MatchRank(new TitleIds(false, "603", "tt0133093", null)));

    [Fact]
    public void TmdbIdsNeverMatchAcrossKinds() =>
        Assert.Equal(-1, new TitleIds(false, "1399", null, null).MatchRank(new TitleIds(true, "1399", null, null)));

    [Fact]
    public void ImdbMatchesIgnoringCase() =>
        Assert.Equal(1, new TitleIds(false, null, "TT0133093", null).MatchRank(new TitleIds(false, "999", "tt0133093", null)));

    [Fact]
    public void TvdbIsTheWeakestMatch() =>
        Assert.Equal(2, new TitleIds(true, "1", "tt1", "81189").MatchRank(new TitleIds(true, "2", "tt2", "81189")));

    [Fact]
    public void TmdbBeatsImdbWhenBothAgree() =>
        Assert.Equal(0, new TitleIds(false, "603", "tt0133093", null).MatchRank(new TitleIds(false, "603", "tt0133093", null)));

    [Fact]
    public void DisagreeingIdsDoNotMatch() =>
        Assert.False(new TitleIds(false, "603", "tt0133093", null).Matches(new TitleIds(false, "604", "tt0234215", null)));

    [Fact]
    public void BlankAndZeroIdsCountAsMissing()
    {
        var ids = new TitleIds(false, " 0 ", "", "  ");
        Assert.True(ids.IsEmpty);
        Assert.False(ids.Matches(new TitleIds(false, "0", "", " ")));
    }

    [Fact]
    public void IdsAreTrimmed() =>
        Assert.True(new TitleIds(false, " 603 ", null, null).Matches(new TitleIds(false, "603", null, null)));

    [Theory]
    [InlineData("Series", true)]
    [InlineData("series", true)]
    [InlineData("Movie", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlySeriesIsTheShowKind(string? type, bool series) =>
        Assert.Equal(series, TitleIds.IsSeriesType(type));

    [Fact]
    public void EqualIdsShareAHash()
    {
        var a = new TitleIds(false, "603", "TT0133093", null);
        var b = new TitleIds(false, "603", "tt0133093", null);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
