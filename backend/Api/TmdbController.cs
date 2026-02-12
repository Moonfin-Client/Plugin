using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Proxy controller for TMDB API requests.
/// Provides episode-level ratings that MDBList doesn't cover.
/// The user's API key is stored in their settings and never exposed to the client.
/// </summary>
[ApiController]
[Route("Moonfin/Tmdb")]
public class TmdbController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;

    // Cache: key = "tmdbId:season" => (response, timestamp)
    private static readonly ConcurrentDictionary<string, (TmdbSeasonRatingsResponse Response, DateTimeOffset CachedAt)> _seasonCache = new();
    // Cache: key = "tmdbId:season:episode" => (response, timestamp)
    private static readonly ConcurrentDictionary<string, (TmdbEpisodeRatingResponse Response, DateTimeOffset CachedAt)> _episodeCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public TmdbController(MoonfinSettingsService settingsService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Fetches episode rating from TMDB for a single episode.
    /// Uses the authenticated user's TMDB API key from their settings.
    /// </summary>
    /// <param name="tmdbId">TMDB series ID.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("EpisodeRating")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TmdbEpisodeRatingResponse>> GetEpisodeRating(
        [FromQuery] string tmdbId,
        [FromQuery] int season,
        [FromQuery] int episode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return BadRequest(new { Error = "Missing required parameter: tmdbId" });
        }

        var apiKey = await GetUserApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new TmdbEpisodeRatingResponse
            {
                Success = false,
                Error = "No TMDB API key configured. Add your key in Moonfin Settings."
            });
        }

        var episodeCacheKey = $"{tmdbId.Trim()}:{season}:{episode}";
        if (_episodeCache.TryGetValue(episodeCacheKey, out var cachedEp) && DateTimeOffset.UtcNow - cachedEp.CachedAt < CacheTtl)
        {
            return Ok(cachedEp.Response);
        }

        try
        {
            var url = $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(tmdbId.Trim())}/season/{season}/episode/{episode}";
            var client = CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                return Ok(new TmdbEpisodeRatingResponse
                {
                    Success = false,
                    Error = "TMDB rate limit reached. Try again later."
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new TmdbEpisodeRatingResponse
                {
                    Success = false,
                    Error = $"TMDB returned status {(int)response.StatusCode}"
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<TmdbEpisodeApiResponse>(json, JsonOptions);

            var result = new TmdbEpisodeRatingResponse
            {
                Success = true,
                VoteAverage = data?.VoteAverage,
                VoteCount = data?.VoteCount,
                Name = data?.Name,
                AirDate = data?.AirDate,
                SeasonNumber = data?.SeasonNumber,
                EpisodeNumber = data?.EpisodeNumber,
                StillPath = data?.StillPath
            };

            _episodeCache[episodeCacheKey] = (result, DateTimeOffset.UtcNow);

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new TmdbEpisodeRatingResponse
            {
                Success = false,
                Error = $"Failed to fetch from TMDB: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Fetches all episode ratings for an entire season from TMDB.
    /// More efficient than fetching individual episodes — returns all at once.
    /// </summary>
    /// <param name="tmdbId">TMDB series ID.</param>
    /// <param name="season">Season number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("SeasonRatings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TmdbSeasonRatingsResponse>> GetSeasonRatings(
        [FromQuery] string tmdbId,
        [FromQuery] int season,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return BadRequest(new { Error = "Missing required parameter: tmdbId" });
        }

        var apiKey = await GetUserApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new TmdbSeasonRatingsResponse
            {
                Success = false,
                Error = "No TMDB API key configured. Add your key in Moonfin Settings."
            });
        }

        var seasonCacheKey = $"{tmdbId.Trim()}:{season}";
        if (_seasonCache.TryGetValue(seasonCacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheTtl)
        {
            return Ok(cached.Response);
        }

        try
        {
            var url = $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(tmdbId.Trim())}/season/{season}";
            var client = CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, apiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                return Ok(new TmdbSeasonRatingsResponse
                {
                    Success = false,
                    Error = "TMDB rate limit reached. Try again later."
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new TmdbSeasonRatingsResponse
                {
                    Success = false,
                    Error = $"TMDB returned status {(int)response.StatusCode}"
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<TmdbSeasonApiResponse>(json, JsonOptions);

            var episodes = new List<TmdbEpisodeRatingResponse>();
            if (data?.Episodes != null)
            {
                foreach (var ep in data.Episodes)
                {
                    var epResult = new TmdbEpisodeRatingResponse
                    {
                        Success = true,
                        VoteAverage = ep.VoteAverage,
                        VoteCount = ep.VoteCount,
                        Name = ep.Name,
                        AirDate = ep.AirDate,
                        SeasonNumber = ep.SeasonNumber,
                        EpisodeNumber = ep.EpisodeNumber,
                        StillPath = ep.StillPath
                    };
                    episodes.Add(epResult);

                    if (ep.EpisodeNumber.HasValue)
                    {
                        var epCacheKey = $"{tmdbId.Trim()}:{season}:{ep.EpisodeNumber.Value}";
                        _episodeCache[epCacheKey] = (epResult, DateTimeOffset.UtcNow);
                    }
                }
            }

            var result = new TmdbSeasonRatingsResponse
            {
                Success = true,
                SeasonName = data?.Name,
                Episodes = episodes
            };

            _seasonCache[seasonCacheKey] = (result, DateTimeOffset.UtcNow);

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new TmdbSeasonRatingsResponse
            {
                Success = false,
                Error = $"Failed to fetch from TMDB: {ex.Message}"
            });
        }
    }

    private async Task<string?> GetUserApiKey()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null) return null;

        var userSettings = await _settingsService.GetUserSettingsAsync(userId.Value);
        return userSettings?.TmdbApiKey;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");
        return client;
    }

    /// <summary>
    /// Applies TMDB authentication. Supports both API key (v3) and Bearer token (v4).
    /// Keys starting with "eyJ" are treated as Bearer tokens.
    /// </summary>
    private static void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            var uriBuilder = new UriBuilder(request.RequestUri!);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["api_key"] = apiKey;
            uriBuilder.Query = query.ToString();
            request.RequestUri = uriBuilder.Uri;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

// ===== Response Models =====

/// <summary>
/// Single episode rating response returned to the client.
/// </summary>
public class TmdbEpisodeRatingResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("voteAverage")]
    public float? VoteAverage { get; set; }

    [JsonPropertyName("voteCount")]
    public int? VoteCount { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("airDate")]
    public string? AirDate { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>TMDB still image path (e.g. /abcdef.jpg).</summary>
    [JsonPropertyName("stillPath")]
    public string? StillPath { get; set; }
}

/// <summary>
/// Season ratings response with all episodes.
/// </summary>
public class TmdbSeasonRatingsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("seasonName")]
    public string? SeasonName { get; set; }

    [JsonPropertyName("episodes")]
    public List<TmdbEpisodeRatingResponse> Episodes { get; set; } = new();
}

// ===== Raw TMDB API Models =====

internal class TmdbEpisodeApiResponse
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("vote_average")]
    public float? VoteAverage { get; set; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; set; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }
}

internal class TmdbSeasonApiResponse
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("episodes")]
    public List<TmdbEpisodeApiResponse>? Episodes { get; set; }
}
