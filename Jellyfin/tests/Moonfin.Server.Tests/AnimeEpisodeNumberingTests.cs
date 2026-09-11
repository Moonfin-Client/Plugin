using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Tests the logic that assigns absolute episode numbers to a series, flattening per-season numbering.
/// </summary>
public class AnimeEpisodeNumberingTests
{
    [Fact]
    public void PerSeasonNumberingIsFlattened()
    {
        var episodes = new List<(int Season, int Index)>
        {
            (1, 1), (1, 2), (1, 3),
            (2, 1), (2, 2)
        };

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, AnimeMarkerResolver.AssignAbsoluteNumbers(episodes));
    }

    [Fact]
    public void AbsoluteNumberingIsKeptAsIs()
    {
        // How One Piece is usually laid out: seasons are arcs, episode numbers run through.
        var episodes = new List<(int Season, int Index)>
        {
            (3, 144), (3, 145),
            (4, 207), (4, 208)
        };

        Assert.Equal(new[] { 144, 145, 207, 208 }, AnimeMarkerResolver.AssignAbsoluteNumbers(episodes));
    }

    [Fact]
    public void AGapInsideASeasonDoesNotShiftLaterEpisodes()
    {
        // Episode 2 of season 1 is missing from the library. Season 2 must still start at 4,
        // because season 1 is four episodes long whether or not they are all present.
        var episodes = new List<(int Season, int Index)>
        {
            (1, 1), (1, 3), (1, 4),
            (2, 1), (2, 2)
        };

        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, AnimeMarkerResolver.AssignAbsoluteNumbers(episodes));
    }

    [Fact]
    public void SingleSeasonSeriesAreUnchanged()
    {
        var episodes = new List<(int Season, int Index)> { (1, 1), (1, 2), (1, 3) };

        Assert.Equal(new[] { 1, 2, 3 }, AnimeMarkerResolver.AssignAbsoluteNumbers(episodes));
    }

    [Fact]
    public void ThreeSeasonsAccumulate()
    {
        var episodes = new List<(int Season, int Index)>
        {
            (1, 25),
            (2, 1), (2, 12),
            (3, 1)
        };

        // Season 1 is 25 long, season 2 is 12, so season 3 episode 1 is absolute 38.
        Assert.Equal(new[] { 25, 26, 37, 38 }, AnimeMarkerResolver.AssignAbsoluteNumbers(episodes));
    }

    [Fact]
    public void RepeatedSlotsDoNotChangeTheNumberingScheme()
    {
        // A recursive query under a series returns each episode once per ancestor, so the
        // caller now feeds distinct season/episode slots. Absolute numbering has to survive
        // that: seen doubled, 144/145/207 would look like repeats and pick per-season.
        var slots = new List<(int Season, int Index)> { (3, 144), (3, 145), (4, 207) };

        Assert.True(AnimeMarkerResolver.IsAlreadyAbsolute(slots));
        Assert.Equal(new[] { 144, 145, 207 }, AnimeMarkerResolver.AssignAbsoluteNumbers(slots));
    }

    [Fact]
    public void EmptyInputIsEmpty()
    {
        Assert.Empty(AnimeMarkerResolver.AssignAbsoluteNumbers(new List<(int, int)>()));
    }
}
