using System.Collections.Generic;
using System.Linq;
using Moonfin.PosterBadges;
using Xunit;

namespace Moonfin.PosterBadges.Tests;

/// <summary>
/// Any, no and every track, the text and number tests, and the audio track count.
/// </summary>
public class PosterBadgeConditionTests
{
    private enum TrackType { Audio, Video, Subtitle }

    private sealed class Track
    {
        public TrackType Type { get; set; }
        public string? Language { get; set; }
        public string? Title { get; set; }
        public int? Channels { get; set; }
    }

    private static Track Audio(string language, string? title = null, int channels = 2) =>
        new Track { Type = TrackType.Audio, Language = language, Title = title, Channels = channels };

    private static bool Shows(PosterBadgeCondition condition, string path, params Track[] tracks)
    {
        var rule = new PosterBadgeRule { Label = "X", Conditions = new List<PosterBadgeCondition> { condition } };
        return PosterBadgeMatcher.Match(new[] { rule }, path, tracks).Any();
    }

    private static PosterBadgeCondition When(string field, string op, string value, string match = PosterBadgeMatcher.Any) =>
        new PosterBadgeCondition { Field = field, Match = match, Operator = op, Value = value };

    private const string Film = "/films/Movie.2025.mkv";

    [Fact]
    public void ContainsNeedsNoPattern()
    {
        Assert.True(Shows(When("Audio.Title", PosterBadgeMatcher.Contains, "vfq"), Film, Audio("fre", "French (VFQ)")));
        Assert.False(Shows(When("Audio.Title", PosterBadgeMatcher.Contains, "VFF"), Film, Audio("fre", "French (VFQ)")));
    }

    [Fact]
    public void NoTrackHoldsOnlyWhenNoneHasIt()
    {
        var noFrance = When("Audio.Title", PosterBadgeMatcher.Contains, "VFF", PosterBadgeMatcher.None);
        Assert.True(Shows(noFrance, Film, Audio("fre", "VFQ"), Audio("eng")));
        Assert.False(Shows(noFrance, Film, Audio("fre", "VFQ"), Audio("fre", "VFF")));
    }

    [Fact]
    public void EveryTrackNeedsAllOfThemAndAtLeastOne()
    {
        var allFrench = When("Audio.Language", PosterBadgeMatcher.OneOf, "fre, fra", PosterBadgeMatcher.Every);
        Assert.True(Shows(allFrench, Film, Audio("fre"), Audio("fra")));
        Assert.False(Shows(allFrench, Film, Audio("fre"), Audio("eng")));
        Assert.False(Shows(allFrench, Film));
    }

    [Fact]
    public void IsComparesTheWholeValue()
    {
        Assert.True(Shows(When("Audio.Language", PosterBadgeMatcher.Is, "FRE"), Film, Audio("fre")));
        Assert.False(Shows(When("Audio.Language", PosterBadgeMatcher.Is, "fr"), Film, Audio("fre")));
        Assert.True(Shows(When("Audio.Channels", PosterBadgeMatcher.Is, "6.0"), Film, Audio("eng", channels: 6)));
    }

    [Fact]
    public void NumbersCompareBothWays()
    {
        var surround = Audio("eng", channels: 6);
        Assert.True(Shows(When("Audio.Channels", PosterBadgeMatcher.AtLeast, "6"), Film, surround));
        Assert.False(Shows(When("Audio.Channels", PosterBadgeMatcher.MoreThan, "6"), Film, surround));
        Assert.True(Shows(When("Audio.Channels", PosterBadgeMatcher.AtMost, "6"), Film, surround));
        Assert.False(Shows(When("Audio.Channels", PosterBadgeMatcher.LessThan, "6"), Film, surround));
    }

    [Fact]
    public void TheAudioTrackCountMakesAMultiLabel()
    {
        var multi = When("Audio.Count", PosterBadgeMatcher.AtLeast, "2");
        Assert.True(Shows(multi, Film, Audio("fre"), Audio("eng")));
        Assert.False(Shows(multi, Film, Audio("eng")));
    }

    [Fact]
    public void MultiCountsLanguagesNotTracks()
    {
        var multi = When("Audio.LanguageCount", PosterBadgeMatcher.AtLeast, "2");

        // A French dub and its audio description are one language.
        Assert.False(Shows(multi, Film, Audio("fre", "VFF"), Audio("fre", "Audiodescription")));

        // So are a dub from France and one from Quebec, whatever code each file uses.
        Assert.False(Shows(multi, Film, Audio("fre", "VFF"), Audio("fra", "VFQ"), Audio("fr-CA")));

        Assert.True(Shows(multi, Film, Audio("fre"), Audio("eng")));

        // An untagged track says nothing about its language.
        Assert.False(Shows(multi, Film, Audio("fre"), Audio("und"), new Track { Type = TrackType.Audio }));
    }

    [Fact]
    public void TheVersionFollowsEveryEditAndOnlyEdits()
    {
        List<PosterBadgeRule> Rules(string value) => new()
        {
            new PosterBadgeRule { Label = "VFQ", Conditions = new List<PosterBadgeCondition> { When("Audio.Title", PosterBadgeMatcher.Contains, value) } },
        };
        var defaults = new PosterBadgeDefaults();

        Assert.Equal(PosterBadgeMatcher.Version(Rules("VFQ"), defaults), PosterBadgeMatcher.Version(Rules("VFQ"), new PosterBadgeDefaults()));
        Assert.NotEqual(PosterBadgeMatcher.Version(Rules("VFQ"), defaults), PosterBadgeMatcher.Version(Rules("VFF"), defaults));
        Assert.NotEqual(PosterBadgeMatcher.Version(Rules("VFQ"), defaults), PosterBadgeMatcher.Version(Rules("VFQ"), new PosterBadgeDefaults { Corner = "topRight" }));
    }

    [Fact]
    public void TheTestExplainsEachLabel()
    {
        PosterBadgeRule Custom(string label, bool enabled, params PosterBadgeCondition[] conditions) =>
            new PosterBadgeRule { Label = label, Enabled = enabled, Conditions = conditions.ToList() };

        var rules = new List<PosterBadgeRule>
        {
            Custom("VFQ", true, When("Audio.Title", PosterBadgeMatcher.Contains, "VFQ"), When("Audio.Title", PosterBadgeMatcher.Contains, "VFF", PosterBadgeMatcher.None)),
            Custom("VFQ", true, When("Path", PosterBadgeMatcher.Contains, "VFQ")),
            Custom("MULTI", true, When("Audio.LanguageCount", PosterBadgeMatcher.AtLeast, "2"), When("Audio.Title", PosterBadgeMatcher.Contains, "VFF", PosterBadgeMatcher.None)),
            Custom("VOSTFR", false, When("Path", PosterBadgeMatcher.Contains, "VOSTFR")),
        };

        var results = PosterBadgeTest.Explain(rules, "/films/Movie.VFQ.mkv", new object[] { Audio("fre", "VFQ"), Audio("eng") });

        Assert.Equal(new[] { "shown", "sameText", "shown", "off" }, results.Select(r => r.Outcome));
        Assert.Equal(new[] { true, true }, results[2].Conditions);

        var missed = PosterBadgeTest.Explain(rules, "/films/Movie.mkv", new object[] { Audio("fre", "VFF") });
        Assert.Equal("notMet", missed[0].Outcome);
        Assert.Equal(new[] { false, false }, missed[0].Conditions);
    }

    [Fact]
    public void ALabelCanBeAnIconAlone()
    {
        var multi = When("Audio.LanguageCount", PosterBadgeMatcher.AtLeast, "2");
        var rules = new List<PosterBadgeRule>
        {
            new PosterBadgeRule { Label = "", Icon = "translate", Conditions = new List<PosterBadgeCondition> { multi } },
            new PosterBadgeRule { Label = " ", Icon = "translate", Conditions = new List<PosterBadgeCondition> { multi } },
            new PosterBadgeRule { Label = "MULTI", Icon = "translate", Conditions = new List<PosterBadgeCondition> { multi } },
            new PosterBadgeRule { Label = "", Icon = "", Conditions = new List<PosterBadgeCondition> { multi } },
        };

        var badges = PosterBadgeMatcher.Match(rules, Film, new[] { Audio("fre"), Audio("eng") });

        // The second icon-only rule is the same label as the first; the last has nothing to draw.
        Assert.Equal(new[] { ("", "translate"), ("MULTI", "translate") }, badges.Select(b => (b.Label, b.Icon)));
        Assert.Equal(new[] { "shown", "sameText", "shown", "noText" },
            PosterBadgeTest.Explain(rules, Film, new[] { Audio("fre"), Audio("eng") }).Select(r => r.Outcome));
    }

    [Fact]
    public void AFlagIsAnIconLikeTheOthers()
    {
        var quebec = When("Path", PosterBadgeMatcher.Contains, "VFQ");
        var rules = new List<PosterBadgeRule>
        {
            new PosterBadgeRule { Label = "VFQ", Icon = "flag-ca", Conditions = new List<PosterBadgeCondition> { quebec } },
            new PosterBadgeRule { Label = "", Icon = "flag-fr", Conditions = new List<PosterBadgeCondition> { quebec } },
            new PosterBadgeRule { Label = "", Icon = "flag-zz", Conditions = new List<PosterBadgeCondition> { quebec } },
            new PosterBadgeRule { Label = "CAT", Icon = "flag-es-ct", Conditions = new List<PosterBadgeCondition> { quebec } },
        };

        var badges = PosterBadgeMatcher.Match(rules, "/films/Movie.VFQ.mkv", new object[0]);
        Assert.Equal(new[] { ("VFQ", "flag-ca"), ("", "flag-fr"), ("CAT", "flag-es-ct") }, badges.Select(b => (b.Label, b.Icon)));
    }

    [Fact]
    public void AnUnknownIconIsDropped()
    {
        var rules = new List<PosterBadgeRule>
        {
            new PosterBadgeRule { Label = "VFQ", Icon = "dolby", Conditions = new List<PosterBadgeCondition> { When("Path", PosterBadgeMatcher.Contains, "VFQ") } },
            new PosterBadgeRule { Label = "", Icon = "dolby", Conditions = new List<PosterBadgeCondition> { When("Path", PosterBadgeMatcher.Contains, "VFQ") } },
        };

        var badge = PosterBadgeMatcher.Match(rules, "/films/Movie.VFQ.mkv", new object[0]).Single();
        Assert.Equal(("VFQ", ""), (badge.Label, badge.Icon));
    }

    [Fact]
    public void NoneTurnsThePathAround()
    {
        var notRemux = When("Path", PosterBadgeMatcher.Contains, "remux", PosterBadgeMatcher.None);
        Assert.True(Shows(notRemux, Film));
        Assert.False(Shows(notRemux, "/films/Movie.2025.REMUX.mkv"));
    }

    [Fact]
    public void ABlankValueOrABadNumberNeverReadsAsNoTrackHasIt()
    {
        Assert.False(Shows(When("Audio.Title", PosterBadgeMatcher.Contains, " ", PosterBadgeMatcher.None), Film, Audio("eng")));
        Assert.False(Shows(When("Audio.Channels", PosterBadgeMatcher.AtLeast, "six", PosterBadgeMatcher.None), Film, Audio("eng")));
    }
}
