using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// The popular TMDB movies for each holiday, keyed "{holiday}:{year}" and shared by every
    /// user. The per-user work (owned titles out, parental limit) happens on a copy.
    /// </summary>
    public class SeasonalSuggestionsCacheService : FileBackedCache<SeasonalSuggestionsEntry>
    {
        public SeasonalSuggestionsCacheService(ILogger logger) : base(logger, "seasonal_suggestions_cache.json", "Seasonal suggestions") { }

        /// <summary>The entry, unless it is older than <paramref name="maxAge"/> or was built from fewer discover pages than asked for.</summary>
        public List<CustomRowItem>? TryGet(string key, TimeSpan maxAge, int pages)
        {
            var cache = EnsureLoaded();
            if (cache.TryGetValue(key, out var entry) && entry.Pages >= pages && DateTimeOffset.UtcNow - entry.CachedAt < maxAge) return entry.Items;
            return null;
        }

        public void Set(string key, List<CustomRowItem> items, int pages)
        {
            EnsureLoaded()[key] = new SeasonalSuggestionsEntry { Items = items, CachedAt = DateTimeOffset.UtcNow, Pages = pages };
        }
    }

    public class SeasonalSuggestionsEntry
    {
        [JsonPropertyName("items")] public List<CustomRowItem> Items { get; set; } = new List<CustomRowItem>();
        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
        [JsonPropertyName("pages")] public int Pages { get; set; }
    }
}
