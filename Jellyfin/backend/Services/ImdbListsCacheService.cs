using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persistent file-backed cache for IMDb official lists.
/// The lists sync task populates this cache; the custom rows controller reads from it.
/// </summary>
public class ImdbListsCacheService : FileBackedCacheService<ImdbListsCacheEntry>
{
    public ImdbListsCacheService(ILogger<ImdbListsCacheService> logger)
        : base(logger, "imdb_lists_cache.json", "IMDb lists")
    {
    }

    public List<CustomRowItem>? TryGetItems(string chartType, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(chartType, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry.Items;
        }
        return null;
    }

    public void SetItems(string chartType, List<CustomRowItem> items)
    {
        var cache = EnsureLoaded();
        cache[chartType] = new ImdbListsCacheEntry
        {
            Items = items,
            CachedAt = DateTimeOffset.UtcNow
        };
    }
}

public class ImdbListsCacheEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
