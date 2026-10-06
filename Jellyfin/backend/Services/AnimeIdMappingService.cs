using System.Net.Http;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Downloads and caches a table mapping anime provider ids to MyAnimeList ids, so the recap
/// pass can resolve a show without going to the network. The table comes from
/// Fribb/anime-lists, a community-maintained mirror of AniList's own mapping.
/// </summary>
public class AnimeIdMappingService
{
    private const string MappingUrl =
        "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json";

    private const string MappingFileName = "anime_id_mapping.json";

    private static readonly TimeSpan MappingMaxAge = TimeSpan.FromDays(14);

    /// <summary>
    /// The table wont be downloaded more often than this, even when the copy on disk is
    /// stale, so a failed download doesnt hammer GitHub with retries.
    /// </summary>
    private static readonly TimeSpan DownloadRetryInterval = TimeSpan.FromHours(6);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnimeIdMappingService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly string _mappingPath;

    private MappingIndex? _index;
    private DateTime? _indexWrittenAt;
    private DateTimeOffset _lastDownloadAttempt = DateTimeOffset.MinValue;

    public AnimeIdMappingService(IHttpClientFactory httpClientFactory, ILogger<AnimeIdMappingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _mappingPath = Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), MappingFileName);
    }

    /// <summary>
    /// Loads the table, downloading it only when the copy on disk is missing or stale. A
    /// failed download keeps whatever is already there.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_index != null && !IsStale())
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index != null && !IsStale())
            {
                return;
            }

            if (IsStale() && DateTimeOffset.UtcNow - _lastDownloadAttempt > DownloadRetryInterval)
            {
                _lastDownloadAttempt = DateTimeOffset.UtcNow;
                await DownloadAsync(cancellationToken).ConfigureAwait(false);
            }

            // Only reparse when the file on disk has actually changed. A stale copy that
            // cant be refreshed otherwise gets deserialised again on every single lookup.
            if (File.Exists(_mappingPath))
            {
                var writtenAt = File.GetLastWriteTimeUtc(_mappingPath);
                if (_index == null || _indexWrittenAt != writtenAt)
                {
                    _index = Parse(_mappingPath);
                    _indexWrittenAt = writtenAt;
                    _logger.LogInformation(
                        "Anime id mapping loaded ({Count} entries with a MyAnimeList id)", _index.Count);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anime id mapping could not be loaded");
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool IsStale() =>
        !File.Exists(_mappingPath) ||
        DateTime.UtcNow - File.GetLastWriteTimeUtc(_mappingPath) > MappingMaxAge;

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client
                .GetAsync(MappingUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Anime id mapping download returned {Status}, keeping any existing copy",
                    (int)response.StatusCode);
                return;
            }

            var tempPath = _mappingPath + ".tmp";
            var directory = Path.GetDirectoryName(_mappingPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _mappingPath, overwrite: true);
            _logger.LogInformation("Anime id mapping downloaded from Fribb/anime-lists");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anime id mapping download failed, keeping any existing copy");
        }
    }

    /// <summary>
    /// Loads a table from a given file, so tests can check the parsing without a download.
    /// </summary>
    internal void LoadFrom(string path) => _index = Parse(path);

    private static MappingIndex Parse(string path)
    {
        var index = new MappingIndex();

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var malId = ReadInt(entry, "mal_id");
            if (malId == null)
            {
                continue;
            }

            index.Count++;

            Add(index.ByAniList, ReadInt(entry, "anilist_id"), malId.Value);
            Add(index.ByAniDb, ReadInt(entry, "anidb_id"), malId.Value);
            Add(index.ByAniSearch, ReadInt(entry, "anisearch_id"), malId.Value);
            Add(index.ByKitsu, ReadInt(entry, "kitsu_id"), malId.Value);
            // The table lists IMDb ids as an array, since one entry can have several titles.
            foreach (var imdbId in ReadStrings(entry, "imdb_id"))
            {
                Add(index.ByImdb, imdbId, malId.Value);
            }

            // One TVDB or TMDB id covers a whole show, while MyAnimeList splits it per
            // season, so the first entry wins. That is the earliest season, and the recap
            // pass only applies numbers inside the entry's own episode count anyway.
            Add(index.ByTvdb, ReadInt(entry, "tvdb_id"), malId.Value);

            if (entry.TryGetProperty("themoviedb_id", out var tmdb) && tmdb.ValueKind == JsonValueKind.Object)
            {
                Add(index.ByTmdb, ReadInt(tmdb, "tv"), malId.Value);
            }
        }

        return index;
    }

    /// <summary>
    /// Resolves one provider id, given by the canonical names <see cref="AnimeTitleAliasService"/>
    /// uses ("anidb", "tvdb", ...), to a MyAnimeList id. Returns null when it isnt in the table.
    /// </summary>
    public int? ResolveProvider(string provider, string value)
    {
        var index = _index;
        if (index == null || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (provider == "imdb")
        {
            return index.ByImdb.TryGetValue(trimmed, out var fromImdb) ? fromImdb : null;
        }

        if (!int.TryParse(trimmed, out var id))
        {
            return null;
        }

        var map = provider switch
        {
            "anilist" => index.ByAniList,
            "anidb" => index.ByAniDb,
            "anisearch" => index.ByAniSearch,
            "kitsu" => index.ByKitsu,
            "tvdb" => index.ByTvdb,
            "tmdb" => index.ByTmdb,
            _ => null
        };

        return map != null && map.TryGetValue(id, out var malId) ? malId : null;
    }

    /// <summary>
    /// Resolves a MyAnimeList id for a series, using the provider ids the library already has. Returns null if no mapping is found.
    /// </summary>
    public int? Resolve(BaseItem series)
    {
        var index = _index;
        if (index == null)
        {
            return null;
        }

        if (TryGetInt(series, out var anilist, "AniList", "Anilist") &&
            index.ByAniList.TryGetValue(anilist, out var fromAniList))
        {
            return fromAniList;
        }

        if (TryGetInt(series, out var anidb, "AniDB", "AniDb", "Anidb") &&
            index.ByAniDb.TryGetValue(anidb, out var fromAniDb))
        {
            return fromAniDb;
        }

        if (TryGetInt(series, out var anisearch, "AniSearch", "Anisearch") &&
            index.ByAniSearch.TryGetValue(anisearch, out var fromAniSearch))
        {
            return fromAniSearch;
        }

        if (TryGetInt(series, out var kitsu, "Kitsu", "KitsuIo") &&
            index.ByKitsu.TryGetValue(kitsu, out var fromKitsu))
        {
            return fromKitsu;
        }

        if (TryGetInt(series, out var tvdb, "Tvdb", "TheTVDB") &&
            index.ByTvdb.TryGetValue(tvdb, out var fromTvdb))
        {
            return fromTvdb;
        }

        if (TryGetInt(series, out var tmdb, "Tmdb", "TheMovieDb") &&
            index.ByTmdb.TryGetValue(tmdb, out var fromTmdb))
        {
            return fromTmdb;
        }

        foreach (var (key, value) in series.ProviderIds)
        {
            if (string.Equals(key, "Imdb", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(value) &&
                index.ByImdb.TryGetValue(value.Trim(), out var fromImdb))
            {
                return fromImdb;
            }
        }

        return null;
    }

    private static void Add(Dictionary<int, int> map, int? key, int malId)
    {
        if (key != null)
        {
            map.TryAdd(key.Value, malId);
        }
    }

    private static void Add(Dictionary<string, int> map, string? key, int malId)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            map.TryAdd(key.Trim(), malId);
        }
    }

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Reads a property that is either one string or an array of them.
    /// </summary>
    private static IEnumerable<string?> ReadStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            yield break;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            yield return value.GetString();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    yield return item.GetString();
                }
            }
        }
    }

    private static bool TryGetInt(BaseItem series, out int value, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (providerKey, providerValue) in series.ProviderIds)
            {
                if (string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(providerValue?.Trim(), out value))
                {
                    return true;
                }
            }
        }

        value = 0;
        return false;
    }

    private class MappingIndex
    {
        public int Count { get; set; }
        public Dictionary<int, int> ByAniList { get; } = new();
        public Dictionary<int, int> ByAniDb { get; } = new();
        public Dictionary<int, int> ByAniSearch { get; } = new();
        public Dictionary<int, int> ByKitsu { get; } = new();
        public Dictionary<int, int> ByTvdb { get; } = new();
        public Dictionary<int, int> ByTmdb { get; } = new();
        public Dictionary<string, int> ByImdb { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
