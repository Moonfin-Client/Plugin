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

    internal ImdbListsCacheService(ILogger<ImdbListsCacheService> logger, string dataPath)
        : base(logger, "imdb_lists_cache.json", "IMDb lists", dataPath)
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

    /// <summary>Every cached chart, for the pass that matches their titles to the library.</summary>
    public IEnumerable<List<CustomRowItem>> AllItemLists() =>
        EnsureLoaded().Values.Select(entry => entry.Items).ToList();

    public bool RatingsFilled(string chartType)
    {
        return EnsureLoaded().TryGetValue(chartType, out var entry) && entry.RatingsFilled;
    }

    public void MarkRatingsFilled(string chartType)
    {
        if (EnsureLoaded().TryGetValue(chartType, out var entry))
        {
            entry.RatingsFilled = true;
        }
    }

    /// <summary>
    /// Clears the backdrop of chart titles cached before they got one from TMDB, which used the
    /// poster instead, so clients treat them like any title without a backdrop.
    /// </summary>
    public static void DropPosterBackdrops(IEnumerable<CustomRowItem> items)
    {
        foreach (var item in items)
        {
            if (item.BackdropUrl != null && item.BackdropUrl == item.PosterUrl)
            {
                item.BackdropUrl = null;
            }
        }
    }
}

public class ImdbListsCacheEntry
{
    [JsonPropertyName("items")]
    public List<CustomRowItem> Items { get; set; } = new();

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }

    [JsonPropertyName("ratingsFilled")]
    public bool RatingsFilled { get; set; }
}
