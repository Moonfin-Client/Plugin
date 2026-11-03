using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Proxy controller for MDBList API requests.
/// The user's API key is stored in their settings and never exposed to the client.
/// </summary>
[ApiController]
[Route("Moonfin/MdbList")]
public class MdbListController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly MdbListCacheService _cacheService;
    private readonly MdbListListsCacheService _listsCacheService;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    // Lenient read TTL so a stale cache still serves rows rather than returning nothing.
    private static readonly TimeSpan ListsReadTtl = TimeSpan.FromDays(30);

    // Remember failed upstream lookups briefly so a burst of requests during a bad-key
    // or rate-limit spell doesn't hammer api.mdblist.com.
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Error)> _negativeCache = new();

    // Debounced disk flush for on-demand cache writes (the batch task also flushes).
    private static readonly TimeSpan FlushDebounce = TimeSpan.FromSeconds(30);
    private static DateTimeOffset _lastFlushRequest = DateTimeOffset.MinValue;

    private sealed class WatchingNowCacheEntry
    {
        public DateTimeOffset CachedAt { get; }
        public MdbListWatchingNowResponse Data { get; }

        public WatchingNowCacheEntry(DateTimeOffset cachedAt, MdbListWatchingNowResponse data)
        {
            CachedAt = cachedAt;
            Data = data;
        }
    }

    private static WatchingNowCacheEntry? _watchingNowCache;
    private static readonly TimeSpan WatchingNowCacheTtl = TimeSpan.FromMinutes(4.5);
    private static readonly SemaphoreSlim _watchingNowLock = new(1, 1);

    public MdbListController(MoonfinSettingsService settingsService, MdbListCacheService cacheService, MdbListListsCacheService listsCacheService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
        _listsCacheService = listsCacheService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Fetches ratings from MDBList for a given TMDb ID.
    /// </summary>
    [HttpGet("Ratings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MdbListResponse>> GetRatings(
        [FromQuery] string type,
        [FromQuery] string tmdbId,
        [FromQuery] string? profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(tmdbId))
        {
            return BadRequest(new { Error = "Missing required parameters: type, tmdbId" });
        }

        type = type.Trim().ToLowerInvariant();
        if (type != "movie" && type != "show")
        {
            return BadRequest(new { Error = "Invalid type. Expected: movie or show" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        // Resolving against the caller's device profile lets the sources they picked in
        // the app drive the filtering. Clients that send no profile keep the global-only
        // view they had before.
        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, NormalizeProfileName(profile));

        // The resolver already falls back to the server-wide key when the user has none.
        var apiKey = resolved?.MdblistApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new MdbListResponse
            {
                Success = false,
                Error = "No MDBList API key configured. Add your key in Moonfin Settings, or ask your server admin to set a server-wide key."
            });
        }

        var cacheKey = $"{type}:{tmdbId.Trim()}";
        var allRatings = _cacheService.TryGet(cacheKey, CacheTtl);

        if (allRatings == null)
        {
            if (_negativeCache.TryGetValue(cacheKey, out var negative))
            {
                if (DateTimeOffset.UtcNow - negative.At < NegativeCacheTtl)
                {
                    return Ok(new MdbListResponse { Success = false, Error = negative.Error });
                }

                _negativeCache.TryRemove(cacheKey, out _);
            }

            // On-demand fallback: fetch single item from MDBList
            try
            {
                var url = $"https://api.mdblist.com/tmdb/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(tmdbId.Trim())}/?apikey={Uri.EscapeDataString(apiKey)}";

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    return NegativeResult(cacheKey, "MDBList rate limit reached. Try again later.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return NegativeResult(cacheKey, $"MDBList returned status {(int)response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<MdbListApiResponse>(json, JsonOptions);

                allRatings = data?.Ratings ?? new List<MdbListRating>();
                _cacheService.Set(cacheKey, allRatings);
                RequestFlush();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return NegativeResult(cacheKey, $"Failed to fetch from MDBList: {ex.Message}");
            }
        }

        var filteredRatings = FilterAndOrderRatings(allRatings, RequestedRatingSources(resolved));

        return Ok(new MdbListResponse
        {
            Success = true,
            Ratings = filteredRatings
        });
    }

    private ActionResult<MdbListResponse> NegativeResult(string cacheKey, string error)
    {
        _negativeCache[cacheKey] = (DateTimeOffset.UtcNow, error);
        return Ok(new MdbListResponse { Success = false, Error = error });
    }

    private void RequestFlush()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastFlushRequest < FlushDebounce)
        {
            return;
        }

        _lastFlushRequest = now;
        _ = Task.Run(() => _cacheService.FlushAsync());
    }

    /// <summary>
    /// Validates an MDBList API key by proxying GET /user and returning plan/quota info.
    /// Admin-only, used by the "Test key" button on the plugin config page.
    /// Pass <paramref name="key"/> to test an unsaved key, otherwise the server-wide key is used.
    /// </summary>
    [HttpGet("KeyInfo")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MdbListKeyInfoResponse>> GetKeyInfo(
        [FromQuery] string? key,
        CancellationToken cancellationToken)
    {
        var apiKey = string.IsNullOrWhiteSpace(key) ? MoonfinPlugin.Instance?.Configuration?.MdblistApiKey : key.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new MdbListKeyInfoResponse { Success = false, Error = "No MDBList API key configured." });
        }

        try
        {
            var url = $"https://api.mdblist.com/user?apikey={Uri.EscapeDataString(apiKey)}";
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = (int)response.StatusCode == 429
                    ? "MDBList rate limit reached. Try again later."
                    : $"MDBList rejected the key (status {(int)response.StatusCode}).";
                return Ok(new MdbListKeyInfoResponse { Success = false, Error = reason });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<MdbListUserInfo>(json, JsonOptions);
            if (info == null || string.IsNullOrEmpty(info.Username))
            {
                return Ok(new MdbListKeyInfoResponse { Success = false, Error = "Unexpected response from MDBList." });
            }

            return Ok(new MdbListKeyInfoResponse
            {
                Success = true,
                Username = info.Username,
                Plan = info.Plan,
                IsSupporter = info.IsSupporter,
                ApiRequests = info.ApiRequests,
                ApiRequestsCount = info.ApiRequestsCount,
                RateLimitRemaining = info.RateLimitRemaining
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new MdbListKeyInfoResponse { Success = false, Error = $"Failed to reach MDBList: {ex.Message}" });
        }
    }

    /// <summary>
    /// Drops every cached MDBList rating so lookups refetch from the API. Entries are only
    /// judged on age, so this is the one way to retire ratings that are cached but wrong.
    /// </summary>
    [HttpPost("ClearCache")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MdbListClearCacheResponse>> ClearCache()
    {
        _negativeCache.Clear();
        var removed = await _cacheService.ClearAsync().ConfigureAwait(false);
        return Ok(new MdbListClearCacheResponse { Success = true, Removed = removed });
    }

    /// <summary>
    /// Returns the catalog of MDBList official lists available to show as home rows.
    /// Read-only from the server-side cache populated by the lists sync task.
    /// </summary>
    [HttpGet("Lists")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<MdbListCatalogResponse> GetLists()
    {
        var catalog = _listsCacheService.TryGetCatalog(ListsReadTtl);
        if (catalog == null)
        {
            return Ok(new MdbListCatalogResponse
            {
                Success = false,
                Error = "No MDBList lists cached yet. Ask your server admin to set a server-wide MDBList key and run the Moonfin MDBList Official Lists Sync task."
            });
        }

        return Ok(new MdbListCatalogResponse
        {
            Success = true,
            Lists = catalog
        });
    }

    /// <summary>
    /// Returns the cached items for a single official list, optionally filtered by media type.
    /// Official lists are combined movie + show; pass <paramref name="mediatype"/> to narrow.
    /// </summary>
    [HttpGet("Lists/{slug}/Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<MdbListItemsResponse> GetListItems(
        string slug,
        [FromQuery] string? mediatype)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return BadRequest(new { Error = "Missing required path parameter: slug" });
        }

        string? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(mediatype))
        {
            typeFilter = mediatype.Trim().ToLowerInvariant();
            if (typeFilter != "movie" && typeFilter != "show")
            {
                return BadRequest(new { Error = "Invalid mediatype. Expected: movie or show" });
            }
        }

        var items = _listsCacheService.TryGetItems(slug.Trim(), ListsReadTtl);
        if (items == null)
        {
            return Ok(new MdbListItemsResponse
            {
                Success = false,
                Slug = slug,
                Error = "This list is not cached yet. It may sync on the next run of the Moonfin MDBList Official Lists Sync task."
            });
        }

        if (typeFilter != null)
        {
            items = items.Where(i => string.Equals(i.Type, typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(new MdbListItemsResponse
        {
            Success = true,
            Slug = slug,
            Items = items
        });
    }

    /// <summary>
    /// Fetches currently watching items and site statistics from MDBList.
    /// </summary>
    [HttpGet("WatchingNow")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MdbListWatchingNowResponse>> GetWatchingNow(
        [FromQuery] string? profile,
        CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        string? apiKey = null;
        if (userId.HasValue)
        {
            var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, NormalizeProfileName(profile));
            apiKey = resolved?.MdblistApiKey;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new MdbListWatchingNowResponse
            {
                Error = "No MDBList API key configured. Add your key in Moonfin Settings, or ask your server admin to set a server-wide key."
            });
        }

        var snapshot = Volatile.Read(ref _watchingNowCache);
        if (snapshot != null && DateTimeOffset.UtcNow - snapshot.CachedAt < WatchingNowCacheTtl)
        {
            return Ok(snapshot.Data);
        }

        var negKey = $"watching_now:{apiKey.Trim()}";
        if (_negativeCache.TryGetValue(negKey, out var negative))
        {
            if (DateTimeOffset.UtcNow - negative.At < NegativeCacheTtl)
            {
                return Ok(new MdbListWatchingNowResponse { Error = negative.Error });
            }
            _negativeCache.TryRemove(negKey, out _);
        }

        await _watchingNowLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = Volatile.Read(ref _watchingNowCache);
            if (snapshot != null && DateTimeOffset.UtcNow - snapshot.CachedAt < WatchingNowCacheTtl)
            {
                return Ok(snapshot.Data);
            }

            if (_negativeCache.TryGetValue(negKey, out negative))
            {
                if (DateTimeOffset.UtcNow - negative.At < NegativeCacheTtl)
                {
                    return Ok(new MdbListWatchingNowResponse { Error = negative.Error });
                }
                _negativeCache.TryRemove(negKey, out _);
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            var url = $"https://api.mdblist.com/watching/now?apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;
                var err = $"MDBList API returned {statusCode}: {errBody}";
                if (statusCode == 429 || statusCode >= 500)
                {
                    _negativeCache[negKey] = (DateTimeOffset.UtcNow, err);
                }
                return Ok(new MdbListWatchingNowResponse
                {
                    Error = err
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<MdbListWatchingNowResponse>(json, JsonOptions);

            if (result?.Items != null && result.Items.Count > 0)
            {
                Volatile.Write(ref _watchingNowCache, new WatchingNowCacheEntry(DateTimeOffset.UtcNow, result));
                return Ok(result);
            }

            if (result != null && !string.IsNullOrWhiteSpace(result.Error))
            {
                return Ok(result);
            }

            return Ok(new MdbListWatchingNowResponse
            {
                Error = "Failed to deserialize MDBList response."
            });
        }
        catch (Exception ex)
        {
            return Ok(new MdbListWatchingNowResponse
            {
                Error = ex.Message
            });
        }
        finally
        {
            _watchingNowLock.Release();
        }
    }

    private static readonly string[] DefaultRatingSources = ["imdb", "tmdb", "tomatoes", "metacritic"];

    private static string NormalizeProfileName(string? profile)
    {
        var name = profile?.Trim().ToLowerInvariant();
        return name is "desktop" or "mobile" or "tv" ? name : "global";
    }

    // Home rows and the media bar pick their own sources and every client filters by them,
    // so the answer also carries the sources only those lists ask for.
    internal static List<string> RequestedRatingSources(MoonfinSettingsProfile? resolved)
    {
        var picked = resolved?.MdblistRatingSources;
        var sources = new List<string>(picked is { Count: > 0 } ? picked : DefaultRatingSources);
        foreach (var list in new[] { resolved?.HomeRowsMdblistRatingSources, resolved?.MediaBarMdblistRatingSources })
        {
            if (list == null) continue;
            foreach (var source in list)
            {
                if (!string.IsNullOrEmpty(source) && !sources.Exists(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase)))
                {
                    sources.Add(source);
                }
            }
        }

        return sources;
    }

    private static List<MdbListRating> FilterAndOrderRatings(List<MdbListRating> allRatings, List<string>? selectedSources)
    {
        var sources = (selectedSources is { Count: > 0 }) ? (IReadOnlyList<string>)selectedSources : DefaultRatingSources;

        var ratingsBySource = new Dictionary<string, MdbListRating>(StringComparer.OrdinalIgnoreCase);
        foreach (var rating in allRatings)
        {
            if (!string.IsNullOrEmpty(rating.Source))
            {
                ratingsBySource[rating.Source] = rating;
            }
        }

        var result = new List<MdbListRating>();
        foreach (var source in sources)
        {
            // MDBList's raw source names are imdb, metacritic, metacriticuser, trakt,
            // tomatoes, popcorn, tmdb, letterboxd, rogerebert, and myanimelist. RT
            // critics arrive as "tomatoes" and RT audience as "popcorn".
            var lookupSource = source;
            if (string.Equals(source, "rtAudience", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "tomatoes_audience", StringComparison.OrdinalIgnoreCase))
            {
                lookupSource = "popcorn";
            }

            if (ratingsBySource.TryGetValue(lookupSource, out var rating))
            {
                // Clone the rating object to prevent mutating the cached instance in memory
                var ratingClone = new MdbListRating
                {
                    Source = source,
                    Value = rating.Value,
                    Score = rating.Score,
                    Votes = rating.Votes,
                    Url = rating.Url
                };

                // Letterboxd: MDBList's value field is on an ambiguous 0-10 scale.
                // Normalize to the native 0-5 scale so all clients receive a correct value
                // without mutating the underlying cache. Keep it as a double so clients
                // can parse it cleanly and append "/5" for display.
                if (string.Equals(ratingClone.Source, "letterboxd", StringComparison.OrdinalIgnoreCase))
                {
                    if (ratingClone.Score.HasValue)
                    {
                        ratingClone.Value = Math.Round(ratingClone.Score.Value / 20.0, 1);
                    }
                    else if (ratingClone.Value.HasValue)
                    {
                        var val = ratingClone.Value.Value;
                        ratingClone.Value = val > 5.0 ? Math.Round(val / 2.0, 1) : val;
                    }
                }
                result.Add(ratingClone);
            }
        }

        return result;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
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

public class MdbListResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("ratings")]
    public List<MdbListRating> Ratings { get; set; } = new();
}

public class MdbListRating
{
    /// <summary>Rating source (e.g. imdb, tmdb, tomatoes).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Provider's native rating value.</summary>
    [JsonPropertyName("value")]
    public double? Value { get; set; }

    /// <summary>Normalized score (0-100).</summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    [JsonPropertyName("votes")]
    public int? Votes { get; set; }

    [JsonPropertyName("url")]
    [JsonConverter(typeof(TolerantStringConverter))]
    public string? Url { get; set; }
}

internal class MdbListApiResponse
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("ratings")]
    public List<MdbListRating>? Ratings { get; set; }
}

public class MdbListKeyInfoResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("plan")]
    public string? Plan { get; set; }

    [JsonPropertyName("isSupporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("apiRequests")]
    public int? ApiRequests { get; set; }

    [JsonPropertyName("apiRequestsCount")]
    public int? ApiRequestsCount { get; set; }

    [JsonPropertyName("rateLimitRemaining")]
    public int? RateLimitRemaining { get; set; }
}

public class MdbListClearCacheResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }
}

internal class MdbListUserInfo
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("plan")]
    public string? Plan { get; set; }

    [JsonPropertyName("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("api_requests")]
    public int? ApiRequests { get; set; }

    [JsonPropertyName("api_requests_count")]
    public int? ApiRequestsCount { get; set; }

    [JsonPropertyName("rate_limit_remaining")]
    public int? RateLimitRemaining { get; set; }
}

public class MdbListCatalogResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("lists")]
    public List<MdbListCatalogEntry> Lists { get; set; } = new();
}

public class MdbListCatalogEntry
{
    /// <summary>Stable slug used to fetch this list's items.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable list name (e.g. "Top Watched Movies of The Week").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Media type of the list: movie, show, or empty for combined lists.</summary>
    [JsonPropertyName("mediatype")]
    public string? Mediatype { get; set; }

    /// <summary>Number of items reported by MDBList for this list.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class MdbListItemsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<MdbListItem> Items { get; set; } = new();
}

public class MdbListItem
{
    /// <summary>TMDB id (also present in providerIds.Tmdb).</summary>
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>Title of the movie or show.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>movie or show.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Release year, when MDBList provides it.</summary>
    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    /// <summary>Rank within the list (1-based), used to preserve list order.</summary>
    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    /// <summary>TMDB poster path (relative, e.g. /abc.jpg), resolved server-side during the sync.</summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>
    /// External ids the client matches against the local library.
    /// Casing intentionally matches the client's AggregatedItem.providerIds (Imdb/Tmdb/Tvdb).
    /// </summary>
    [JsonPropertyName("providerIds")]
    public MdbListItemProviderIds ProviderIds { get; set; } = new();
}

public class MdbListItemProviderIds
{
    [JsonPropertyName("Imdb")]
    public string? Imdb { get; set; }

    [JsonPropertyName("Tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("Tvdb")]
    public string? Tvdb { get; set; }
}

public class MdbListWatchingNowResponse
{
    [JsonPropertyName("built_at")]
    public string? BuiltAt { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("users")]
    public int Users { get; set; }

    [JsonPropertyName("movies")]
    public int Movies { get; set; }

    [JsonPropertyName("shows")]
    public int Shows { get; set; }

    [JsonPropertyName("items")]
    public List<MdbListWatchingNowItem> Items { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class MdbListWatchingNowItem
{
    [JsonPropertyName("public_id")]
    public string? PublicId { get; set; }

    [JsonPropertyName("tmdb_id")]
    public long? TmdbId { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("backdrop")]
    [JsonConverter(typeof(TolerantStringConverter))]
    public string? Backdrop { get; set; }

    [JsonPropertyName("logo")]
    [JsonConverter(typeof(TolerantStringConverter))]
    public string? Logo { get; set; }

    [JsonPropertyName("watchers")]
    public int Watchers { get; set; }
}
