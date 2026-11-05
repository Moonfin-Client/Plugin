using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Moonfin.LiveTv.Tests;
using Xunit;

namespace Moonfin.Calendar.Tests;

public class ArrCalendarWindowTests
{
    private static readonly DateTime Today = new DateTime(2026, 11, 5, 15, 30, 0, DateTimeKind.Utc);

    private static (string Start, string End)? Clamp(string? start, string? end, int back = 14, int ahead = 90)
    {
        var window = ArrCalendarWindow.Clamp(start, end, Today, back, ahead);
        return window == null ? null : (window.Value.Start.ToString("yyyy-MM-dd"), window.Value.End.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void MissingDatesAreTodayAndTheFarEdge() => Assert.Equal(("2026-11-05", "2027-02-03"), Clamp(null, ""));

    [Fact]
    public void TheHomeRowsWindowIsUntouched() => Assert.Equal(("2026-11-05", "2027-02-03"), Clamp("2026-11-05", "2027-02-03"));

    [Fact]
    public void AStartTooFarBackIsHeldToDaysBack() => Assert.Equal(("2026-10-22", "2026-11-30"), Clamp("2026-01-01", "2026-11-30"));

    [Fact]
    public void AnEndTooFarAheadIsHeldToDaysAhead() => Assert.Equal(("2026-11-05", "2026-12-05"), Clamp(null, "2028-01-01", ahead: 30));

    [Fact]
    public void ATimestampCountsAsItsDay() => Assert.Equal(("2026-11-01", "2026-11-10"), Clamp("2026-11-01T00:00:00Z", "2026-11-10T23:59:59Z"));

    [Theory]
    [InlineData("2026-01-01", "2026-01-31")]
    [InlineData("2027-06-01", "2027-07-01")]
    [InlineData("2026-11-20", "2026-11-10")]
    public void NothingLeftAfterClampingIsNull(string start, string end) => Assert.Null(Clamp(start, end));

    [Fact]
    public void ANegativeWindowCountsAsZero() => Assert.Equal(("2026-11-05", "2026-11-05"), Clamp("2026-10-01", "2026-12-01", back: -5, ahead: -5));

    [Fact]
    public void SonarrAsksForSeriesAndEpisodeImagesAndMonitoredOnlyByDefault() =>
        Assert.Equal(
            "start=2026-11-05&end=2027-02-03&includeSeries=true&includeEpisodeImages=true",
            ArrCalendarWindow.Query(true, new DateTime(2026, 11, 5), new DateTime(2027, 2, 3), unmonitored: false));

    [Fact]
    public void UnmonitoredReachesBothArrs()
    {
        Assert.EndsWith("&includeEpisodeImages=true&unmonitored=true", ArrCalendarWindow.Query(true, Today, Today, unmonitored: true));
        Assert.Equal("start=2026-11-05&end=2026-11-05&unmonitored=true", ArrCalendarWindow.Query(false, Today, Today, unmonitored: true));
    }

    [Fact]
    public void RadarrGetsNoSonarrFlags() =>
        Assert.Equal("start=2026-11-05&end=2026-11-05", ArrCalendarWindow.Query(false, Today, Today, unmonitored: false));

    [Fact]
    public void OnlyANonAdminIsRefusedWhenTheCalendarIsAdminsOnly()
    {
        Assert.Equal("The calendar is limited to admins on this server", ArrCalendarWindow.Refusal(adminsOnly: true, isAdmin: false));
        Assert.Null(ArrCalendarWindow.Refusal(adminsOnly: true, isAdmin: true));
        Assert.Null(ArrCalendarWindow.Refusal(adminsOnly: false, isAdmin: false));
    }

    [Fact]
    public void TheCacheKeySeparatesServiceWindowAndMonitoring()
    {
        var keys = new[]
        {
            ArrCalendarWindow.CacheKey(true, Today, Today, false),
            ArrCalendarWindow.CacheKey(false, Today, Today, false),
            ArrCalendarWindow.CacheKey(true, Today, Today.AddDays(1), false),
            ArrCalendarWindow.CacheKey(true, Today, Today, true),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }
}

public class ArrCalendarCacheTests
{
    private DateTime _now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private int _fetches;

    private Task<string> Fetch()
    {
        _fetches++;
        return Task.FromResult("answer " + _fetches);
    }

    private Task<string> Get(ArrCalendarCache<string> cache, string key = "k", int minutes = 5) =>
        cache.GetOrFetchAsync(key, minutes, Fetch, value => value.StartsWith("answer", StringComparison.Ordinal));

    [Fact]
    public async Task ARepeatWithinTheMinutesIsServedFromTheCache()
    {
        var cache = new ArrCalendarCache<string>(() => _now);
        await Get(cache);
        _now = _now.AddMinutes(4);

        Assert.Equal("answer 1", await Get(cache));
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task AnExpiredAnswerIsFetchedAgain()
    {
        var cache = new ArrCalendarCache<string>(() => _now);
        await Get(cache);
        _now = _now.AddMinutes(5);

        Assert.Equal("answer 2", await Get(cache));
    }

    [Fact]
    public async Task ZeroMinutesTurnsTheCacheOff()
    {
        var cache = new ArrCalendarCache<string>(() => _now);
        await Get(cache, minutes: 0);
        await Get(cache, minutes: 0);

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task AnAnswerKeepRejectsIsNotStored()
    {
        var cache = new ArrCalendarCache<string>(() => _now);
        await cache.GetOrFetchAsync("k", 5, () => { _fetches++; return Task.FromResult("502"); }, value => value != "502");
        await Get(cache);

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task EachKeyHasItsOwnAnswer()
    {
        var cache = new ArrCalendarCache<string>(() => _now);
        await Get(cache, "sonarr");

        Assert.Equal("answer 2", await Get(cache, "radarr"));
        Assert.Equal("answer 1", await Get(cache, "sonarr"));
    }
}

public class ArrCalendarAnnotatorTests
{
    private static readonly CalendarLibraryFolder Tv = new CalendarLibraryFolder("tv", new[] { "/media/tv" }, visible: true);
    private static readonly CalendarLibraryFolder Anime = new CalendarLibraryFolder("anime", new[] { "/media/anime" }, visible: false);
    private static readonly CalendarLibraryFolder Kids = new CalendarLibraryFolder("kids", new[] { @"D:\Kids\" }, visible: true);
    private static readonly CalendarLibraryFolder Movies = new CalendarLibraryFolder("movies", new[] { "/media/movies" }, visible: true);

    private static string Episode(int tvdb, int season, int episode, string path = "/tv/Show", int tmdb = 0, string imdb = "") =>
        JsonSerializer.Serialize(new
        {
            seriesId = 7,
            seasonNumber = season,
            episodeNumber = episode,
            title = "Pilot",
            series = new { title = "Show", tvdbId = tvdb, tmdbId = tmdb, imdbId = imdb, path },
        });

    private static List<JsonObject> Annotate(bool isSonarr, FakeLibrary library, params string[] elements)
    {
        var body = Encoding.UTF8.GetBytes("[" + string.Join(",", elements) + "]");
        var array = (JsonArray)JsonNode.Parse(ArrCalendarAnnotator.Annotate(body, isSonarr, library))!;
        return array.Select(n => (JsonObject)n!).ToList();
    }

    private static string? Str(JsonObject element, string name) => element[name]?.GetValue<string>();

    private static string[] Libraries(JsonObject element) =>
        ((JsonArray)element["moonfinLibraryIds"]!).Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void AnEpisodeInTheLibraryGetsItsSeriesEpisodeAndLibrary()
    {
        var library = new FakeLibrary(Tv)
            .Series("s1", new CalendarProviderIds("100", null, null), "tv")
            .Episode("e1", "s1", 1, 1)
            .Episode("e2", "s1", 1, 2);

        var element = Annotate(true, library, Episode(100, 1, 2)).Single();

        Assert.Equal("s1", Str(element, "moonfinItemId"));
        Assert.Equal("e2", Str(element, "moonfinEpisodeId"));
        Assert.Equal(new[] { "tv" }, Libraries(element));
        Assert.Equal("Pilot", Str(element, "title"));
    }

    [Fact]
    public void AMissingEpisodeKeepsTheSeriesAndANullEpisodeId()
    {
        var library = new FakeLibrary(Tv).Series("s1", new CalendarProviderIds("100", null, null), "tv");

        var element = Annotate(true, library, Episode(100, 2, 1)).Single();

        Assert.Equal("s1", Str(element, "moonfinItemId"));
        Assert.True(element.ContainsKey("moonfinEpisodeId"));
        Assert.Null(element["moonfinEpisodeId"]);
    }

    [Fact]
    public void AFileHoldingTwoEpisodesAnswersForBoth()
    {
        var library = new FakeLibrary(Tv)
            .Series("s1", new CalendarProviderIds("100", null, null), "tv")
            .Episode("e1", "s1", 1, 1, episodeEnd: 2);

        Assert.Equal("e1", Str(Annotate(true, library, Episode(100, 1, 2)).Single(), "moonfinEpisodeId"));
    }

    [Fact]
    public void TvdbWinsOverTmdbAndTmdbOverImdb()
    {
        var library = new FakeLibrary(Tv)
            .Series("byTmdb", new CalendarProviderIds(null, "200", null), "tv")
            .Series("byTvdb", new CalendarProviderIds("100", null, null), "tv")
            .Series("byImdb", new CalendarProviderIds(null, null, "tt300"), "tv");

        Assert.Equal("byTvdb", Str(Annotate(true, library, Episode(100, 1, 1, tmdb: 200, imdb: "tt300")).Single(), "moonfinItemId"));
        Assert.Equal("byTmdb", Str(Annotate(true, library, Episode(999, 1, 1, tmdb: 200, imdb: "tt300")).Single(), "moonfinItemId"));
        Assert.Equal("byImdb", Str(Annotate(true, library, Episode(999, 1, 1, tmdb: 999, imdb: "TT300")).Single(), "moonfinItemId"));
    }

    [Fact]
    public void ATitleNotInTheLibraryLandsInTheLibraryItsFolderSitsUnder()
    {
        var library = new FakeLibrary(Tv, Kids);

        var element = Annotate(true, library, Episode(100, 1, 1, path: "/media/tv/Show")).Single();

        Assert.Null(element["moonfinItemId"]);
        Assert.Null(element["moonfinEpisodeId"]);
        Assert.Equal(new[] { "tv" }, Libraries(element));
        Assert.Equal(new[] { "kids" }, Libraries(Annotate(true, library, Episode(100, 1, 1, path: @"D:\Kids\Show")).Single()));
    }

    [Fact]
    public void OtherwiseTheParentFolderNameMatchesALibraryFolderName()
    {
        var library = new FakeLibrary(Tv, Movies);

        Assert.Equal(new[] { "tv" }, Libraries(Annotate(true, library, Episode(100, 1, 1, path: "/TV/Show")).Single()));
    }

    [Fact]
    public void NoMatchAtAllIsAnEmptyListAndStays()
    {
        var library = new FakeLibrary(Tv, Anime);

        var element = Annotate(true, library, Episode(100, 1, 1, path: "/elsewhere/Show")).Single();

        Assert.Empty(Libraries(element));
    }

    [Fact]
    public void AnElementOnlyInLibrariesTheUserCantSeeIsDropped()
    {
        var library = new FakeLibrary(Tv, Anime)
            .Series("hidden", new CalendarProviderIds("100", null, null), "anime")
            .Series("shown", new CalendarProviderIds("101", null, null), "tv");

        var elements = Annotate(true, library, Episode(100, 1, 1), Episode(101, 1, 1), Episode(102, 1, 1, path: "/anime/Other"));

        Assert.Equal("shown", Str(elements.Single(), "moonfinItemId"));
    }

    [Fact]
    public void ACopyTheUserCanSeeIsTheOneHandedBackAndOnlyItsLibraryIsListed()
    {
        var library = new FakeLibrary(Tv, Anime)
            .Series("hidden", new CalendarProviderIds("100", null, null), "anime")
            .Series("shown", new CalendarProviderIds("100", null, null), "tv")
            .Episode("hiddenEp", "hidden", 1, 1)
            .Episode("shownEp", "shown", 1, 1);

        var element = Annotate(true, library, Episode(100, 1, 1)).Single();

        Assert.Equal("shown", Str(element, "moonfinItemId"));
        Assert.Equal("shownEp", Str(element, "moonfinEpisodeId"));
        Assert.Equal(new[] { "tv" }, Libraries(element));
    }

    [Fact]
    public void EpisodesAreLookedUpOncePerRequestGroupedBySeries()
    {
        var library = new FakeLibrary(Tv)
            .Series("s1", new CalendarProviderIds("100", null, null), "tv")
            .Series("s2", new CalendarProviderIds("200", null, null), "tv");
        var elements = Enumerable.Range(1, 30).Select(n => Episode(n % 2 == 0 ? 100 : 200, 1, n)).ToArray();

        Annotate(true, library, elements);

        Assert.Equal(1, library.TitleLookups);
        Assert.Equal(2, library.TitlesAsked);
        Assert.Equal(1, library.EpisodeLookups);
    }

    [Fact]
    public void ARadarrMovieGetsItsIdsWithoutAnEpisodeId()
    {
        var library = new FakeLibrary(Movies).Movie("m1", new CalendarProviderIds(null, "550", "tt0137523"), "movies");
        var movie = JsonSerializer.Serialize(new { title = "Fight Club", tmdbId = 550, imdbId = "tt0137523", path = "/movies/Fight Club (1999)" });

        var element = Annotate(false, library, movie).Single();

        Assert.Equal("m1", Str(element, "moonfinItemId"));
        Assert.False(element.ContainsKey("moonfinEpisodeId"));
        Assert.Equal(new[] { "movies" }, Libraries(element));
        Assert.Equal(0, library.EpisodeLookups);
    }

    [Fact]
    public void AnAnswerThatIsNoArrayIsPassedOn()
    {
        var body = Encoding.UTF8.GetBytes("{\"message\":\"Unauthorized\"}");

        Assert.Same(body, ArrCalendarAnnotator.Annotate(body, true, new FakeLibrary()));
    }

    private sealed class FakeLibrary : ICalendarLibrary
    {
        private readonly List<CalendarLibraryFolder> _folders;
        private readonly List<(CalendarLibraryItem Item, bool Series)> _items = new List<(CalendarLibraryItem, bool)>();
        private readonly List<CalendarLibraryEpisode> _episodes = new List<CalendarLibraryEpisode>();

        public FakeLibrary(params CalendarLibraryFolder[] folders)
        {
            _folders = folders.ToList();
        }

        public int TitleLookups { get; private set; }

        public int TitlesAsked { get; private set; }

        public int EpisodeLookups { get; private set; }

        public FakeLibrary Series(string id, CalendarProviderIds ids, params string[] libraries)
        {
            _items.Add((new CalendarLibraryItem(id, ids, libraries), true));
            return this;
        }

        public FakeLibrary Movie(string id, CalendarProviderIds ids, params string[] libraries)
        {
            _items.Add((new CalendarLibraryItem(id, ids, libraries), false));
            return this;
        }

        public FakeLibrary Episode(string id, string seriesId, int season, int episode, int? episodeEnd = null)
        {
            _episodes.Add(new CalendarLibraryEpisode(id, seriesId, season, episode, episodeEnd));
            return this;
        }

        public IReadOnlyList<CalendarLibraryFolder> Folders() => _folders;

        // Hands back every title of the kind, as a server that over-matches would.
        public IReadOnlyList<CalendarLibraryItem> FindTitles(bool series, IReadOnlyCollection<CalendarProviderIds> titles)
        {
            TitleLookups++;
            TitlesAsked += titles.Count;
            return _items.Where(i => i.Series == series).Select(i => i.Item).ToList();
        }

        public IReadOnlyList<CalendarLibraryEpisode> FindEpisodes(IReadOnlyCollection<string> seriesIds)
        {
            EpisodeLookups++;
            return _episodes.Where(e => seriesIds.Contains(e.SeriesId)).ToList();
        }
    }
}

public class CalendarReminderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moonfin-reminders-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private DateTime _now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private CalendarReminderStore NewStore() => new CalendarReminderStore(_folder, PlainStateFile.Instance, () => _now);

    private static CalendarReminder EpisodeReminder(int tvdb = 81189, int season = 5, int episode = 14) =>
        CalendarReminders.Normalize(
            new CalendarReminder { Kind = "episode", TvdbId = tvdb, TmdbId = 1396, SeasonNumber = season, EpisodeNumber = episode, Title = "Breaking Bad", Subtitle = "Ozymandias" },
            out _)!;

    private static CalendarReminder MovieReminder(int? tmdb = 550, string? imdb = "tt0137523") =>
        CalendarReminders.Normalize(new CalendarReminder { Kind = "movie", TmdbId = tmdb, ImdbId = imdb, Title = "Fight Club" }, out _)!;

    [Fact]
    public void KeysAreTheOnesTheClientComputes()
    {
        Assert.Equal("episode:81189:5:14", EpisodeReminder().Key);
        Assert.Equal("episode:81189:0:1", EpisodeReminder(season: 0, episode: 1).Key);
        Assert.Equal("movie:550", MovieReminder().Key);
        Assert.Equal("movie:imdb:tt0137523", MovieReminder(tmdb: null).Key);
    }

    [Fact]
    public void AKeyTheClientSentIsReplaced()
    {
        var reminder = CalendarReminders.Normalize(new CalendarReminder { Key = "anything", Kind = "Movie", TmdbId = 550, Title = "Fight Club" }, out _)!;

        Assert.Equal("movie:550", reminder.Key);
        Assert.Equal("movie", reminder.Kind);
    }

    [Theory]
    [InlineData("episode", null, 5, 14, null, "Show")]
    [InlineData("episode", 81189, null, 14, null, "Show")]
    [InlineData("movie", null, null, null, null, "Movie")]
    [InlineData("movie", null, null, null, "nm123", "Movie")]
    [InlineData("show", 81189, 5, 14, null, "Show")]
    [InlineData("movie", null, null, null, "tt1", "")]
    public void AReminderWithoutWhatItsKeyNeedsIsRefused(string kind, int? tvdb, int? season, int? episode, string? imdb, string title)
    {
        var tmdb = kind == "movie" && imdb == "tt1" ? 1 : (int?)null;
        var reminder = CalendarReminders.Normalize(
            new CalendarReminder { Kind = kind, TvdbId = tvdb, TmdbId = tmdb, ImdbId = imdb, SeasonNumber = season, EpisodeNumber = episode, Title = title },
            out var error);

        Assert.Null(reminder);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NumbersSentAsStringsAreRead()
    {
        var reminder = JsonSerializer.Deserialize<CalendarReminder>("{\"kind\":\"episode\",\"tvdbId\":\"81189\",\"seasonNumber\":\"5\",\"episodeNumber\":14,\"title\":\"Breaking Bad\"}")!;

        Assert.Equal("episode:81189:5:14", CalendarReminders.Normalize(reminder, out _)!.Key);
    }

    [Fact]
    public void RemindersSurviveANewStore()
    {
        var stored = NewStore().Upsert(_user, EpisodeReminder())!;

        var reread = NewStore().List(_user).Single();

        Assert.Equal(_now, stored.CreatedAt);
        Assert.Equal("episode:81189:5:14", reread.Key);
        Assert.Equal("Ozymandias", reread.Subtitle);
        Assert.Equal(1396, reread.TmdbId);
        Assert.Equal(_now, reread.CreatedAt);
        Assert.Empty(NewStore().List(Guid.NewGuid()));
    }

    [Fact]
    public void UpsertingTheSameKeyReplacesItAndKeepsTheFirstCreatedAt()
    {
        var store = NewStore();
        var first = store.Upsert(_user, EpisodeReminder())!;
        _now = _now.AddDays(3);
        var changed = EpisodeReminder();
        changed.Subtitle = "Renamed";

        var second = store.Upsert(_user, changed)!;

        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("Renamed", NewStore().List(_user).Single().Subtitle);
    }

    [Fact]
    public void RemoveIsTrueOnlyForAReminderThatWasThere()
    {
        var store = NewStore();
        store.Upsert(_user, MovieReminder());

        Assert.True(store.Remove(_user, "movie:550"));
        Assert.False(store.Remove(_user, "movie:550"));
        Assert.Empty(NewStore().List(_user));
        Assert.True(NewStore().IsEmpty);
    }

    [Fact]
    public void RemindersOlderThanAYearAreDroppedOnLoad()
    {
        var store = NewStore();
        store.Upsert(_user, MovieReminder());
        _now = _now.AddDays(200);
        store.Upsert(_user, EpisodeReminder());
        _now = _now.AddDays(200);

        Assert.Equal(new[] { "episode:81189:5:14" }, NewStore().List(_user).Select(r => r.Key));
    }

    [Fact]
    public void AUserAtTheLimitCanReplaceButNotAdd()
    {
        var store = NewStore();
        for (var i = 1; i <= CalendarReminders.MaxPerUser; i++) store.Upsert(_user, MovieReminder(tmdb: i, imdb: null));

        Assert.Null(store.Upsert(_user, MovieReminder(tmdb: 100000, imdb: null)));
        Assert.NotNull(store.Upsert(_user, MovieReminder(tmdb: 1, imdb: null)));
    }

    [Fact]
    public void AnEpisodeFiresTheReminderForItsSeriesSeasonAndNumber()
    {
        var store = NewStore();
        var other = Guid.NewGuid();
        store.Upsert(_user, EpisodeReminder());
        store.Upsert(other, EpisodeReminder(episode: 15));

        var matches = store.Matching(new CalendarArrival(true, new CalendarProviderIds("81189", null, null), 5, 14, null));

        Assert.Equal(_user, matches.Single().UserId);
        Assert.Equal("episode:81189:5:14", matches.Single().Reminder.Key);
    }

    [Theory]
    [InlineData("81189", null, 5, 14, null, true)]
    [InlineData(null, "1396", 5, 14, null, true)]
    [InlineData("81189", null, 5, 13, 14, true)]
    [InlineData("81189", null, 5, 13, null, false)]
    [InlineData("81189", null, 4, 14, null, false)]
    [InlineData("12345", null, 5, 14, null, false)]
    [InlineData("81189", null, null, null, null, false)]
    public void EpisodeMatching(string? tvdb, string? tmdb, int? season, int? episode, int? episodeEnd, bool matches)
    {
        var arrival = new CalendarArrival(true, new CalendarProviderIds(tvdb, tmdb, null), season, episode, episodeEnd);

        Assert.Equal(matches, CalendarReminders.Matches(EpisodeReminder(), arrival));
    }

    [Fact]
    public void AMovieMatchesOnTmdbOrImdbButNeverAnEpisode()
    {
        Assert.True(CalendarReminders.Matches(MovieReminder(), new CalendarArrival(false, new CalendarProviderIds(null, "550", null), null, null, null)));
        Assert.True(CalendarReminders.Matches(MovieReminder(tmdb: null), new CalendarArrival(false, new CalendarProviderIds(null, null, "TT0137523"), null, null, null)));
        Assert.False(CalendarReminders.Matches(MovieReminder(), new CalendarArrival(true, new CalendarProviderIds(null, "550", null), 1, 1, null)));
        Assert.False(CalendarReminders.Matches(MovieReminder(), new CalendarArrival(false, new CalendarProviderIds(null, "551", "tt1"), null, null, null)));
    }

    [Fact]
    public void TheMessageNamesTheTitleAndEpisode()
    {
        Assert.Equal(("Breaking Bad is in your library", "S5E14 Ozymandias"), CalendarReminders.Message(EpisodeReminder()));
        Assert.Equal(("Fight Club is in your library", "Ready to watch"), CalendarReminders.Message(MovieReminder()));

        var unnamed = EpisodeReminder();
        unnamed.Subtitle = null;
        Assert.Equal("S5E14", CalendarReminders.Message(unnamed).Body);
    }
}
