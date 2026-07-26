using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    public class MdbListCacheService : FileBackedCache<MdbListCacheEntry>
    {
        public MdbListCacheService(ILogger logger) : base(logger, "mdblist_cache.json", "MDBList") { }

        public List<MdbListRating>? TryGet(string cacheKey, TimeSpan maxAge)
        {
            var cache = EnsureLoaded();
            if (cache.TryGetValue(cacheKey, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
                return entry.Ratings;
            return null;
        }

        public void Set(string cacheKey, List<MdbListRating> ratings)
        {
            var cache = EnsureLoaded();
            cache[cacheKey] = new MdbListCacheEntry { Ratings = ratings, CachedAt = DateTimeOffset.UtcNow };
        }

        public void SetMany(Dictionary<string, List<MdbListRating>> items)
        {
            var cache = EnsureLoaded();
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in items)
                cache[kvp.Key] = new MdbListCacheEntry { Ratings = kvp.Value, CachedAt = now };
        }

        public HashSet<string> GetFreshKeys(TimeSpan maxAge)
        {
            var cache = EnsureLoaded();
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in cache)
                if (kvp.Value.CachedAt >= cutoff) keys.Add(kvp.Key);
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
            foreach (var kvp in cache)
            {
                if (kvp.Value.CachedAt < cutoff && cache.TryRemove(kvp.Key, out _))
                    removed++;
            }
            return removed;
        }
    }

    public class MdbListCacheEntry
    {
        [JsonPropertyName("ratings")] public List<MdbListRating> Ratings { get; set; } = new List<MdbListRating>();
        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
    }

    public class MdbListRating
    {
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("value")] public double? Value { get; set; }
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("votes")] public int? Votes { get; set; }

        // MDBList's url field can be a string, null, a number, or a boolean. The tolerant
        // converter keeps a non-string value from failing the whole payload.
        [JsonPropertyName("url")]
        [JsonConverter(typeof(TolerantStringConverter))]
        public string? Url { get; set; }
    }

    /// <summary>
    /// Tolerant string converter that handles non-string JSON values (e.g. false, 0)
    /// by converting them to their string representation or null.
    /// </summary>
    public class TolerantStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.False:
                case JsonTokenType.True:
                    return null; // treat boolean url values as absent
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString();
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }
    }
}
