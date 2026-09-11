using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Tests the logic that matches a library series to its entry on animefillerlist.com, 
/// which is complicated by the site's inconsistent use of alternate names and season markers.
/// </summary>
public class AnimeTitleMatcherTests
{
    private static readonly AnimeFillerShow AttackOnTitan = new()
    {
        Slug = "attack-titan",
        Title = "Attack on Titan (Shingeki no Kyojin)"
    };

    private static readonly AnimeFillerShow Naruto = new() { Slug = "naruto", Title = "Naruto" };

    private static readonly AnimeFillerShow NarutoShippuden = new()
    {
        Slug = "naruto-shippuden",
        Title = "Naruto Shippuden"
    };

    private static Dictionary<string, AnimeFillerShow> Index() =>
        AnimeTitleMatcher.BuildIndex(new[] { AttackOnTitan, Naruto, NarutoShippuden });

    [Theory]
    [InlineData("Attack on Titan")]
    [InlineData("attack on titan")]
    [InlineData("Shingeki no Kyojin")]
    [InlineData("Attack on Titan (2013)")]
    [InlineData("Attack on Titan Season 3")]
    [InlineData("Attack on Titan Part 2")]
    public void Match_FindsTheShowUnderEverySpellingALibraryUses(string libraryName)
    {
        Assert.Same(AttackOnTitan, AnimeTitleMatcher.Match(Index(), libraryName));
    }

    [Fact]
    public void Match_KeepsSeparateEntriesApart()
    {
        // Naruto and Shippuden are different shows with different filler lists, and the
        // season-suffix stripping must not collapse one onto the other.
        Assert.Same(Naruto, AnimeTitleMatcher.Match(Index(), "Naruto"));
        Assert.Same(NarutoShippuden, AnimeTitleMatcher.Match(Index(), "Naruto Shippuden"));
    }

    [Fact]
    public void Match_ReturnsNullForUnrelatedTitles()
    {
        Assert.Null(AnimeTitleMatcher.Match(Index(), "Breaking Bad"));
        Assert.Null(AnimeTitleMatcher.Match(Index(), string.Empty));
        Assert.Null(AnimeTitleMatcher.Match(Index(), (string?)null));
    }

    [Fact]
    public void Match_FallsBackToTheOriginalTitle()
    {
        Assert.Same(
            AttackOnTitan,
            AnimeTitleMatcher.Match(Index(), "Something Localised", "Shingeki no Kyojin"));
    }

    [Theory]
    [InlineData("The Promised Neverland", "promised neverland")]
    [InlineData("Fruits Basket", "fruits basket")]
    [InlineData("Re:ZERO -Starting Life in Another World-", "re zero starting life in another world")]
    [InlineData("Kaguya-sama: Love Is War", "kaguya sama love is war")]
    [InlineData("JoJo's Bizarre Adventure", "jojo s bizarre adventure")]
    [InlineData("Tokyo Ghoul √A", "tokyo ghoul a")]
    [InlineData("Fullmetal Alchemist & Brotherhood", "fullmetal alchemist and brotherhood")]
    public void Normalize_FoldsPunctuationArticlesAndDiacritics(string input, string expected)
    {
        Assert.Equal(expected, AnimeTitleMatcher.Normalize(input));
    }

    [Fact]
    public void Variants_TreatsParenthesesAsAlternateNamesButNotYears()
    {
        var variants = AnimeTitleMatcher.Variants("Dororo (2019)").ToList();

        Assert.Contains("dororo", variants);
        Assert.DoesNotContain("2019", variants);
    }

    [Fact]
    public void BuildIndex_LetsAShowsOwnNameBeatAnotherShowsAlternateName()
    {
        // Verbatim from the site's index, and in its order: the fan re-edit sorts first and
        // lists the real show as its alternate name. Claiming keys in one pass hands
        // "one piece" to One Pace.
        var onePace = new AnimeFillerShow { Slug = "one-pace", Title = "One Pace (One Piece)" };
        var onePiece = new AnimeFillerShow { Slug = "one-piece", Title = "One Piece" };

        var index = AnimeTitleMatcher.BuildIndex(new[] { onePace, onePiece });

        Assert.Same(onePiece, AnimeTitleMatcher.Match(index, "One Piece"));
        Assert.Same(onePace, AnimeTitleMatcher.Match(index, "One Pace"));
    }

    [Fact]
    public void Match_TellsTwoAdaptationsApartByTheirYear()
    {
        // Also verbatim: the site writes both with U+00D7, and the 1999 entry has a
        // trailing space. Only the year separates them.
        var original = new AnimeFillerShow { Slug = "hunter-x-hunter-1999", Title = "Hunter × Hunter " };
        var remake = new AnimeFillerShow { Slug = "hunter-x-hunter", Title = "Hunter × Hunter (2011)" };

        var index = AnimeTitleMatcher.BuildIndex(new[] { original, remake });

        Assert.Same(remake, AnimeTitleMatcher.Match(index, "Hunter x Hunter (2011)"));
        Assert.Same(original, AnimeTitleMatcher.Match(index, "Hunter x Hunter"));
    }

    [Fact]
    public void Normalize_ReadsTheMultiplicationSignAsAnX()
    {
        // Dropping it as punctuation would fold the title to "hunter hunter" and match nothing.
        Assert.Equal("hunter x hunter", AnimeTitleMatcher.Normalize("Hunter × Hunter"));
    }

    [Fact]
    public void BuildIndex_LetsTheFirstShowKeepAContestedKey()
    {
        // The site's own index has a row whose slug belongs to an unrelated show, so a
        // later collision must not overwrite an already-good mapping.
        var first = new AnimeFillerShow { Slug = "real-show", Title = "Same Name" };
        var second = new AnimeFillerShow { Slug = "wrong-show", Title = "Same Name" };

        var index = AnimeTitleMatcher.BuildIndex(new[] { first, second });

        Assert.Same(first, AnimeTitleMatcher.Match(index, "Same Name"));
    }
}
