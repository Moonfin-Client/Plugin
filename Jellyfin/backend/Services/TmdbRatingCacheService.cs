using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Certifications looked up on TMDB, keyed "movie:{tmdbId}" and "tv:{tmdbId}", plus the
/// TMDB title an IMDb id resolved to under "imdb:{imdbId}". A title TMDB has no rating for
/// is kept too, so it isn't asked about again for a month.
/// </summary>
public class TmdbRatingCacheService : FileBackedCacheService<TmdbRatingEntry>
{
    public TmdbRatingCacheService(ILogger<TmdbRatingCacheService> logger)
        : base(logger, "tmdb_ratings_cache.json", "TMDB ratings")
    {
    }

    public TmdbRatingEntry? TryGet(string key, TimeSpan maxAge)
    {
        var cache = EnsureLoaded();
        if (cache.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.CachedAt < maxAge)
        {
            return entry;
        }

        return null;
    }

    public void Set(string key, TmdbRatingEntry entry)
    {
        entry.CachedAt = DateTimeOffset.UtcNow;
        EnsureLoaded()[key] = entry;
    }
}

public class TmdbRatingEntry
{
    [JsonPropertyName("certification")]
    public string? Certification { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("tmdbId")]
    public long? TmdbId { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
