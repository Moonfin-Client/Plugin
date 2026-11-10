using Moonfin.Games;
using Xunit;

namespace Moonfin.Games.Tests;

/// <summary>
/// One gb core plays Game Boy and Game Boy Color, but libretro keeps their art in separate sets,
/// so the system folder decides which set is tried first.
/// </summary>
public class GameBoyColorArtworkTests
{
    [Theory]
    [InlineData("GBC", "Nintendo - Game Boy Color", "Nintendo - Game Boy")]
    [InlineData("Game Boy Color", "Nintendo - Game Boy Color", "Nintendo - Game Boy")]
    [InlineData("Game Boy", "Nintendo - Game Boy", "Nintendo - Game Boy Color")]
    public void AGameBoyFolderTriesItsOwnSetBeforeTheOther(string systemName, string first, string second)
    {
        var platforms = GameArtworkStore.CandidatePlatformsFor("gb", coreWasDefaulted: false, systemName);

        Assert.Equal(first, platforms[0]);
        Assert.Contains(second, platforms);
    }

    [Fact]
    public void OtherSystemsKeepTheirOneSet()
    {
        var platforms = GameArtworkStore.CandidatePlatformsFor("gba", coreWasDefaulted: false, "GBA");

        Assert.Equal(new[] { "Nintendo - Game Boy Advance" }, platforms);
    }
}
