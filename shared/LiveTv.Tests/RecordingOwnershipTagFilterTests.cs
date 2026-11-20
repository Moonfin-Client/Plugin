using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Moonfin.Recommendations;
using Moonfin.SmartSearch;
using Xunit;

namespace Moonfin.LiveTv.Tests;

/// <summary>
/// "Recorded by name" tags say who recorded an item, not what it is, so the similar-items scoring
/// and the smart-search index both servers build leave them out.
/// </summary>
public sealed class RecordingOwnershipTagFilterTests
{
    private static readonly IReadOnlyList<string> NoTitles = new string[0];

    private sealed class Film
    {
        public Film(string name, DateTime premiere, params string[] tags)
        {
            Name = name;
            Premiere = premiere;
            Tags = tags;
        }

        public string Name { get; }

        public DateTime Premiere { get; }

        public string[] Tags { get; }

        public SimilarItemFacts Facts => new(Name, new[] { "Drama" }, Tags, null, 2000, null);
    }

    [Fact]
    public void OwnerTags_AreIgnoredAsSimilaritySeeds()
    {
        var facts = new SimilarItemFacts("Seed", new[] { "Drama" }, new[] { "Recorded by Jordan", "island", "recorded by alex" }, null, 2000, null);
        var seed = new SimilarSeed(facts, Enumerable.Empty<(string?, CreditRole)>());

        Assert.Equal(new[] { "island" }, facts.Tags);
        Assert.Equal(new[] { "island" }, seed.Tags);
        Assert.Null(new SimilarItemFacts("Seed", null, null, null, null, null).Tags);
    }

    [Fact]
    public void OwnerTags_EarnNoSimilarityScore()
    {
        var seed = new SimilarSeed(new Film("Seed", DateTime.MinValue, "Recorded by Jordan").Facts, Enumerable.Empty<(string?, CreditRole)>());

        // Equal but for the shared owner tag; the newer premiere wins a tie.
        var sameOwner = new Film("Same owner", new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Recorded by Jordan");
        var other = new Film("Other", new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var ranked = SimilarScoring.Rank(
            new[] { sameOwner, other },
            seed,
            10,
            f => f.Facts,
            f => f.Premiere,
            _ => _ => null,
            CancellationToken.None);

        Assert.Equal(new[] { "Other", "Same owner" }, ranked.Select(f => f.Name));
    }

    [Fact]
    public void SmartSearch_DoesNotIndexOwnerTags()
    {
        var library = new SmartLibrary();
        library.AddTitle("1", false, "Cast Away", 2000, null, null, new[] { "Drama" }, new[] { "island", "Recorded by Jordan" }, new string[0], new string[0], null);

        var built = library.Build((_, _) => NoTitles);

        var text = built.Single(b => b.Entry.Key == "1").Text;
        Assert.Equal("Cast Away (2000). Movie. Drama. island.", text);
        Assert.DoesNotContain("Jordan", text);
    }

    [Fact]
    public void IsOwnerTag_MatchesThePrefixOnly()
    {
        Assert.True(RecordingOwnershipService.IsOwnerTag("Recorded by Jordan"));
        Assert.True(RecordingOwnershipService.IsOwnerTag("recorded BY jordan"));
        Assert.False(RecordingOwnershipService.IsOwnerTag("Recorded live"));
        Assert.False(RecordingOwnershipService.IsOwnerTag(null));
    }
}
