using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Resolves a series to its AnimeFillerList show and the cached episode table for it, if any.
/// </summary>
public class AnimeMarkerResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager? _mediaSourceManager;
    private readonly AnimeFillerListClient _client;
    private readonly AnimeMarkerCacheService _cache;
    private readonly ILogger<AnimeMarkerResolver> _logger;

    public AnimeMarkerResolver(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        AnimeFillerListClient client,
        AnimeMarkerCacheService cache,
        ILogger<AnimeMarkerResolver> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Returns the series that are candidates for matching to an AnimeFillerList show, based on the configured library ids. 
    /// When no libraries are configured, the whole server is scanned.
    /// When libraries are configured but none can be resolved, returns an empty list and logs a warning.
    /// </summary>
    public List<Series> GetCandidateSeries()
    {
        var libraryIds = GetSelectedLibraryFolderIds();

        if (libraryIds == null)
        {
            return QuerySeries(null).ToList();
        }

        // Libraries were selected but none resolved. Scanning the whole server instead
        // would be the opposite of what was asked for, so scan nothing and say so.
        if (libraryIds.Count == 0)
        {
            _logger.LogWarning("Anime markers: libraries are selected but none could be resolved, so nothing was scanned");
            return new List<Series>();
        }

        var candidates = new List<Series>();
        var seen = new HashSet<Guid>();

        foreach (var libraryId in libraryIds)
        {
            foreach (var series in QuerySeries(libraryId))
            {
                if (seen.Add(series.Id))
                {
                    candidates.Add(series);
                }
            }
        }

        return candidates;
    }

    private IEnumerable<Series> QuerySeries(Guid? parentId)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            IsVirtualItem = false,
            Recursive = true
        };

        if (parentId != null)
        {
            query.ParentId = parentId.Value;
        }

        return _libraryManager.GetItemsResult(query).Items.OfType<Series>();
    }

    /// <summary>
    /// Returns the configured library folder ids, or null when no libraries are configured.
    /// </summary>
    public HashSet<Guid>? GetSelectedLibraryFolderIds()
    {
        var configuredIds = MoonfinPlugin.Instance?.Configuration?.AnimeMarkerLibraryIds ?? new List<string>();
        if (configuredIds.Count == 0)
        {
            return null;
        }

        var resolved = new HashSet<Guid>();

        foreach (var rawId in configuredIds)
        {
            if (!Guid.TryParse(rawId, out var libraryId))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(libraryId);
            if (item == null)
            {
                // Unknown to the library manager, but the id may still work as a parent.
                // Worst case the query returns nothing.
                resolved.Add(libraryId);
                continue;
            }

            // Only follow the display parent for a view. Doing it for a collection folder
            // would walk up to the media root and pull in the whole server.
            if (item is UserView view && !view.DisplayParentId.Equals(default))
            {
                resolved.Add(view.DisplayParentId);
            }
            else
            {
                resolved.Add(item.Id);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Finds the AnimeFillerList show for a series, by its library name and its original
    /// title. Returns null when neither matches, which is the right answer for the large
    /// majority of a general library.
    /// </summary>
    public AnimeFillerShow? MatchSeries(Series series) =>
        AnimeTitleMatcher.Match(_client.Index, WithProductionYear(series), series.Name, series.OriginalTitle);

    /// <summary>
    /// The series' name with its production year appended, when it has one.
    /// </summary>
    private static string? WithProductionYear(Series series) =>
        series.ProductionYear is { } year && !string.IsNullOrWhiteSpace(series.Name)
            ? $"{series.Name} ({year.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : null;

    /// <summary>
    /// Every series that matches a show on the site, paired with the show. Used both by the
    /// sync task to decide what to fetch and by the diagnostics endpoint.
    /// </summary>
    public List<SeriesMatch> BuildMatches()
    {
        var matches = new List<SeriesMatch>();

        foreach (var series in GetCandidateSeries())
        {
            var show = MatchSeries(series);
            if (show != null)
            {
                matches.Add(new SeriesMatch(series, show));
            }
        }

        return matches;
    }

    /// <summary>
    /// Markers for one series' episodes, keyed by the Jellyfin episode id in "N" form.
    /// Returns an empty result when the series does not match a show or its table has not
    /// been fetched yet.
    /// </summary>
    public SeriesMarkerResult GetMarkersForSeries(Series series, TimeSpan maxAge)
    {
        var result = new SeriesMarkerResult();

        var show = MatchSeries(series);
        if (show == null)
        {
            return result;
        }

        result.Slug = show.Slug;
        result.Title = show.Title;

        var entry = _cache.TryGet(show.Slug, maxAge);
        if (entry == null)
        {
            result.Pending = true;
            return result;
        }

        var byNumber = new Dictionary<int, AnimeMarkerEpisode>();
        foreach (var episode in entry.Episodes)
        {
            byNumber[episode.Number] = episode;
        }

        result.RecapKnown = entry.RecapMalId != null;

        foreach (var numbered in BuildNumbering(series).Episodes)
        {
            if (byNumber.TryGetValue(numbered.AbsoluteNumber, out var marker))
            {
                result.Episodes[numbered.Episode.Id.ToString("N")] = marker;
            }
        }

        return result;
    }

    /// <summary>
    /// Get the audio kind for a given item.
    /// </summary>
    public AnimeAudioKind? GetAudioForItem(BaseItem item)
    {
        try
        {
            return AnimeAudioClassifier.Classify(
                AnimeMediaStreamReader.GetAudioLanguages(_mediaSourceManager, item));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anime markers: audio streams unreadable for {Item}", item.Id);
            return null;
        }
    }

    /// <summary>
    /// True when the item is in a selected library or looks like anime, and so is a candidate
    /// for audio markers. When no libraries are configured, the whole server is scanned.
    /// </summary>
    public bool IsAudioMarkerCandidateItem(BaseItem item)
    {
        var libraryIds = GetSelectedLibraryFolderIds();

        // No libraries chosen, all items are candidates.
        if (libraryIds == null)
        {
            return LooksLikeAnime(item);
        }

        if (!IsInSelectedLibrary(item, libraryIds))
        {
            return false;
        }

        // Treats all items as Anime even if they don't have a match.
        // Only for Anime only Libraries.
        return TrustsSelectedLibraries || LooksLikeAnime(item);
    }

    private static bool TrustsSelectedLibraries =>
        MoonfinPlugin.Instance?.Configuration?.AnimeAudioTrustSelectedLibraries == true;

    private bool IsInSelectedLibrary(BaseItem item, HashSet<Guid> libraryIds)
    {
        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                if (libraryIds.Contains(folder.Id))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anime markers: collection folders unreadable for {Item}", item.Id);
        }

        for (var parent = item.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (libraryIds.Contains(parent.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the series is a candidate for audio markers, either because it is in a
    /// selected library or because it looks like anime. The latter is a best-effort guess
    /// based on the ids the anime metadata plugins write and on an explicit genre or tag.
    /// </summary>
    public bool IsAudioMarkerCandidate(Series series) => IsAudioMarkerCandidateItem(series);

    private static readonly string[] AnimeProviderKeys =
    {
        "AniList", "AniDB", "AniDb", "Anidb", "AniSearch", "Kitsu", "KitsuIo", "MyAnimeList", "Mal"
    };

    /// <summary>
    /// True when the series looks like anime, based on its provider ids, genres, or tags.
    /// </summary>
    public static bool LooksLikeAnime(BaseItem item)
    {
        // An episode carries almost no provider ids of its own; the anime ids live on the
        // series. Checking the episode alone would call every anime episode "not anime".
        if (item is Episode episode)
        {
            var parentSeries = episode.Series;
            if (parentSeries != null && LooksLikeAnimeCore(parentSeries))
            {
                return true;
            }
        }

        return LooksLikeAnimeCore(item);
    }

    private static bool LooksLikeAnimeCore(BaseItem series)
    {
        foreach (var key in AnimeProviderKeys)
        {
            foreach (var providerKey in series.ProviderIds.Keys)
            {
                if (string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var genre in series.Genres)
        {
            if (genre.Contains("anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var tag in series.Tags)
        {
            if (tag.Contains("anime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the subbed/dubbed verdicts for a series' episodes and seasons, based on the
    /// languages of the audio tracks in each file. Returns an empty result when the series
    /// has no episodes or none of them have audio streams.
    /// </summary>
    private static readonly TimeSpan AudioCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, AudioMarkerResult Result)>
        AudioCache = new();

    public AudioMarkerResult BuildAudioMarkers(Series series)
    {
        if (AudioCache.TryGetValue(series.Id, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < AudioCacheTtl)
        {
            return cached.Result;
        }

        var result = new AudioMarkerResult();

        var episodes = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentId = series.Id,
            IsVirtualItem = false,
            Recursive = true
        }).Items.OfType<Episode>()
            .GroupBy(episode => episode.Id)
            .Select(group => group.First())
            .ToList();

        var bySeason = new Dictionary<Guid, List<AnimeAudioKind?>>();

        foreach (var episode in episodes)
        {
            AnimeAudioKind? kind;
            try
            {
                kind = AnimeAudioClassifier.Classify(
                    AnimeMediaStreamReader.GetAudioLanguages(_mediaSourceManager, episode));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Anime markers: audio streams unreadable for {Episode}", episode.Id);
                kind = null;
            }

            if (kind != null)
            {
                result.Episodes[episode.Id.ToString("N")] = kind.Value;
            }

            var seasonId = episode.SeasonId;
            if (!seasonId.Equals(default))
            {
                if (!bySeason.TryGetValue(seasonId, out var kinds))
                {
                    kinds = new List<AnimeAudioKind?>();
                    bySeason[seasonId] = kinds;
                }

                kinds.Add(kind);
            }
        }

        foreach (var (seasonId, kinds) in bySeason)
        {
            var seasonKind = AnimeAudioClassifier.ClassifySeason(kinds);
            if (seasonKind != null)
            {
                result.Seasons[seasonId.ToString("N")] = seasonKind.Value;
            }
        }

        AudioCache[series.Id] = (DateTimeOffset.UtcNow, result);

        // Bounded so a large library cannot grow this without limit.
        if (AudioCache.Count > 200)
        {
            foreach (var stale in AudioCache
                         .Where(pair => DateTimeOffset.UtcNow - pair.Value.At >= AudioCacheTtl)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                AudioCache.TryRemove(stale, out _);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the cached entry for a series' matched show, or null when the series 
    /// does not match a show or the table has not been fetched yet. 
    /// The entry is considered stale if it is older than the specified age.
    /// </summary>
    public AnimeMarkerCacheEntry? GetCachedEntry(Series series, TimeSpan maxAge)
    {
        var show = MatchSeries(series);
        return show == null ? null : _cache.TryGet(show.Slug, maxAge);
    }

    /// <summary>
    /// Builds a series' absolute numbering from its season and index numbers, 
    /// and returns the numbering mode the library is using. 
    /// The mode is surfaced because it is the single
    /// most useful thing to see when markers land on the wrong episodes.
    /// </summary>
    public SeriesNumbering BuildNumbering(Series series)
    {
        var episodes = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentId = series.Id,
            IsVirtualItem = false,
            Recursive = true
        }).Items.OfType<Episode>()
            // Season 0 is specials, which the site does not list alongside the main run.
            .Where(episode => episode.ParentIndexNumber is > 0 && episode.IndexNumber is > 0)
            // A recursive query under a series matches each episode once per ancestor it
            // has, so an episode inside a season comes back twice. Left in, that doubles
            // the episode count and makes a genuinely absolute-numbered library look like
            // it repeats its index numbers, which picks the wrong numbering scheme.
            .GroupBy(episode => episode.Id)
            .Select(group => group.First())
            .OrderBy(episode => episode.ParentIndexNumber!.Value)
            .ThenBy(episode => episode.IndexNumber!.Value)
            .ToList();

        // Two library items can still legitimately share one season/episode slot, such as a
        // second cut of the same episode. They are one episode as far as the site is
        // concerned, so the numbering is worked out over the distinct slots and every item
        // in a slot then takes that slot's number.
        var slots = episodes
            .Select(episode => (Season: episode.ParentIndexNumber!.Value, Index: episode.IndexNumber!.Value))
            .Distinct()
            .OrderBy(slot => slot.Season)
            .ThenBy(slot => slot.Index)
            .ToList();

        var numbers = AssignAbsoluteNumbers(slots);
        var absoluteBySlot = new Dictionary<(int Season, int Index), int>();
        for (var i = 0; i < slots.Count; i++)
        {
            absoluteBySlot[slots[i]] = numbers[i];
        }

        var numbered = episodes
            .Select(episode => new NumberedEpisode(
                episode,
                absoluteBySlot[(episode.ParentIndexNumber!.Value, episode.IndexNumber!.Value)]))
            .ToList();

        return new SeriesNumbering(IsAlreadyAbsolute(slots) ? "absolute" : "per-season", numbered);
    }

    /// <summary>
    /// True when the library already numbers this series absolutely (S04E207), which is the
    /// case exactly when no index number repeats across the whole series.
    /// </summary>
    public static bool IsAlreadyAbsolute(IReadOnlyList<(int Season, int Index)> episodes) =>
        episodes.Count > 0 &&
        episodes.Select(episode => episode.Index).Distinct().Count() == episodes.Count;

    /// <summary>
    /// The absolute episode number for each of a series' episodes, given their season and
    /// index numbers already sorted by season then index. Split out from the library query
    /// because this is the part with the actual reasoning in it.
    /// </summary>
    public static int[] AssignAbsoluteNumbers(IReadOnlyList<(int Season, int Index)> episodes)
    {
        var numbers = new int[episodes.Count];
        if (episodes.Count == 0)
        {
            return numbers;
        }

        // Index numbers unique across the whole series means the library is already
        // numbering absolutely, so they are the answer as-is. When they repeat,
        // each season restarts at 1 and the earlier seasons have to be added back on.
        if (IsAlreadyAbsolute(episodes))
        {
            for (var i = 0; i < episodes.Count; i++)
            {
                numbers[i] = episodes[i].Index;
            }

            return numbers;
        }

        // A season's length is the highest index seen in it, not how many episodes are
        // present, so a library missing an episode mid-season does not shift every later
        // episode onto the wrong marker.
        var seasonLengths = new Dictionary<int, int>();
        foreach (var (season, index) in episodes)
        {
            seasonLengths[season] = seasonLengths.TryGetValue(season, out var length) && length > index
                ? length
                : index;
        }

        var offsets = new Dictionary<int, int>();
        var running = 0;
        foreach (var season in seasonLengths.Keys.OrderBy(season => season))
        {
            offsets[season] = running;
            running += seasonLengths[season];
        }

        for (var i = 0; i < episodes.Count; i++)
        {
            numbers[i] = offsets[episodes[i].Season] + episodes[i].Index;
        }

        return numbers;
    }
}

/// <summary>
/// Subbed/dubbed verdicts for one series, keyed by Jellyfin id in "N" form. A season
/// appears only when every episode in it agreed.
/// </summary>
public class AudioMarkerResult
{
    public Dictionary<string, AnimeAudioKind> Episodes { get; } = new();

    public Dictionary<string, AnimeAudioKind> Seasons { get; } = new();
}

/// <summary>One library series and the AnimeFillerList show it matched.</summary>
public record SeriesMatch(Series Series, AnimeFillerShow Show);

/// <summary>One library episode and the absolute number the site would use for it.</summary>
public record NumberedEpisode(Episode Episode, int AbsoluteNumber);

/// <summary>
/// The absolute numbering of a series, along with the mode used to determine it.
/// </summary>
public record SeriesNumbering(string Mode, IReadOnlyList<NumberedEpisode> Episodes);

/// <summary>What the client gets back for one series.</summary>
public class SeriesMarkerResult
{
    /// <summary>Jellyfin episode id ("N" form) to its marker. Unmatched episodes are absent.</summary>
    public Dictionary<string, AnimeMarkerEpisode> Episodes { get; } = new();

    /// <summary>The matched show's slug, or null when the series did not match.</summary>
    public string? Slug { get; set; }

    /// <summary>The matched show's title on the site, for the admin readout.</summary>
    public string? Title { get; set; }

    /// <summary>True when the series matched a show whose table has not been fetched yet.</summary>
    public bool Pending { get; set; }

    /// <summary>
    /// True when the recap pass has run for this show. When false, an episode's recap flag
    /// is "not looked up" rather than "not a recap".
    /// </summary>
    public bool RecapKnown { get; set; }
}
