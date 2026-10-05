using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// The TMDB keywords of every movie in the library, keyed by item id, so a holiday row can be
/// built on a server whose items carry no keyword tags of their own.
/// </summary>
public class SeasonalKeywordStoreService : FileBackedCacheService<SeasonalKeywordEntry>
{
    public SeasonalKeywordStoreService(ILogger<SeasonalKeywordStoreService> logger)
        : base(logger, "seasonal_keywords_cache.json", "Seasonal keywords")
    {
    }

    public SeasonalKeywordEntry? TryGet(string itemId)
    {
        return EnsureLoaded().TryGetValue(itemId, out var entry) ? entry : null;
    }

    public void Set(string itemId, long tmdbId, IEnumerable<int> keywordIds)
    {
        EnsureLoaded()[itemId] = new SeasonalKeywordEntry
        {
            TmdbId = tmdbId,
            KeywordIds = keywordIds.Distinct().ToArray(),
            FetchedAt = DateTimeOffset.UtcNow
        };
    }

    public int Count => EnsureLoaded().Count;

    /// <summary>Drops every item not in <paramref name="liveItemIds"/>, the ones gone from the library.</summary>
    public int RemoveMissing(ISet<string> liveItemIds)
    {
        var cache = EnsureLoaded();
        var removed = 0;
        foreach (var key in cache.Keys.ToList())
        {
            if (!liveItemIds.Contains(key) && cache.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    public List<string> ItemIdsWithAnyKeyword(ISet<int> keywordIds)
    {
        var matches = new List<string>();
        foreach (var (itemId, entry) in EnsureLoaded())
        {
            if (entry.KeywordIds.Any(keywordIds.Contains))
            {
                matches.Add(itemId);
            }
        }

        return matches;
    }

    /// <summary>Every TMDB id the library holds, for leaving owned titles out of the suggestions.</summary>
    public HashSet<long> AllTmdbIds()
    {
        return new HashSet<long>(EnsureLoaded().Values.Select(e => e.TmdbId));
    }
}

public class SeasonalKeywordEntry
{
    [JsonPropertyName("tmdbId")]
    public long TmdbId { get; set; }

    [JsonPropertyName("keywordIds")]
    public int[] KeywordIds { get; set; } = Array.Empty<int>();

    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }
}
