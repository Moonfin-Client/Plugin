using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// File-backed cache for IMDb charts, keyed by chart key. The IMDb lists sync task
    /// populates it. The custom-rows API reads from it. Mirrors <see cref="MdbListListsCacheService"/>.
    /// </summary>
    public class ImdbListsCacheService : FileBackedCache<ImdbListsCacheEntry>
    {
        public ImdbListsCacheService(ILogger logger) : base(logger, "imdb_lists_cache.json", "IMDb lists") { }

        public List<CustomRowItem>? TryGetItems(string chartType, TimeSpan maxAge)
        {
            var cache = EnsureLoaded();
            if (cache.TryGetValue(chartType, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
                return entry.Items;
            return null;
        }

        public void SetItems(string chartType, List<CustomRowItem> items)
        {
            var cache = EnsureLoaded();
            cache[chartType] = new ImdbListsCacheEntry { Items = items, CachedAt = DateTimeOffset.UtcNow };
        }

        public bool RatingsFilled(string chartType)
        {
            return EnsureLoaded().TryGetValue(chartType, out var entry) && entry.RatingsFilled;
        }

        public void MarkRatingsFilled(string chartType)
        {
            if (EnsureLoaded().TryGetValue(chartType, out var entry)) entry.RatingsFilled = true;
        }
    }

    public class ImdbListsCacheEntry
    {
        [JsonPropertyName("items")] public List<CustomRowItem> Items { get; set; } = new List<CustomRowItem>();
        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
        [JsonPropertyName("ratingsFilled")] public bool RatingsFilled { get; set; }
    }
}
