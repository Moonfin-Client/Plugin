using Emby.Plugins.Moonfin.Api;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Both name normalisers fold accents, so a rom named "Pokemon" matches the record for "Pokémon".
/// </summary>
public class GameNameNormalizationTests
{
    [Theory]
    [InlineData("Pokémon Red", "pokemonred")]
    [InlineData("Pokemon Red", "pokemonred")]
    [InlineData("Ōkami", "okami")]
    [InlineData("Café International", "cafeinternational")]
    public void NormalizeAlphanumericLower_FoldsAccents(string input, string expected)
    {
        Assert.Equal(expected, GamesScanner.NormalizeAlphanumericLower(input));
    }

    [Fact]
    public void NormalizeAlphanumericLower_MatchesAcrossSpellings()
    {
        Assert.Equal(
            GamesScanner.NormalizeAlphanumericLower("Pokémon Red"),
            GamesScanner.NormalizeAlphanumericLower("Pokemon Red"));
    }

    [Theory]
    [InlineData("Super Mario Bros.", "supermariobros")]
    [InlineData("Legend of Zelda, The", "legendofzeldathe")]
    public void NormalizeAlphanumericLower_LeavesUnaccentedNamesAlone(string input, string expected)
    {
        Assert.Equal(expected, GamesScanner.NormalizeAlphanumericLower(input));
    }

    [Theory]
    [InlineData("Pokémon Red (USA)", "pokemonred")]
    [InlineData("Pokemon Red (USA)", "pokemonred")]
    [InlineData("Café International [!]", "cafeinternational")]
    public void LaunchBoxNormalizeName_FoldsAccentsAndStillDropsBracketedText(
        string input,
        string expected)
    {
        Assert.Equal(expected, GameLaunchBoxHelper.NormalizeName(input));
    }

    // NTFS allows an unpaired surrogate, which string.Normalize throws on; the name stays unfolded.
    [Theory]
    [InlineData("\ud800")]
    [InlineData("Game\udfff Name")]
    [InlineData("\udc00Zelda")]
    public void NormalizeAlphanumericLower_SurvivesAnUnpairedSurrogate(string input)
    {
        Assert.Null(Record.Exception(() => GamesScanner.NormalizeAlphanumericLower(input)));
    }

    [Fact]
    public void LaunchBoxNormalizeName_SurvivesAnUnpairedSurrogate()
    {
        Assert.Null(Record.Exception(() => GameLaunchBoxHelper.NormalizeName("Game\ud800 (USA)")));
    }

    // Falling back must still yield the usable part of the name, not give up on it.
    [Fact]
    public void NormalizeAlphanumericLower_StillReadsTheNameAroundABadSurrogate()
    {
        Assert.Equal("gamename", GamesScanner.NormalizeAlphanumericLower("Game\udfffName"));
    }

    // Folding runs first, so a combining mark mustn't be mistaken for a bracket.
    [Fact]
    public void LaunchBoxNormalizeName_KeepsNestedBracketsBalanced()
    {
        Assert.Equal("game", GameLaunchBoxHelper.NormalizeName("Game (Europe (En,Fr,De)) [b1]"));
    }
}
