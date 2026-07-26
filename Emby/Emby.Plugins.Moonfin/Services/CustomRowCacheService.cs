using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// File-backed cache for fully custom home rows, keyed by "source:type:paramHash".
    /// Mirrors <see cref="ImdbListsCacheService"/>.
    /// </summary>
    public class CustomRowCacheService : FileBackedCache<CustomRowCacheEntry>
    {
        public CustomRowCacheService(ILogger logger) : base(logger, "custom_rows_cache.json", "Custom rows") { }

        public List<CustomRowItem>? TryGet(string cacheKey, TimeSpan maxAge)
        {
            var cache = EnsureLoaded();
            if (cache.TryGetValue(cacheKey, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
                return entry.Items;
            return null;
        }

        public void Set(string cacheKey, List<CustomRowItem> items)
        {
            var cache = EnsureLoaded();
            cache[cacheKey] = new CustomRowCacheEntry { Items = items, CachedAt = DateTimeOffset.UtcNow };
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
            foreach (var kvp in cache)
            {
                if (kvp.Value.CachedAt < cutoff && cache.TryRemove(kvp.Key, out _))
                    removed++;
            }
            return removed;
        }
    }

    public class CustomRowCacheEntry
    {
        [JsonPropertyName("items")] public List<CustomRowItem> Items { get; set; } = new List<CustomRowItem>();
        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
    }
}
