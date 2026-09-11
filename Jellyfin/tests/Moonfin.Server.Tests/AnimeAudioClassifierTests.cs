using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// "Subbed" means Japanese audio and nothing else; anything carrying another spoken
/// language is a dub.
/// </summary>
public class AnimeAudioClassifierTests
{
    [Fact]
    public void JapaneseOnlyIsSubbed()
    {
        Assert.Equal(AnimeAudioKind.Subbed, AnimeAudioClassifier.Classify(new[] { "jpn" }));
    }


    [Fact]
    public void DualAudioIsReportedAsBoth()
    {
        Assert.Equal(
            AnimeAudioKind.SubbedAndDubbed,
            AnimeAudioClassifier.Classify(new[] { "jpn", "ger" }));
    }

    [Fact]
    public void DualAudioCollapsesToDubbedUnlessAskedFor()
    {
        Assert.Equal(
            AnimeAudioKind.Dubbed,
            AnimeAudioClassifier.Collapse(AnimeAudioKind.SubbedAndDubbed, separateDualAudio: false));

        Assert.Equal(
            AnimeAudioKind.SubbedAndDubbed,
            AnimeAudioClassifier.Collapse(AnimeAudioKind.SubbedAndDubbed, separateDualAudio: true));
    }

    [Fact]
    public void CollapsingLeavesTheOtherVerdictsAlone()
    {
        Assert.Equal(AnimeAudioKind.Subbed, AnimeAudioClassifier.Collapse(AnimeAudioKind.Subbed, false));
        Assert.Equal(AnimeAudioKind.Dubbed, AnimeAudioClassifier.Collapse(AnimeAudioKind.Dubbed, false));
    }

    [Fact]
    public void AnotherLanguageOnItsOwnIsDubbed()
    {
        Assert.Equal(AnimeAudioKind.Dubbed, AnimeAudioClassifier.Classify(new[] { "eng" }));
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("jp")]
    [InlineData("jap")]
    [InlineData("Japanese")]
    [InlineData("JPN")]
    public void JapaneseIsRecognisedHoweverItIsSpelled(string code)
    {
        // Jellyfin normalises most tracks, but a remux carries whatever the muxer wrote.
        Assert.Equal(AnimeAudioKind.Subbed, AnimeAudioClassifier.Classify(new[] { code }));
    }

    [Theory]
    [InlineData("und")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void UntaggedAudioIsNotClassified(string? code)
    {
        // Guessing here would put a wrong pill on the very common untagged rip.
        Assert.Null(AnimeAudioClassifier.Classify(new[] { code }));
    }

    [Fact]
    public void UntaggedTracksDoNotCountAsAForeignDub()
    {
        Assert.Equal(AnimeAudioKind.Subbed, AnimeAudioClassifier.Classify(new[] { "jpn", "und" }));
    }

    [Fact]
    public void NoAudioAtAllIsNotClassified()
    {
        Assert.Null(AnimeAudioClassifier.Classify(Array.Empty<string?>()));
    }

    [Fact]
    public void ASeasonWhoseEpisodesAgreeTakesThatVerdict()
    {
        var kinds = new AnimeAudioKind?[] { AnimeAudioKind.Dubbed, AnimeAudioKind.Dubbed };

        Assert.Equal(AnimeAudioKind.Dubbed, AnimeAudioClassifier.ClassifySeason(kinds));
    }

    [Fact]
    public void ASeasonWithBothStaysBlank()
    {
        // Labelling by majority would be wrong for whichever episodes are in the minority.
        var kinds = new AnimeAudioKind?[] { AnimeAudioKind.Dubbed, AnimeAudioKind.Subbed };

        Assert.Null(AnimeAudioClassifier.ClassifySeason(kinds));
    }

    [Fact]
    public void UnclassifiedEpisodesDoNotSpoilASeason()
    {
        // One untagged file in an otherwise subbed season should not silence the pill.
        var kinds = new AnimeAudioKind?[] { AnimeAudioKind.Subbed, null, AnimeAudioKind.Subbed };

        Assert.Equal(AnimeAudioKind.Subbed, AnimeAudioClassifier.ClassifySeason(kinds));
    }

    [Fact]
    public void ASeasonWithNothingClassifiedStaysBlank()
    {
        Assert.Null(AnimeAudioClassifier.ClassifySeason(new AnimeAudioKind?[] { null, null }));
    }
}
