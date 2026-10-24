using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class RdbIdentityTests
{
    [Theory]
    [InlineData("Game 1+2", "Game 12", false)]
    [InlineData("Spider-Man", "Spiderman", false)]
    [InlineData("Tom & Jerry", "Tom - Jerry", false)]
    [InlineData("Tom - Jerry", "Tom Jerry", true)]
    [InlineData("Ariel - The Little Mermaid", "Ariel the Little Mermaid", true)]
    [InlineData("Venom - Spider-Man - Separation Anxiety", "Venom . Spider-Man - Separation Anxiety", true)]
    [InlineData("AC/DC", "AC DC", false)]
    [InlineData("Title: Sub", "Title _ Sub", false)]
    [InlineData("Aleste (Japan) (En)", "Aleste (Japan)", true)]
    [InlineData("Ghouls'n Ghosts (World) (En,Ja) (Rev A)", "Ghouls'n Ghosts (USA, Europe)", true)]
    [InlineData("Sonic & Knuckles (World) (Lock-On)", "Sonic & Knuckles (World)", false)]
    [InlineData("Tetris (USA)", "Tetris (USA) (Tengen) (Unl)", false)]
    [InlineData("Pitfall! (USA)", "Pitfall II - Lost Caverns (USA)", false)]
    [InlineData("Game (USA) [b]", "Game (USA)", false)]
    [InlineData("Game (USA) [b]", "game (europe) [B]", true)]
    [InlineData("Game (USA) (Beta)", "Game (Europe) (beta)", true)]
    [InlineData("Game (En-Gb)", "Game (USA)", true)]
    [InlineData("Game (Frobnicated)", "Game (USA)", false)]
    [InlineData("", "", false)]
    [InlineData("(USA)", "(USA)", false)]
    public void SameGame(string a, string b, bool expected)
    {
        Assert.Equal(expected, RdbIdentity.Matches(RdbIdentity.Of(a), b));
        Assert.Equal(expected, RdbIdentity.Matches(RdbIdentity.Of(b), a));
    }

    [Theory]
    [InlineData("")]
    [InlineData("()")]
    [InlineData("(USA)")]
    [InlineData("(((")]
    [InlineData("Game (USA")]
    [InlineData("Game USA)")]
    [InlineData("Game [USA")]
    [InlineData("Game ]")]
    [InlineData("Game ()()[]")]
    [InlineData("Game \uD800 (USA)")]
    public void MalformedNames_DoNotThrow(string name)
    {
        _ = RdbIdentity.TitleKey(name);
        _ = RdbIdentity.EditionTags(name);
        _ = RdbIdentity.RegionsOf(name);
        _ = RdbIdentity.Matches(RdbIdentity.Of(name), "Game (USA)");
        Assert.False(RdbIdentity.Matches(RdbIdentity.Of(name), null));
    }

    [Fact]
    public void TitleKey_TreatsUnderscoreAsOrdinary()
    {
        Assert.Equal("title _ sub", RdbIdentity.TitleKey("Title _ Sub (USA)"));
        Assert.NotEqual(RdbIdentity.TitleKey("Title _ Sub"), RdbIdentity.TitleKey("Title Sub"));
    }

    private static List<string?> Siblings(string primary, int max, params string[] others)
    {
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal);
        RdbRecord? primaryRecord = null;
        foreach (var name in others.Prepend(primary))
        {
            var record = new RdbRecord { Name = name };
            primaryRecord ??= record;
            // Several keys per record, as in a real index.
            byName[RdbMatcher.NormalizeName(name)] = record;
            byName[RdbMatcher.NormalizeName(name) + "x"] = record;
        }

        var index = new RdbPlatformIndex(new Dictionary<uint, RdbRecord>(), byName);
        return new RdbMatcher().FindSiblings(index, primaryRecord!, max).Select(r => r.Name).ToList();
    }

    [Fact]
    public void FindSiblings_NeverYieldsADifferentEditionOrTitle()
    {
        Assert.Empty(Siblings("Tetris (USA)", 6, "Tetris (USA) (Tengen) (Unl)", "Tetris (Europe) (Beta)"));
        Assert.Empty(Siblings("Pitfall! (USA)", 6, "Pitfall II - Lost Caverns (USA)"));
    }

    [Fact]
    public void FindSiblings_ReturnsSameGameVariantsInPreferenceOrder_Deduplicated()
    {
        var result = Siblings(
            "Game (Korea)", 10,
            "Game (Japan)", "Game (Europe) (Rev 1)", "Game (World) (En,Ja)", "Game (USA)", "Game (Korea) (Beta)", "Game (Brazil) (Pt)");

        Assert.Equal(
            new[] { "Game (USA)", "Game (World) (En,Ja)", "Game (Europe) (Rev 1)", "Game (Japan)", "Game (Brazil) (Pt)" },
            result);
    }

    [Fact]
    public void FindSiblings_SharedRegionComesFirst_AndTheCapApplies()
    {
        var result = Siblings("Game (Japan)", 2, "Game (USA)", "Game (Europe)", "Game (Japan) (Rev A)");

        Assert.Equal(new[] { "Game (Japan) (Rev A)", "Game (USA)" }, result);
    }

    [Fact]
    public void FindSiblings_UnknownTagBlocks_AndEmptyOrNullNamesDoNotThrow()
    {
        Assert.Empty(Siblings("Game (USA)", 6, "Game (USA) (Frobnicated)"));
        Assert.Empty(Siblings("(USA)", 6, "(Europe)", "()"));
        var index = new RdbPlatformIndex(new Dictionary<uint, RdbRecord>(), new Dictionary<string, RdbRecord>());
        Assert.Empty(new RdbMatcher().FindSiblings(index, new RdbRecord { Name = null }, 6));
    }

    [Theory]
    [InlineData("Ariel - The Little Mermaid (USA, Europe)", "(usa, europe)")]
    [InlineData("Game  (USA)   (Rev A)", "(usa) (rev a)")]
    [InlineData("Game (USA) [b]  ", "(usa) [b]")]
    [InlineData("Plain Title", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("(USA)", "(usa)")]
    public void TrailingGroupText_IsLowerCasedWithCollapsedWhitespace(string? name, string expected)
    {
        Assert.Equal(expected, RdbIdentity.TrailingGroupText(name));
    }
}
