using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for fully custom home rows.
/// Keyed by source:type:paramHash.
/// </summary>
public class CustomRowCacheService : FileBackedCacheService<CustomRowCacheEntry>
{
    public CustomRowCacheService(ILogger<CustomRowCacheService> logger)
        : base(logger, "custom_rows_cache.json", "Custom rows")
    {
    }

    public List<CustomRowItem>? TryGet(string cacheKey, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(cacheKey, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }
        return null;
    }

    public void Set(string cacheKey, List<CustomRowItem> items)
    {
        var cache = EnsureLoaded();
        cache[cacheKey] = new CustomRowCacheEntry
        {
            Items = items,
            CachedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Removes entries older than <paramref name="maxAge"/> so abandoned row configs
    /// (changed lists, removed rows) don't accumulate in the cache file forever.
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

public class CustomRowCacheEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}

public class CustomRowItem
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("providerIds")]
    public CustomRowItemProviderIds ProviderIds { get; set; } = new();

    [JsonPropertyName("userRating")]
    public string? UserRating { get; set; }

    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("backdropUrl")]
    public string? BackdropUrl { get; set; }
}

public class CustomRowItemProviderIds
{
    [JsonPropertyName("Imdb")]
    public string? Imdb { get; set; }

    [JsonPropertyName("Tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("Tvdb")]
    public string? Tvdb { get; set; }
}
