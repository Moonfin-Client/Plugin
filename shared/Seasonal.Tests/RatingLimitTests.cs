using System.Collections.Generic;
using Xunit;

namespace Moonfin.Seasonal.Tests;

public sealed class RatingLimitTests
{
    private static readonly Dictionary<string, int> UsRatings = new Dictionary<string, int>
    {
        ["G"] = 1,
        ["PG"] = 7,
        ["PG-13"] = 13,
        ["R"] = 17,
        ["NC-17"] = 18
    };

    [Fact]
    public void WithoutALimitEverythingPassesUnratedIncluded()
    {
        var limit = new RatingLimit(null, UsRatings, "US");

        Assert.True(limit.IsAllowed("R"));
        Assert.True(limit.IsAllowed(null));
        Assert.True(limit.IsAllowed("made up"));
        Assert.False(limit.HasLimit);
    }

    [Fact]
    public void ALimitKeepsRatingsAtOrBelowItAndDropsTheRest()
    {
        var limit = new RatingLimit(13, UsRatings, "US");

        Assert.True(limit.IsAllowed("PG-13"));
        Assert.True(limit.IsAllowed("pg"));
        Assert.False(limit.IsAllowed("R"));
    }

    [Fact]
    public void UnratedAndUnknownCertificationsAreHiddenUnderALimit()
    {
        var limit = new RatingLimit(13, UsRatings, "US");

        Assert.False(limit.IsAllowed(null));
        Assert.False(limit.IsAllowed(""));
        Assert.False(limit.IsAllowed("NR"));
    }

    [Fact]
    public void ACountryPrefixOnTheCertificationIsIgnored()
    {
        var limit = new RatingLimit(13, UsRatings, "US");

        Assert.Equal(13, limit.ValueOf("US-PG-13"));
        Assert.True(limit.IsAllowed("US-PG-13"));
    }

    [Fact]
    public void ATableWithCountryPrefixedNamesStillResolves()
    {
        var limit = new RatingLimit(12, new Dictionary<string, int> { ["GB-12A"] = 12, ["GB-15"] = 15 }, "GB");

        Assert.True(limit.IsAllowed("12A"));
        Assert.False(limit.IsAllowed("15"));
    }
}
