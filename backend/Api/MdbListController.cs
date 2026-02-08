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
/// Proxy controller for MDBList API requests.
/// The user's API key is stored in their settings and never exposed to the client.
/// </summary>
[ApiController]
[Route("Moonfin/MdbList")]
public class MdbListController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;

    // Simple in-memory cache: key = "type:tmdbId", value = (response, timestamp)
    private static readonly ConcurrentDictionary<string, (MdbListResponse Response, DateTimeOffset CachedAt)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public MdbListController(MoonfinSettingsService settingsService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Fetches ratings from MDBList for a given TMDb ID.
    /// Uses the authenticated user's API key from their settings.
    /// </summary>
    /// <param name="type">Content type: movie or show.</param>
    /// <param name="tmdbId">TMDb ID of the item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("Ratings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<MdbListResponse>> GetRatings(
        [FromQuery] string type,
        [FromQuery] string tmdbId,
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

        // Get user's API key from their settings
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        var userSettings = await _settingsService.GetUserSettingsAsync(userId.Value);
        var apiKey = userSettings?.MdblistApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new MdbListResponse
            {
                Success = false,
                Error = "No MDBList API key configured. Add your key in Moonfin Settings."
            });
        }

        // Check cache
        var cacheKey = $"{type}:{tmdbId.Trim()}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheTtl)
        {
            return Ok(cached.Response);
        }

        // Fetch from MDBList
        try
        {
            var url = $"https://api.mdblist.com/{Uri.EscapeDataString("tmdb")}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(tmdbId.Trim())}?apikey={Uri.EscapeDataString(apiKey)}";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
            {
                return Ok(new MdbListResponse
                {
                    Success = false,
                    Error = "MDBList rate limit reached. Try again later."
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                return Ok(new MdbListResponse
                {
                    Success = false,
                    Error = $"MDBList returned status {(int)response.StatusCode}"
                });
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<MdbListApiResponse>(json, JsonOptions);

            var result = new MdbListResponse
            {
                Success = true,
                Ratings = data?.Ratings ?? new List<MdbListRating>()
            };

            // Cache the result
            _cache[cacheKey] = (result, DateTimeOffset.UtcNow);

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Ok(new MdbListResponse
            {
                Success = false,
                Error = $"Failed to fetch from MDBList: {ex.Message}"
            });
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

// ===== Converters =====

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

// ===== Models =====

/// <summary>
/// Response returned to the client.
/// </summary>
public class MdbListResponse
{
    /// <summary>Whether the request was successful.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Error message if the request failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Array of ratings from different sources.</summary>
    [JsonPropertyName("ratings")]
    public List<MdbListRating> Ratings { get; set; } = new();
}

/// <summary>
/// A single rating from MDBList.
/// </summary>
public class MdbListRating
{
    /// <summary>Rating source name (e.g. imdb, tmdb, tomatoes).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>Provider's native rating value.</summary>
    [JsonPropertyName("value")]
    public double? Value { get; set; }

    /// <summary>Normalized score (0-100).</summary>
    [JsonPropertyName("score")]
    public double? Score { get; set; }

    /// <summary>Number of votes.</summary>
    [JsonPropertyName("votes")]
    public int? Votes { get; set; }

    /// <summary>Provider URL for this item.</summary>
    [JsonPropertyName("url")]
    [JsonConverter(typeof(TolerantStringConverter))]
    public string? Url { get; set; }
}

/// <summary>
/// Raw MDBList API response.
/// </summary>
internal class MdbListApiResponse
{
    /// <summary>Content type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Array of ratings from different sources.</summary>
    [JsonPropertyName("ratings")]
    public List<MdbListRating>? Ratings { get; set; }
}
