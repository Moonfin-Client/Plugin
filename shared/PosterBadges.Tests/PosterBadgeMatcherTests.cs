using System.Collections.Generic;
using System.Linq;
using Moonfin.PosterBadges;
using Xunit;

namespace Moonfin.PosterBadges.Tests;

/// <summary>
/// The VFQ rules from the admin page, checked against how real releases label their tracks
/// and files.
/// </summary>
public class PosterBadgeMatcherTests
{
    private enum TrackType { Audio, Video, Subtitle }

    private sealed class Track
    {
        public TrackType Type { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
        public int? Height { get; set; }
    }

    private static Track Audio(string language, string? title = null) =>
        new Track { Type = TrackType.Audio, Language = language, Title = title };

    private static PosterBadgeCondition When(string field, string value, string op = PosterBadgeMatcher.Matches) =>
        new PosterBadgeCondition { Field = field, Operator = op, Value = value };

    private static PosterBadgeRule Rule(string label, params PosterBadgeCondition[] conditions) =>
        new PosterBadgeRule { Label = label, Color = "#0EA5E9", Conditions = conditions.ToList() };

    private const string FromFrance = "VFF|VFI|France|TRUEFRENCH";

    // Quebec French is the only French on offer: a track or the file says VFQ, and nothing
    // says French from France. A VF2 carries both, so it gets no badge.
    private static readonly List<PosterBadgeRule> VfqRules = new()
    {
        Rule(
            "VFQ",
            When("Audio.Title", "VFQ|Qu[ée]bec|Canad"),
            When("Audio.Title", FromFrance, PosterBadgeMatcher.NotMatches),
            When("Path", @"\bVF[F2I]\b|TRUEFRENCH", PosterBadgeMatcher.NotMatches)),
        Rule(
            "VFQ",
            When("Path", @"\bVFQ\b"),
            When("Audio.Language", "^(fre|fra)$"),
            When("Audio.Title", FromFrance, PosterBadgeMatcher.NotMatches)),
    };

    private static IEnumerable<string> Labels(string path, params Track[] tracks) =>
        PosterBadgeMatcher.Match(VfqRules, path, tracks).Select(b => b.Label);

    [Fact]
    public void AQuebecTrackWithNoOtherFrenchGetsTheBadge()
    {
        Assert.Equal(
            new[] { "VFQ" },
            Labels("/films/Movie.2025.MULTi.1080p.mkv", Audio("eng", "VO"), Audio("fre", "French (VFQ)")));
    }

    [Fact]
    public void AFileNamedVfqCountsWhenItsTrackJustSaysVf()
    {
        Assert.Equal(
            new[] { "VFQ" },
            Labels("/films/Backrooms.2026.MULTi.VFQ.2160p.mkv", Audio("eng", "VO"), Audio("fre", "VF")));
    }

    [Fact]
    public void AVf2WithBothFrenchDubsGetsNothing()
    {
        Assert.Empty(Labels(
            "/films/Supergirl.2026.REPACK.MULTI.VF2.mkv", Audio("fre", "VFF"), Audio("fre", "VFQ"), Audio("eng", "VO")));
        Assert.Empty(Labels(
            "/films/Obsession.2026.CUSTOM.MULTi.VF2.mkv", Audio("eng", "English (VO)"), Audio("fre", "French (VFF)"), Audio("fre", "French (VFQ)")));
    }

    [Fact]
    public void AFileNamedVfqWithNoFrenchTrackGetsNothing()
    {
        Assert.Empty(Labels("/films/Wicked (2024) vfq.mkv", Audio("eng"), Audio("pol")));
    }

    [Fact]
    public void FrenchFromFranceAloneGetsNothing()
    {
        Assert.Empty(Labels("/films/Movie.2026.MULTi.TRUEFRENCH.mkv", Audio("eng", "VO"), Audio("fre", "VFF")));
    }

    [Fact]
    public void TwoMatchingRulesWithOneLabelShowOneBadge()
    {
        var badges = PosterBadgeMatcher.Match(
            VfqRules,
            "/films/Movie.2025.VFQ.1080p.mkv",
            new[] { Audio("fre", "French (VFQ)") });

        Assert.Single(badges);
        Assert.Equal("#0EA5E9", badges[0].Color);
    }

    [Fact]
    public void ANumberIsComparedAsANumber()
    {
        var rules = new[] { Rule("4K", When("Video.Height", "2000", PosterBadgeMatcher.AtLeast)) };

        Assert.Single(PosterBadgeMatcher.Match(rules, null, new[] { new Track { Type = TrackType.Video, Height = 2160 } }));
        Assert.Empty(PosterBadgeMatcher.Match(rules, null, new[] { new Track { Type = TrackType.Video, Height = 1080 } }));
    }

    [Fact]
    public void ABrokenOrEmptyRuleNeverMatches()
    {
        var rules = new[]
        {
            Rule("Broken", When("Path", "(unclosed")),
            Rule("Broken negative", When("Path", "(unclosed", PosterBadgeMatcher.NotMatches)),
            Rule("No conditions"),
            new PosterBadgeRule { Label = "Off", Enabled = false, Conditions = { When("Path", ".") } },
            Rule("Unknown field", When("Audio.NoSuchProperty", ".")),
        };

        Assert.Empty(PosterBadgeMatcher.Match(rules, "/films/anything.mkv", new[] { Audio("fre", "VFQ") }));
    }
}
