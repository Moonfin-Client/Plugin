using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for MDBList ratings.
/// Stores all ratings unfiltered, keyed by "movie:tmdbId" or "show:tmdbId".
/// The batch task populates this cache; the controller reads from it.
/// Uses stream-based JSON I/O to handle large caches without string allocation spikes.
/// </summary>
public class MdbListCacheService : FileBackedCacheService<MdbListCacheEntry>
{
    public MdbListCacheService(ILogger<MdbListCacheService> logger)
        : base(logger, "mdblist_cache.json", "MDBList")
    {
    }

    public List<MdbListRating>? TryGet(string cacheKey, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(cacheKey, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Ratings;
        }
        return null;
    }

    public void Set(string cacheKey, List<MdbListRating> ratings)
    {
        var cache = EnsureLoaded();
        cache[cacheKey] = new MdbListCacheEntry
        {
            Ratings = ratings,
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    public void SetMany(Dictionary<string, List<MdbListRating>> items)
    {
        var cache = EnsureLoaded();
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, ratings) in items)
        {
            cache[key] = new MdbListCacheEntry
            {
                Ratings = ratings,
                CachedAt = now
            };
        }
    }

    public HashSet<string> GetFreshKeys(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in cache)
        {
            if (entry.CachedAt >= cutoff)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    /// <summary>
    /// Removes entries older than <paramref name="maxAge"/>. Entries beyond the read TTL
    /// are never served, so anything well past it (removed library items, one-off
    /// on-demand lookups) is dead weight in the cache file.
    /// </summary>
    public int PruneOlderThan(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        foreach (var (key, entry) in cache)
        {
            if (entry.CachedAt < cutoff && cache.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}

public class MdbListCacheEntry
{
    [JsonPropertyName("ratings")]
    public List<MdbListRating> Ratings { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
