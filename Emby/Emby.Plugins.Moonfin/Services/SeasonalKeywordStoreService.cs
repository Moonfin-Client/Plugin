using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// The TMDB keywords of every movie in the library, keyed by item id. Emby keeps no keyword
    /// tags of its own, so this is how the seasonal row finds holiday titles here.
    /// </summary>
    public class SeasonalKeywordStoreService : FileBackedCache<SeasonalKeywordEntry>
    {
        public SeasonalKeywordStoreService(ILogger logger) : base(logger, "seasonal_keywords_cache.json", "Seasonal keywords") { }

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
                if (!liveItemIds.Contains(key) && cache.TryRemove(key, out _)) removed++;
            }
            return removed;
        }

        public List<long> TmdbIdsWithAnyKeyword(ISet<int> keywordIds)
        {
            var matches = new List<long>();
            foreach (var entry in EnsureLoaded().Values)
            {
                if (entry.KeywordIds.Any(keywordIds.Contains)) matches.Add(entry.TmdbId);
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
        [JsonPropertyName("tmdbId")] public long TmdbId { get; set; }
        [JsonPropertyName("keywordIds")] public int[] KeywordIds { get; set; } = Array.Empty<int>();
        [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; set; }
    }
}
