using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class LocalItemIdServiceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-local-ids-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibraryManager _library = new();
    private readonly List<InternalItemsQuery> _queries = new();
    private readonly List<BaseItem> _libraryItems = new();
    private readonly CustomRowCacheService _customRows;
    private readonly ImdbListsCacheService _imdb;
    private readonly LocalItemIdService _service;

    public LocalItemIdServiceTests()
    {
        Directory.CreateDirectory(_dataPath);
        _library.ItemsResultHandler = query =>
        {
            _queries.Add(query);
            var kind = query.IncludeItemTypes.Single();
            return _libraryItems.Where(item => (kind == BaseItemKind.Series) == item is Series).ToList();
        };
        _library.ItemByIdHandler = id => _libraryItems.FirstOrDefault(item => item.Id == id);
        _customRows = new CustomRowCacheService(NullLogger<CustomRowCacheService>.Instance, _dataPath);
        _imdb = new ImdbListsCacheService(NullLogger<ImdbListsCacheService>.Instance, _dataPath);
        _service = new LocalItemIdService(_library, _customRows, _imdb, new NoServices(), NullLogger<LocalItemIdService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ResolvePicksTheExactMatchOverALooseCandidate()
    {
        // Some server versions match a provider id as a substring, so 6030 comes back for 603.
        var loose = AddMovie(tmdb: "6030");
        var exact = AddMovie(tmdb: "603");
        var item = ListMovie(tmdb: "603");

        _service.Resolve([item]);

        Assert.Equal(exact.Id.ToString("N"), item.LocalItemId);
        Assert.NotEqual(loose.Id.ToString("N"), item.LocalItemId);
    }

    [Fact]
    public void ResolvePrefersTheTmdbMatchOverAnImdbOne()
    {
        AddMovie(tmdb: "1", imdb: "tt0133093");
        var tmdbMatch = AddMovie(tmdb: "603", imdb: "tt9999999");
        var item = ListMovie(tmdb: "603", imdb: "tt0133093");

        _service.Resolve([item]);

        Assert.Equal(tmdbMatch.Id.ToString("N"), item.LocalItemId);
    }

    [Fact]
    public void ResolveAsksForTheEntrysKindAndAllItsIds()
    {
        var show = AddSeries(tmdb: "1399", tvdb: "121361");
        var item = new CustomRowItem
        {
            Type = "Series",
            ProviderIds = new CustomRowItemProviderIds { Tmdb = "1399", Imdb = "tt0944947", Tvdb = "121361" }
        };

        _service.Resolve([item]);

        var query = Assert.Single(_queries);
        Assert.Equal([BaseItemKind.Series], query.IncludeItemTypes);
        Assert.False(query.IsVirtualItem);
        Assert.Equal("1399", query.HasAnyProviderId["Tmdb"]);
        Assert.Equal("tt0944947", query.HasAnyProviderId["Imdb"]);
        Assert.Equal("121361", query.HasAnyProviderId["Tvdb"]);
        Assert.Equal(show.Id.ToString("N"), item.LocalItemId);
    }

    [Fact]
    public void ResolveNeverMatchesAMovieToASeriesWithTheSameTmdbNumber()
    {
        AddSeries(tmdb: "1399");
        var item = ListMovie(tmdb: "1399");

        _service.Resolve([item]);

        Assert.Null(item.LocalItemId);
    }

    [Fact]
    public void ResolveClearsAMatchTheLibraryNoLongerHas()
    {
        var item = ListMovie(tmdb: "603");
        item.LocalItemId = Guid.NewGuid().ToString("N");

        _service.Resolve([item]);

        Assert.Null(item.LocalItemId);
    }

    [Fact]
    public void ResolveLooksUpATitleOnceAndSkipsEntriesWithoutIds()
    {
        var movie = AddMovie(tmdb: "603");
        var first = ListMovie(tmdb: "603");
        var second = ListMovie(tmdb: "603");
        var blank = new CustomRowItem { Type = "Movie" };

        _service.Resolve([first, second, blank]);

        Assert.Single(_queries);
        Assert.Equal(movie.Id.ToString("N"), first.LocalItemId);
        Assert.Equal(movie.Id.ToString("N"), second.LocalItemId);
        Assert.Null(blank.LocalItemId);
    }

    [Fact]
    public void WithoutTheOptInTheResponseCarriesNoIdsAndTheCacheKeepsThem()
    {
        var localId = Guid.NewGuid().ToString("N");
        var cached = ListMovie(tmdb: "603");
        cached.LocalItemId = localId;
        var items = new List<CustomRowItem> { cached, ListMovie(tmdb: "604") };

        var response = _service.ForResponse(items, Guid.NewGuid(), includeLocalItemIds: false);

        Assert.All(response, item => Assert.Null(item.LocalItemId));
        Assert.Equal(localId, cached.LocalItemId);
        Assert.Equal("603", response[0].ProviderIds.Tmdb);
        Assert.DoesNotContain("localItemId", Serialize(response));
    }

    [Fact]
    public void ARowWithoutMatchesIsReturnedAsItIs()
    {
        var items = new List<CustomRowItem> { ListMovie(tmdb: "603") };

        Assert.Same(items, _service.ForResponse(items, Guid.NewGuid(), includeLocalItemIds: false));
        Assert.Same(items, _service.ForResponse(items, Guid.NewGuid(), includeLocalItemIds: true));
    }

    [Fact]
    public void AUserThatCantBeResolvedSeesNoMatches()
    {
        var movie = AddMovie(tmdb: "603");
        var cached = ListMovie(tmdb: "603");
        cached.LocalItemId = movie.Id.ToString("N");

        var response = _service.ForResponse([cached], Guid.NewGuid(), includeLocalItemIds: true);

        Assert.Null(Assert.Single(response).LocalItemId);
        Assert.Equal(movie.Id.ToString("N"), cached.LocalItemId);
    }

    [Fact]
    public void TheIdIsOnTheWireOnlyWhenSet()
    {
        var item = ListMovie(tmdb: "603");
        Assert.DoesNotContain("localItemId", Serialize([item]));

        item.LocalItemId = "abc";
        Assert.Contains("\"localItemId\":\"abc\"", Serialize([item]));
    }

    [Fact]
    public void AnArrivalFillsTheCachedEntriesThatLackIt()
    {
        var unmatched = ListMovie(tmdb: "603");
        var otherKind = new CustomRowItem { Type = "Series", ProviderIds = new CustomRowItemProviderIds { Tmdb = "603" } };
        var stillThere = AddMovie(tmdb: "999", imdb: "tt0133093");
        var matchedElsewhere = ListMovie(imdb: "tt0133093");
        matchedElsewhere.LocalItemId = stillThere.Id.ToString("N");
        var gone = ListMovie(imdb: "tt0133093");
        gone.LocalItemId = Guid.NewGuid().ToString("N");
        _customRows.Set("row", [unmatched, otherKind, matchedElsewhere]);
        _imdb.SetItems("imdb_top_250_movies", [gone]);

        var arrival = AddMovie(tmdb: "603", imdb: "tt0133093");
        var changed = _service.MatchArrival(arrival);

        Assert.Equal(2, changed);
        Assert.Equal(arrival.Id.ToString("N"), unmatched.LocalItemId);
        Assert.Equal(arrival.Id.ToString("N"), gone.LocalItemId);
        Assert.Equal(stillThere.Id.ToString("N"), matchedElsewhere.LocalItemId);
        Assert.Null(otherKind.LocalItemId);
    }

    [Fact]
    public void ArrivalsOtherThanMoviesAndSeriesAreIgnored()
    {
        _customRows.Set("row", [ListMovie(tmdb: "603")]);
        var episode = new Episode { Id = Guid.NewGuid() };
        episode.SetProviderId(MetadataProvider.Tmdb, "603");

        Assert.Equal(0, _service.MatchArrival(episode));
    }

    [Fact]
    public async Task ResolvingTheCachesMatchesEveryCachedList()
    {
        var movie = AddMovie(tmdb: "603");
        var show = AddSeries(imdb: "tt0944947");
        var row = ListMovie(tmdb: "603");
        var chart = new CustomRowItem { Type = "Series", ProviderIds = new CustomRowItemProviderIds { Imdb = "tt0944947" } };
        _customRows.Set("row", [row]);
        _imdb.SetItems("imdb_top_250_tv_shows", [chart]);

        await _service.ResolveCachedAsync(CancellationToken.None);

        Assert.Equal(movie.Id.ToString("N"), row.LocalItemId);
        Assert.Equal(show.Id.ToString("N"), chart.LocalItemId);
        Assert.True(File.Exists(Path.Combine(_dataPath, "custom_rows_cache.json")));
    }

    private Movie AddMovie(string? tmdb = null, string? imdb = null)
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        if (tmdb != null) movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
        if (imdb != null) movie.SetProviderId(MetadataProvider.Imdb, imdb);
        _libraryItems.Add(movie);
        return movie;
    }

    private Series AddSeries(string? tmdb = null, string? imdb = null, string? tvdb = null)
    {
        var series = new Series { Id = Guid.NewGuid() };
        if (tmdb != null) series.SetProviderId(MetadataProvider.Tmdb, tmdb);
        if (imdb != null) series.SetProviderId(MetadataProvider.Imdb, imdb);
        if (tvdb != null) series.SetProviderId(MetadataProvider.Tvdb, tvdb);
        _libraryItems.Add(series);
        return series;
    }

    private static CustomRowItem ListMovie(string? tmdb = null, string? imdb = null) => new()
    {
        Name = "Title",
        Type = "Movie",
        ProviderIds = new CustomRowItemProviderIds { Tmdb = tmdb, Imdb = imdb }
    };

    private static string Serialize(List<CustomRowItem> items) =>
        JsonSerializer.Serialize(new CustomRowResponse { Success = true, Items = items });

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
