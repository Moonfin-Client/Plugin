using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Looks up a title's certification on TMDB for the server's metadata country, so rows built
/// from outside lists can carry an OfficialRating like library items do. Movies use the
/// release dates, shows the content ratings, and an IMDb-only title is first resolved to its
/// TMDB id.
/// </summary>
public class TmdbRatingService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);
    private const int Parallelism = 6;

    // Theatrical first, then limited, digital, physical, TV and premiere, since a festival
    // premiere often carries no rating at all.
    private static readonly int[] ReleaseTypePriority = [3, 2, 4, 5, 6, 1];

    private readonly TmdbRatingCacheService _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<TmdbRatingService> _logger;

    public TmdbRatingService(
        TmdbRatingCacheService cache,
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager configManager,
        ILogger<TmdbRatingService> logger)
    {
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>The country whose certifications are used, the one the server fetches metadata for.</summary>
    public string RatingCountry
    {
        get
        {
            var country = _configManager.Configuration.MetadataCountryCode;
            return string.IsNullOrWhiteSpace(country) ? "US" : country.Trim().ToUpperInvariant();
        }
    }

    /// <summary>
    /// Fills OfficialRating on every item that has none. Nothing happens without a key, and a
    /// title whose lookup fails is left alone for the next pass.
    /// </summary>
    public async Task FillRatingsAsync(IReadOnlyList<CustomRowItem> items, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        var pending = items.Where(i => string.IsNullOrEmpty(i.OfficialRating)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(Parallelism);
        await Task.WhenAll(pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var certification = await ResolveCertificationAsync(item, apiKey, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(certification))
                {
                    item.OfficialRating = certification;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not look up a rating for {Name}", item.Name);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        await _cache.FlushAsync().ConfigureAwait(false);
    }

    public async Task<string?> GetCertificationAsync(string mediaType, long tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        var key = $"{mediaType}:{tmdbId}";
        var cached = _cache.TryGet(key, CacheTtl);
        if (cached != null)
        {
            return cached.Certification;
        }

        var certification = mediaType == "tv"
            ? await FetchTvRatingAsync(tmdbId, apiKey, cancellationToken).ConfigureAwait(false)
            : await FetchMovieCertificationAsync(tmdbId, apiKey, cancellationToken).ConfigureAwait(false);
        _cache.Set(key, new TmdbRatingEntry { Certification = certification, MediaType = mediaType, TmdbId = tmdbId });
        return certification;
    }

    /// <summary>The TMDB movie or show an IMDb id belongs to, or null when TMDB doesn't know it.</summary>
    public async Task<(string MediaType, long TmdbId)?> FindByImdbAsync(string imdbId, string apiKey, CancellationToken cancellationToken)
    {
        var key = $"imdb:{imdbId}";
        var cached = _cache.TryGet(key, CacheTtl);
        if (cached != null)
        {
            return cached.TmdbId.HasValue && cached.MediaType != null ? (cached.MediaType, cached.TmdbId.Value) : null;
        }

        using var doc = await GetJsonAsync($"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", apiKey, cancellationToken).ConfigureAwait(false);
        (string MediaType, long TmdbId)? found = null;
        if (doc != null)
        {
            found = FirstId(doc.RootElement, "movie_results", "movie") ?? FirstId(doc.RootElement, "tv_results", "tv");
        }

        _cache.Set(key, new TmdbRatingEntry { MediaType = found?.MediaType, TmdbId = found?.TmdbId });
        return found;
    }

    private async Task<string?> ResolveCertificationAsync(CustomRowItem item, string apiKey, CancellationToken cancellationToken)
    {
        var mediaType = string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
        long? tmdbId = long.TryParse(item.ProviderIds?.Tmdb, out var parsed) ? parsed : null;
        if (tmdbId == null && !string.IsNullOrWhiteSpace(item.ProviderIds?.Imdb))
        {
            var found = await FindByImdbAsync(item.ProviderIds.Imdb, apiKey, cancellationToken).ConfigureAwait(false);
            if (found != null)
            {
                mediaType = found.Value.MediaType;
                tmdbId = found.Value.TmdbId;
            }
        }

        return tmdbId == null
            ? null
            : await GetCertificationAsync(mediaType, tmdbId.Value, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FetchMovieCertificationAsync(long tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"https://api.themoviedb.org/3/movie/{tmdbId}/release_dates", apiKey, cancellationToken).ConfigureAwait(false);
        if (doc == null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return PickMovieCertification(results, RatingCountry) ?? (RatingCountry == "US" ? null : PickMovieCertification(results, "US"));
    }

    private static string? PickMovieCertification(JsonElement results, string country)
    {
        foreach (var entry in results.EnumerateArray())
        {
            if (!entry.TryGetProperty("iso_3166_1", out var iso) || !string.Equals(iso.GetString(), country, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.TryGetProperty("release_dates", out var releases) || releases.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var byType = new Dictionary<int, string>();
            foreach (var release in releases.EnumerateArray())
            {
                var certification = release.TryGetProperty("certification", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(certification))
                {
                    continue;
                }

                var type = release.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                byType.TryAdd(type, certification.Trim());
            }

            foreach (var type in ReleaseTypePriority)
            {
                if (byType.TryGetValue(type, out var certification))
                {
                    return certification;
                }
            }

            return byType.Values.FirstOrDefault();
        }

        return null;
    }

    private async Task<string?> FetchTvRatingAsync(long tmdbId, string apiKey, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync($"https://api.themoviedb.org/3/tv/{tmdbId}/content_ratings", apiKey, cancellationToken).ConfigureAwait(false);
        if (doc == null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return PickTvRating(results, RatingCountry) ?? (RatingCountry == "US" ? null : PickTvRating(results, "US"));
    }

    private static string? PickTvRating(JsonElement results, string country)
    {
        foreach (var entry in results.EnumerateArray())
        {
            if (entry.TryGetProperty("iso_3166_1", out var iso) &&
                string.Equals(iso.GetString(), country, StringComparison.OrdinalIgnoreCase) &&
                entry.TryGetProperty("rating", out var rating))
            {
                var value = rating.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static (string MediaType, long TmdbId)? FirstId(JsonElement root, string property, string mediaType)
    {
        if (root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    return (mediaType, id.GetInt64());
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One TMDB GET. A 404 is a title TMDB doesn't have, so it answers null and gets cached as
    /// such. A 429 waits out the Retry-After once. Anything else throws, so nothing is cached.
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("MoonfinHttpClient");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(request, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(json);
        }

        throw new HttpRequestException("TMDB kept answering 429 for " + url);
    }
}
