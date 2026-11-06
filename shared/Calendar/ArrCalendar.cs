using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Moonfin.Calendar;

/// <summary>
/// How a Sonarr or Radarr calendar request is shaped before the arr is asked: the dates held to
/// the admin's window, the query the arr gets and the key its answer is cached under.
/// </summary>
public static class ArrCalendarWindow
{
    public const string AdminsOnlyError = "The calendar is limited to admins on this server";

    /// <summary>The 403 error for a caller kept out by the admins-only setting, or null when they may see the calendar.</summary>
    public static string? Refusal(bool adminsOnly, bool isAdmin) => adminsOnly && !isAdmin ? AdminsOnlyError : null;

    /// <summary>
    /// The requested dates held between today minus daysBack and today plus daysAhead. A missing
    /// start is today and a missing end is the last day of the window. Null when nothing is left.
    /// </summary>
    public static (DateTime Start, DateTime End)? Clamp(string? start, string? end, DateTime today, int daysBack, int daysAhead)
    {
        var day = today.Date;
        var earliest = day.AddDays(-Math.Max(0, daysBack));
        var latest = day.AddDays(Math.Max(0, daysAhead));
        var from = ParseDay(start) ?? day;
        var to = ParseDay(end) ?? latest;
        if (from < earliest) from = earliest;
        if (to > latest) to = latest;
        return from > to ? ((DateTime, DateTime)?)null : (from, to);
    }

    /// <summary>The arr's calendar query. Leaving unmonitored out is the arr's own monitored-only default.</summary>
    public static string Query(bool isSonarr, DateTime start, DateTime end, bool unmonitored)
    {
        var query = "start=" + Day(start) + "&end=" + Day(end);
        if (isSonarr) query += "&includeSeries=true&includeEpisodeImages=true";
        if (unmonitored) query += "&unmonitored=true";
        return query;
    }

    public static string CacheKey(bool isSonarr, DateTime start, DateTime end, bool unmonitored) =>
        (isSonarr ? "sonarr" : "radarr") + "|" + Day(start) + "|" + Day(end) + "|" + (unmonitored ? "all" : "monitored");

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime? ParseDay(string? value) =>
        !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.Date
            : (DateTime?)null;
}

/// <summary>
/// Answers kept for a few minutes. Only an answer keep accepts is stored, so an arr that failed
/// is asked again on the next request.
/// </summary>
public sealed class ArrCalendarCache<T>
    where T : class
{
    // Every window a client asks for is its own entry, so a caller stepping through months can't grow it without bound.
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, (T Value, DateTime Expires)> _entries = new ConcurrentDictionary<string, (T Value, DateTime Expires)>();
    private readonly Func<DateTime> _now;

    public ArrCalendarCache(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    public async Task<T> GetOrFetchAsync(string key, int minutes, Func<Task<T>> fetch, Func<T, bool> keep)
    {
        if (minutes <= 0) return await fetch().ConfigureAwait(false);
        if (_entries.TryGetValue(key, out var hit) && hit.Expires > _now()) return hit.Value;

        var value = await fetch().ConfigureAwait(false);
        if (keep(value))
        {
            var now = _now();
            foreach (var entry in _entries)
            {
                if (entry.Value.Expires <= now) _entries.TryRemove(entry.Key, out _);
            }

            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = (value, now.AddMinutes(minutes));
        }

        return value;
    }
}

/// <summary>A title's provider ids. An id the arr doesn't have is null.</summary>
public sealed class CalendarProviderIds
{
    public CalendarProviderIds(string? tvdb, string? tmdb, string? imdb)
    {
        Tvdb = Clean(tvdb);
        Tmdb = Clean(tmdb);
        Imdb = Clean(imdb);
    }

    public string? Tvdb { get; }

    public string? Tmdb { get; }

    public string? Imdb { get; }

    public bool IsEmpty => Tvdb == null && Tmdb == null && Imdb == null;

    internal string Key => Tvdb + "|" + Tmdb + "|" + Imdb?.ToLowerInvariant();

    // Sonarr and Radarr send 0 for an id they don't have.
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "0" ? null : trimmed;
    }
}

/// <summary>A series or movie in the library and the libraries it is in.</summary>
public sealed class CalendarLibraryItem
{
    public CalendarLibraryItem(string id, CalendarProviderIds ids, IReadOnlyList<string> libraryIds)
    {
        Id = id;
        Ids = ids;
        LibraryIds = libraryIds;
    }

    public string Id { get; }

    public CalendarProviderIds Ids { get; }

    public IReadOnlyList<string> LibraryIds { get; }
}

/// <summary>An episode in the library. EpisodeEnd is set for a file that holds several episodes.</summary>
public sealed class CalendarLibraryEpisode
{
    public CalendarLibraryEpisode(string id, string seriesId, int? season, int? episode, int? episodeEnd)
    {
        Id = id;
        SeriesId = seriesId;
        Season = season;
        Episode = episode;
        EpisodeEnd = episodeEnd;
    }

    public string Id { get; }

    public string SeriesId { get; }

    public int? Season { get; }

    public int? Episode { get; }

    public int? EpisodeEnd { get; }
}

/// <summary>A library, its folders on disk and whether the requesting user can see it.</summary>
public sealed class CalendarLibraryFolder
{
    public CalendarLibraryFolder(string id, IReadOnlyList<string> locations, bool visible)
    {
        Id = id;
        Locations = locations;
        Visible = visible;
    }

    public string Id { get; }

    public IReadOnlyList<string> Locations { get; }

    public bool Visible { get; }
}

/// <summary>What the annotation asks of the media server, answered once per calendar request.</summary>
public interface ICalendarLibrary
{
    IReadOnlyList<CalendarLibraryFolder> Folders();

    /// <summary>Series or movies holding any of these ids. Extra matches are fine, the annotation ranks them.</summary>
    IReadOnlyList<CalendarLibraryItem> FindTitles(bool series, IReadOnlyCollection<CalendarProviderIds> titles);

    IReadOnlyList<CalendarLibraryEpisode> FindEpisodes(IReadOnlyCollection<string> seriesIds);
}

/// <summary>
/// Adds the media server's ids and libraries to each element of a Sonarr or Radarr calendar, and
/// drops the elements whose libraries the user can't see. Runs per user after the cache.
/// </summary>
public static class ArrCalendarAnnotator
{
    public static byte[] Annotate(byte[] body, bool isSonarr, ICalendarLibrary library)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return body;
        }

        if (array == null || array.Count == 0) return body;

        var elements = array.ToList();
        array.Clear();
        var titles = elements.Select(e => e is JsonObject element ? TitleOf(element, isSonarr) : null).ToList();
        var wanted = titles
            .Where(t => t != null && !t.Ids.IsEmpty)
            .GroupBy(t => t!.Ids.Key)
            .Select(g => g.First()!.Ids)
            .ToList();
        var candidates = wanted.Count == 0 ? new List<CalendarLibraryItem>() : library.FindTitles(isSonarr, wanted);
        var folders = library.Folders();
        var visible = new HashSet<string>(folders.Where(f => f.Visible).Select(f => f.Id), StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<string, Resolution>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            if (title != null && !resolved.ContainsKey(title.Key)) resolved[title.Key] = Resolve(title, candidates, folders, visible);
        }

        var episodes = new List<CalendarLibraryEpisode>().ToLookup(e => e.SeriesId);
        if (isSonarr)
        {
            var seriesIds = resolved.Values.SelectMany(r => r.ItemIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (seriesIds.Count > 0) episodes = library.FindEpisodes(seriesIds).ToLookup(e => e.SeriesId, StringComparer.OrdinalIgnoreCase);
        }

        for (var i = 0; i < elements.Count; i++)
        {
            var node = elements[i];
            var title = titles[i];
            if (!(node is JsonObject element) || title == null)
            {
                array.Add(node);
                continue;
            }

            var resolution = resolved[title.Key];
            if (resolution.Libraries.Count > 0 && resolution.Visible.Count == 0) continue;

            element["moonfinItemId"] = resolution.ItemIds.FirstOrDefault();
            if (isSonarr) element["moonfinEpisodeId"] = EpisodeId(element, resolution.ItemIds, episodes);
            var libraryIds = new JsonArray();
            foreach (var id in resolution.Visible) libraryIds.Add(JsonValue.Create(id));
            element["moonfinLibraryIds"] = libraryIds;
            array.Add(element);
        }

        return Encoding.UTF8.GetBytes(array.ToJsonString());
    }

    /// <summary>The libraries an arr folder lands in: a library folder it sits under, then one named like its parent folder.</summary>
    private static List<string> LibrariesForPath(string? path, IReadOnlyList<CalendarLibraryFolder> folders)
    {
        var target = NormalizePath(path);
        if (target == null) return new List<string>();

        var byPrefix = folders.Where(f => f.Locations.Any(l => IsUnder(target, NormalizePath(l)))).Select(f => f.Id).Distinct().ToList();
        if (byPrefix.Count > 0) return byPrefix;

        var cut = target.LastIndexOf('/');
        var parent = cut > 0 ? LastSegment(target.Substring(0, cut)) : null;
        if (string.IsNullOrEmpty(parent)) return new List<string>();

        return folders
            .Where(f => f.Locations.Any(l => string.Equals(LastSegment(NormalizePath(l)), parent, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Id)
            .Distinct()
            .ToList();
    }

    /// <summary>The library copies of a title, by TVDB id, then TMDB, then IMDb, the order the arr ranks them in.</summary>
    private static List<CalendarLibraryItem> Match(CalendarProviderIds ids, IEnumerable<CalendarLibraryItem> candidates)
    {
        var list = candidates.ToList();
        if (ids.Tvdb != null)
        {
            var byTvdb = list.Where(c => string.Equals(c.Ids.Tvdb, ids.Tvdb, StringComparison.Ordinal)).ToList();
            if (byTvdb.Count > 0) return byTvdb;
        }

        if (ids.Tmdb != null)
        {
            var byTmdb = list.Where(c => string.Equals(c.Ids.Tmdb, ids.Tmdb, StringComparison.Ordinal)).ToList();
            if (byTmdb.Count > 0) return byTmdb;
        }

        return ids.Imdb == null
            ? new List<CalendarLibraryItem>()
            : list.Where(c => string.Equals(c.Ids.Imdb, ids.Imdb, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static Resolution Resolve(Title title, IReadOnlyList<CalendarLibraryItem> candidates, IReadOnlyList<CalendarLibraryFolder> folders, HashSet<string> visible)
    {
        var matches = title.Ids.IsEmpty ? new List<CalendarLibraryItem>() : Match(title.Ids, candidates);
        if (matches.Count == 0)
        {
            var byPath = LibrariesForPath(title.Path, folders);
            return new Resolution(new List<string>(), byPath, byPath.Where(visible.Contains).ToList());
        }

        // A copy the user can see comes first, so the id handed back is one they can open.
        var itemIds = matches
            .OrderBy(m => m.LibraryIds.Any(visible.Contains) ? 0 : 1)
            .Select(m => m.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var libraries = matches.SelectMany(m => m.LibraryIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Resolution(itemIds, libraries, libraries.Where(visible.Contains).ToList());
    }

    private static string? EpisodeId(JsonObject element, List<string> seriesIds, ILookup<string, CalendarLibraryEpisode> episodes)
    {
        var season = Number(element["seasonNumber"]);
        var number = Number(element["episodeNumber"]);
        if (season == null || number == null) return null;

        foreach (var seriesId in seriesIds)
        {
            foreach (var episode in episodes[seriesId])
            {
                if (episode.Season == season && episode.Episode <= number && number <= (episode.EpisodeEnd ?? episode.Episode)) return episode.Id;
            }
        }

        return null;
    }

    private static Title TitleOf(JsonObject element, bool isSonarr)
    {
        var source = isSonarr ? element["series"] as JsonObject : element;
        if (source == null) return new Title(new CalendarProviderIds(null, null, null), null);

        var ids = new CalendarProviderIds(isSonarr ? Text(source["tvdbId"]) : null, Text(source["tmdbId"]), Text(source["imdbId"]));
        return new Title(ids, Text(source["path"]));
    }

    private static string? Text(JsonNode? node)
    {
        if (!(node is JsonValue value)) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    private static string? NormalizePath(string? path)
    {
        var normalized = path?.Trim().Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsUnder(string path, string? root) =>
        root != null && (string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

    private static string? LastSegment(string? path)
    {
        if (path == null) return null;
        var cut = path.LastIndexOf('/');
        return cut >= 0 ? path.Substring(cut + 1) : path;
    }

    private sealed class Title
    {
        public Title(CalendarProviderIds ids, string? path)
        {
            Ids = ids;
            Path = path;
        }

        public CalendarProviderIds Ids { get; }

        public string? Path { get; }

        public string Key => Ids.Key + "|" + Path;
    }

    private sealed class Resolution
    {
        public Resolution(List<string> itemIds, List<string> libraries, List<string> visible)
        {
            ItemIds = itemIds;
            Libraries = libraries;
            Visible = visible;
        }

        public List<string> ItemIds { get; }

        public List<string> Libraries { get; }

        public List<string> Visible { get; }
    }
}
