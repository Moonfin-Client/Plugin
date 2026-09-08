using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Covers the scoring and candidate selection in <see cref="MoonfinSimilarItemsService"/>. The
/// service reaches the library through two ILibraryManager members only, so a fixture list plus
/// a people lookup is enough to drive the whole pipeline.
/// </summary>
public class SimilarItemsScoringTests
{
    private static Movie MakeMovie(
        string name,
        string[]? genres = null,
        string[]? tags = null,
        string[]? studios = null,
        int? year = null,
        float? rating = null)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Genres = genres ?? [],
            Tags = tags ?? [],
            Studios = studios ?? [],
            ProductionYear = year,
            CommunityRating = rating
        };
    }

    private static (MoonfinSimilarItemsService Service, List<InternalItemsQuery> Queries) Build(
        IEnumerable<BaseItem> catalog,
        Func<BaseItem, List<PersonInfo>>? people = null)
    {
        var items = catalog.ToList();
        var queries = new List<InternalItemsQuery>();
        var library = new FakeLibraryManager
        {
            ItemsResultHandler = q =>
            {
                queries.Add(q);
                return items;
            },
            PeopleHandler = people
        };

        return (new MoonfinSimilarItemsService(library, NullLogger<MoonfinSimilarItemsService>.Instance), queries);
    }

    private static Task<IReadOnlyList<BaseItem>> Run(
        MoonfinSimilarItemsService service,
        BaseItem seed,
        int? limit = null,
        IReadOnlyList<Guid>? exclude = null)
    {
        return service.GetSimilarItemsAsync(seed, null, limit, exclude, CancellationToken.None);
    }

    [Fact]
    public async Task SharedGenresOutrankUnrelatedItems()
    {
        var seed = MakeMovie("Seed", genres: ["Science Fiction", "Adventure"], year: 2020);
        var match = MakeMovie("Match", genres: ["Science Fiction", "Adventure"], year: 2020);
        var partial = MakeMovie("Partial", genres: ["Adventure"], year: 2020);
        var unrelated = MakeMovie("Unrelated", genres: ["Romance"], year: 2020);

        var (service, _) = Build([match, partial, unrelated]);
        var results = await Run(service, seed);

        Assert.Equal(["Match", "Partial", "Unrelated"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task GenreScoreIsCappedSoOneSpamEntryCannotDominate()
    {
        var shared = Enumerable.Range(0, 12).Select(i => $"Genre{i}").ToArray();
        var seed = MakeMovie("Seed", genres: shared, year: 2000);

        // Seven shared genres already reaches the cap, so the twelve genre entry can only match
        // it, never beat it. The newer premiere date is what breaks the tie.
        var capped = MakeMovie("Capped", genres: shared.Take(7).ToArray(), year: 2000);
        capped.PremiereDate = new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var spammer = MakeMovie("Spammer", genres: shared, year: 2000);
        spammer.PremiereDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var (service, _) = Build([capped, spammer]);
        var results = await Run(service, seed);

        Assert.Equal("Capped", results[0].Name);
    }

    [Fact]
    public async Task CloserProductionYearsRankHigher()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], year: 2010);
        var near = MakeMovie("Near", genres: ["Drama"], year: 2011);
        var far = MakeMovie("Far", genres: ["Drama"], year: 1989);

        var (service, _) = Build([far, near]);
        var results = await Run(service, seed);

        Assert.Equal("Near", results[0].Name);
    }

    [Fact]
    public async Task SequelTitlesGetABonus()
    {
        var seed = MakeMovie("Blade Runner", genres: ["Science Fiction"], year: 1982);
        var sequel = MakeMovie("Blade Runner 2049", genres: ["Science Fiction"], year: 1982);
        var sibling = MakeMovie("Total Recall", genres: ["Science Fiction"], year: 1982);

        var (service, _) = Build([sibling, sequel]);
        var results = await Run(service, seed);

        Assert.Equal("Blade Runner 2049", results[0].Name);
    }

    [Fact]
    public async Task StopWordsAndShortWordsDoNotEarnATitleBonus()
    {
        var seed = MakeMovie("The Godfather", genres: ["Crime"], year: 1972);
        var sequel = MakeMovie("Godfather Part II", genres: ["Crime"], year: 1972);
        var sharesOnlyAStopWord = MakeMovie("The Notebook", genres: ["Crime"], year: 1972);

        var (service, _) = Build([sharesOnlyAStopWord, sequel]);
        var results = await Run(service, seed);

        Assert.Equal(["Godfather Part II", "The Notebook"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task ATitlelessSeedDoesNotGiveEveryCandidateTheBonus()
    {
        // "the" is a stop word and "fly" is under the four character floor, so this seed has no
        // keywords. An empty keyword set is a subset of everything, so without a guard every
        // candidate would collect the title bonus and the ordering would collapse.
        var seed = MakeMovie("The Fly", genres: ["Horror"], year: 1986);
        var closerYear = MakeMovie("The Thing", genres: ["Horror"], year: 1986);
        var fartherYear = MakeMovie("Videodrome", genres: ["Horror"], year: 1976);

        var (service, _) = Build([fartherYear, closerYear]);
        var results = await Run(service, seed);

        Assert.Equal(["The Thing", "Videodrome"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task SharedStudiosAndCloseRatingsRankHigher()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], studios: ["A24"], year: 2015, rating: 8.0f);
        var sameStudio = MakeMovie("SameStudio", genres: ["Drama"], studios: ["A24"], year: 2015, rating: 8.1f);
        var closeRating = MakeMovie("CloseRating", genres: ["Drama"], studios: ["Other"], year: 2015, rating: 8.1f);
        var farRating = MakeMovie("FarRating", genres: ["Drama"], studios: ["Other"], year: 2015, rating: 2.0f);

        var (service, _) = Build([farRating, closeRating, sameStudio]);
        var results = await Run(service, seed);

        Assert.Equal(["SameStudio", "CloseRating", "FarRating"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task SharedCastAndCrewRaiseTheRanking()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], year: 2015);
        var withDirector = MakeMovie("WithDirector", genres: ["Drama"], year: 2015);
        var withoutDirector = MakeMovie("WithoutDirector", genres: ["Drama"], year: 2015);

        var people = new Dictionary<Guid, List<PersonInfo>>
        {
            [seed.Id] = [new PersonInfo { Name = "Denis Villeneuve", Type = PersonKind.Director }],
            [withDirector.Id] = [new PersonInfo { Name = "Denis Villeneuve", Type = PersonKind.Director }],
            [withoutDirector.Id] = [new PersonInfo { Name = "Someone Else", Type = PersonKind.Director }]
        };

        var (service, _) = Build(
            [withoutDirector, withDirector],
            item => people.TryGetValue(item.Id, out var list) ? list : []);

        var results = await Run(service, seed);

        Assert.Equal("WithDirector", results[0].Name);
    }

    [Fact]
    public async Task SeedAndExcludedItemsAreNeverReturned()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], year: 2015);
        var excluded = MakeMovie("Excluded", genres: ["Drama"], year: 2015);
        var kept = MakeMovie("Kept", genres: ["Drama"], year: 2015);

        var (service, _) = Build([seed, excluded, kept]);
        var results = await Run(service, seed, exclude: [excluded.Id]);

        Assert.Equal(["Kept"], results.Select(r => r.Name));
    }

    [Fact]
    public async Task CandidateQueriesAreBoundedAndOrdered()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], tags: ["gritty"], year: 2015);
        var (service, queries) = Build([MakeMovie("Other", genres: ["Drama"], year: 2015)]);

        await Run(service, seed);

        Assert.NotEmpty(queries);
        Assert.All(queries, q =>
        {
            Assert.True(q.Limit is > 0 and <= 400, $"Query limit was {q.Limit}.");
            Assert.NotNull(q.OrderBy);
            Assert.NotEmpty(q.OrderBy);
            Assert.True(q.Recursive);
            Assert.False(q.EnableTotalRecordCount);
        });
    }

    [Fact]
    public async Task LimitIsHonouredAndClamped()
    {
        var seed = MakeMovie("Seed", genres: ["Drama"], year: 2015);
        var catalog = Enumerable.Range(0, 50)
            .Select(i => MakeMovie($"Item{i}", genres: ["Drama"], year: 2015))
            .Cast<BaseItem>()
            .ToList();

        var (service, _) = Build(catalog);

        Assert.Equal(5, (await Run(service, seed, limit: 5)).Count);
        Assert.Equal(20, (await Run(service, seed)).Count);
        Assert.Equal(50, (await Run(service, seed, limit: 10_000)).Count);
    }

    [Fact]
    public async Task UnsupportedItemTypesReturnNothingWithoutQueryingTheLibrary()
    {
        var seed = new Episode { Id = Guid.NewGuid(), Name = "Pilot" };
        var (service, queries) = Build([MakeMovie("Other", genres: ["Drama"])]);

        var results = await Run(service, seed);

        Assert.Empty(results);
        Assert.Empty(queries);
    }

    [Fact]
    public async Task SeriesSeedsQueryForSeries()
    {
        var seed = new Series { Id = Guid.NewGuid(), Name = "Seed Show", Genres = ["Drama"], ProductionYear = 2015 };
        var candidate = new Series { Id = Guid.NewGuid(), Name = "Other Show", Genres = ["Drama"], ProductionYear = 2015 };

        var (service, queries) = Build([candidate]);
        var results = await Run(service, seed);

        Assert.Equal(["Other Show"], results.Select(r => r.Name));
        Assert.All(queries, q => Assert.Equal([BaseItemKind.Series], q.IncludeItemTypes));
    }
}
