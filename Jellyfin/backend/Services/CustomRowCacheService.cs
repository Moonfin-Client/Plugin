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

    internal CustomRowCacheService(ILogger<CustomRowCacheService> logger, string dataPath)
        : base(logger, "custom_rows_cache.json", "Custom rows", dataPath)
    {
    }

    // An entry written before rows carried ratings counts as expired, so it's fetched once more.
    public List<CustomRowItem>? TryGet(string cacheKey, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(cacheKey, out var entry) &&
            entry.RatingsFilled &&
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
            CachedAt = DateTimeOffset.UtcNow,
            RatingsFilled = true
        };
    }

    /// <summary>Every cached row, for the pass that matches their titles to the library.</summary>
    public IEnumerable<List<CustomRowItem>> AllItemLists() =>
        EnsureLoaded().Values.Select(entry => entry.Items).ToList();

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

    [JsonPropertyName("ratingsFilled")]
    public bool RatingsFilled { get; set; }
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

    /// <summary>The certification for the server's country, looked up on TMDB. Null when unknown.</summary>
    [JsonPropertyName("officialRating")]
    public string? OfficialRating { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("backdropUrl")]
    public string? BackdropUrl { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }

    [JsonPropertyName("runTimeTicks")]
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// The library item this entry is, in Jellyfin's "N" id form, or null when the library
    /// lacks it. The cache keeps the match for every user, so the endpoint only hands it to
    /// clients that ask for it and only when the user can see that item.
    /// </summary>
    [JsonPropertyName("localItemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalItemId { get; set; }

    /// <summary>
    /// A copy carrying <paramref name="localItemId"/>, or this item when it already does. The
    /// cached items are shared by every request, so the endpoint never changes them in place.
    /// </summary>
    public CustomRowItem WithLocalItemId(string? localItemId)
    {
        if (LocalItemId == localItemId)
        {
            return this;
        }

        var copy = (CustomRowItem)MemberwiseClone();
        copy.LocalItemId = localItemId;
        return copy;
    }
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
