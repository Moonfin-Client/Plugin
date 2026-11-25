using System.Reflection;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The age of a letter-only rating such as R comes from Jellyfin through reflection, since the
/// method changed between 10.10 and 10.11. A miss there would quietly drop colours and icons
/// per age for those ratings.
/// </summary>
public sealed class PosterBadgeAgeBindingTests
{
    private static object? Field(string name) =>
        typeof(PosterBadgesController).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

    [Fact]
    public void TheRatingLookupBindsAgainstTheReferencedServer()
    {
        Assert.NotNull((MethodInfo?)Field("RatingScoreMethod"));
    }

    [Fact]
    public void BothAnswerShapesGiveTheAge()
    {
        var ageFrom = typeof(PosterBadgesController).GetMethod("AgeFrom", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing AgeFrom");

        Assert.Equal(17, ageFrom.Invoke(null, new object?[] { 17 }));
        Assert.Equal(17, ageFrom.Invoke(null, new object?[] { new { Score = 17, SubScore = (int?)null } }));
        Assert.Null(ageFrom.Invoke(null, new object?[] { null }));
    }
}
