using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for MDBList official lists.
/// Holds the list catalog under the key "catalog" and each list's items under "items:{slug}".
/// The lists sync task populates this cache; the controller reads from it.
/// Mirrors <see cref="MdbListCacheService"/> (the ratings cache) in structure and locking.
/// </summary>
public class MdbListListsCacheService : FileBackedCacheService<MdbListListsCacheEntry>
{
    private const string CatalogKey = "catalog";

    public MdbListListsCacheService(ILogger<MdbListListsCacheService> logger)
        : base(logger, "mdblist_lists_cache.json", "MDBList lists")
    {
    }

    public List<MdbListCatalogEntry>? TryGetCatalog(TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(CatalogKey, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Catalog;
        }
        return null;
    }

    public void SetCatalog(List<MdbListCatalogEntry> catalog)
    {
        var cache = EnsureLoaded();
        cache[CatalogKey] = new MdbListListsCacheEntry
        {
            Catalog = catalog,
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    public List<MdbListItem>? TryGetItems(string slug, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(ItemsKey(slug), out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }
        return null;
    }

    public void SetItems(string slug, List<MdbListItem> items)
    {
        var cache = EnsureLoaded();
        cache[ItemsKey(slug)] = new MdbListListsCacheEntry
        {
            Items = items,
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    private static string ItemsKey(string slug) => $"items:{slug}";

    /// <summary>Age of the cached catalog, or null when nothing is cached yet.</summary>
    public TimeSpan? GetCatalogAge()
    {
        var cache = EnsureLoaded();
        return cache.TryGetValue(CatalogKey, out var entry)
            ? DateTimeOffset.UtcNow - entry.CachedAt
            : null;
    }

    /// <summary>
    /// Drops items entries for lists no longer present in the current catalog so
    /// delisted charts don't accumulate in the cache file forever.
    /// </summary>
    public int PruneItemsNotIn(IReadOnlyCollection<string> currentSlugs)
    {
        var cache = EnsureLoaded();
        var keep = new HashSet<string>(currentSlugs.Select(ItemsKey), StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var key in cache.Keys.Where(k => k.StartsWith("items:", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!keep.Contains(key) && cache.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Returns already-resolved posters from the current cache, keyed by "{type}:{tmdbId}",
    /// so the sync only calls TMDB for ids it has not resolved before.
    /// </summary>
    public Dictionary<string, string> GetKnownPosters()
    {
        var cache = EnsureLoaded();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in cache.Values)
        {
            if (entry.Items == null) continue;
            foreach (var item in entry.Items)
            {
                var tmdb = item.ProviderIds?.Tmdb;
                if (string.IsNullOrEmpty(tmdb) || string.IsNullOrEmpty(item.Poster)) continue;
                var key = item.Type + ":" + tmdb;
                if (!result.ContainsKey(key)) result[key] = item.Poster!;
            }
        }
        return result;
    }
}

public class MdbListListsCacheEntry
{
    [JsonPropertyName("catalog")]
    public List<MdbListCatalogEntry>? Catalog { get; set; }

    [JsonPropertyName("items")]
    public List<MdbListItem>? Items { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
