using System.Collections.Generic;
using System.Linq;
using Moonfin.PosterBadges;
using Xunit;

namespace Moonfin.PosterBadges.Tests;

/// <summary>
/// The age rating label and the "Rating" condition, on the values Jellyfin, Emby, Plex and
/// NFO files store.
/// </summary>
public class PosterBadgeRatingTests
{
    private static PosterBadgeRule RatingRule(string? country = null, string? icon = null) =>
        new PosterBadgeRule { Preset = PosterBadgeRating.PresetId, Label = "Age rating", RatingCountry = country, Icon = icon };

    private static PosterBadge? Badge(PosterBadgeRule rule, string? rating, string? path = "/films/a.mkv") =>
        PosterBadgeMatcher.Match(new[] { rule }, path, path == null ? null : new List<object>(), rating: rating).SingleOrDefault();

    [Theory]
    [InlineData("FR-12", "FR-12")]
    [InlineData("  PG-13 ", "PG-13")]
    [InlineData("Rated R", "R")]
    [InlineData("MA 15+", "MA 15+")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("NR", null)]
    [InlineData("FR-NR", null)]
    [InlineData("N/A", null)]
    [InlineData("Not Rated", null)]
    [InlineData("Unrated", null)]
    public void TheStoredRatingIsShownAsItIs(string? stored, string? shown)
    {
        Assert.Equal(shown, Badge(RatingRule(), stored)?.Label);
    }

    [Theory]
    [InlineData("FR-12", "fr", "12")]
    [InlineData("gb/15", "gb", "15")]
    [InlineData("FSK-16", "de", "16")]
    [InlineData("PG-13", null, "PG-13")]
    [InlineData("NC-17", null, "NC-17")]
    [InlineData("TV-MA", null, "TV-MA")]
    [InlineData("R", null, "R")]
    [InlineData("16", null, "16")]
    public void OnlyAPrefixNamesTheCountry(string rating, string? country, string rest)
    {
        Assert.Equal((country, rest), PosterBadgeRating.Split(rating));
    }

    [Fact]
    public void TheCountryCanBeHiddenOrTurnedIntoItsFlag()
    {
        Assert.Equal("12", Badge(RatingRule("hide"), "FR-12")!.Label);

        var flagged = Badge(RatingRule("flag", "person"), "FR-12")!;
        Assert.Equal(("12", "flag-fr"), (flagged.Label, flagged.Icon));

        // No country to show, so the rating stays whole with the rule's own icon.
        var bare = Badge(RatingRule("flag", "person"), "PG-13")!;
        Assert.Equal(("PG-13", "person"), (bare.Label, bare.Icon));
    }

    [Fact]
    public void ASeriesGetsItsRatingButNoFileLabel()
    {
        var rules = new List<PosterBadgeRule>
        {
            RatingRule(),
            new PosterBadgeRule { Preset = "remux", Label = "REMUX" },
            new PosterBadgeRule
            {
                Label = "No REMUX",
                Conditions = { new PosterBadgeCondition { Field = "Path", Match = PosterBadgeMatcher.None, Operator = PosterBadgeMatcher.Contains, Value = "REMUX" } },
            },
        };

        var badges = PosterBadgeMatcher.Match(rules, null, null, rating: "TV-MA");

        Assert.Equal(new[] { "TV-MA" }, badges.Select(b => b.Label));
    }

    [Theory]
    [InlineData("FR-TP", true)]
    [InlineData("G", true)]
    [InlineData("FR-12", false)]
    [InlineData(null, false)]
    public void ARuleCanGroupRatings(string? rating, bool kids)
    {
        var rule = new PosterBadgeRule
        {
            Label = "Kids",
            Conditions = { new PosterBadgeCondition { Field = "Rating", Operator = PosterBadgeMatcher.OneOf, Value = "FR-TP, FR-U, G, PG" } },
        };

        Assert.Equal(kids, Badge(rule, rating) != null);
    }

    private static readonly List<string> Bands = new() { "#000000", "#000007", "#000012", "#000016", "#000018" };

    [Theory]
    [InlineData("G", 0, "#000000")]
    [InlineData("PG-13", null, "#000012")]
    [InlineData("TV-Y7", null, "#000007")]
    [InlineData("MA 15+", null, "#000012")]
    [InlineData("FR-16", null, "#000016")]
    [InlineData("R", 17, "#000016")]
    [InlineData("NC-17", 1000, "#000018")]
    [InlineData("R", null, "#4B5563")]
    public void TheColourFollowsTheAge(string rating, int? serverAge, string color)
    {
        var rule = RatingRule();
        rule.Color = "#4B5563";
        rule.AgeColors = Bands;

        // The server's age wins; without it, the number in the text; without either, the one colour.
        var badge = PosterBadgeMatcher.Match(new[] { rule }, null, null, rating: rating, age: serverAge).Single();

        Assert.Equal(color, badge.Color);
    }

    [Theory]
    [InlineData("FSK-0", "keep", "family_restroom")]
    [InlineData("PG-13", "keep", "person")]
    [InlineData("TV-MA", "keep", "person")]
    [InlineData("FR-18", "keep", "18_up_rating")]
    [InlineData("FR-18", "flag", "flag-fr")]
    [InlineData("R", "keep", "person")]
    public void TheIconFollowsTheAgeUnlessAFlagShows(string rating, string country, string icon)
    {
        // An empty band, a made-up name and an unknown age all keep the label's own icon.
        var rule = RatingRule(country, "person");
        rule.AgeColors = Bands;
        rule.AgeIcons = new List<string> { "family_restroom", "", "made_up", "warning", "18_up_rating" };

        Assert.Equal(icon, PosterBadgeMatcher.Match(new[] { rule }, null, null, rating: rating).Single().Icon);
    }

    [Fact]
    public void WithoutColoursByAgeTheLabelKeepsItsColour()
    {
        var rule = RatingRule();
        rule.Color = "#4B5563";

        Assert.Equal("#4B5563", PosterBadgeMatcher.Match(new[] { rule }, null, null, rating: "PG-13").Single().Color);
    }

    [Fact]
    public void TheSameTextShowsOnce()
    {
        var custom = new PosterBadgeRule
        {
            Label = "PG-13",
            Conditions = { new PosterBadgeCondition { Field = "Rating", Operator = PosterBadgeMatcher.OneOf, Value = "PG-13" } },
        };

        Assert.Single(PosterBadgeMatcher.Match(new[] { RatingRule(), custom }, null, null, rating: "PG-13"));
        Assert.Single(PosterBadgeMatcher.Match(new[] { custom, RatingRule() }, null, null, rating: "PG-13"));

        // The Test tool says the same.
        string[] Outcomes(params PosterBadgeRule[] rules) =>
            PosterBadgeTest.Explain(rules, null, null, "PG-13").Select(r => r.Outcome).ToArray();
        Assert.Equal(new[] { "shown", "sameText" }, Outcomes(RatingRule(), custom));
        Assert.Equal(new[] { "shown", "sameText" }, Outcomes(custom, RatingRule()));

        custom.Label = "Teens";
        Assert.Equal(new[] { "shown", "shown" }, Outcomes(RatingRule(), custom));
    }

    [Fact]
    public void AnUnratedItemPassesAnUnless()
    {
        var rule = new PosterBadgeRule
        {
            Label = "Not 18",
            Conditions = { new PosterBadgeCondition { Field = "Rating", Match = PosterBadgeMatcher.None, Operator = PosterBadgeMatcher.Contains, Value = "18" } },
        };

        Assert.NotNull(Badge(rule, null));
        Assert.Null(Badge(rule, "FR-18"));
    }
}
