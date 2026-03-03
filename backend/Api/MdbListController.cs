using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly MdbListCacheService _cacheService;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    public MdbListController(MoonfinSettingsService settingsService, MdbListCacheService cacheService, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _cacheService = cacheService;
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

        // Resolve the full profile (device → global → admin defaults) to get user settings
        var resolved = await _settingsService.GetResolvedProfileAsync(userId.Value, "global");
        var apiKey = resolved?.MdblistApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = MoonfinPlugin.Instance?.Configuration?.MdblistApiKey;
        }

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
            // On-demand fallback: fetch single item from MDBList
            try
            {
                var url = $"https://api.mdblist.com/tmdb/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(tmdbId.Trim())}?apikey={Uri.EscapeDataString(apiKey)}";

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

                allRatings = data?.Ratings ?? new List<MdbListRating>();
                _cacheService.Set(cacheKey, allRatings);
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

        var filteredRatings = FilterAndOrderRatings(allRatings, resolved?.MdblistRatingSources);

        return Ok(new MdbListResponse
        {
            Success = true,
            Ratings = filteredRatings
        });
    }

    private static readonly string[] DefaultRatingSources = ["imdb", "tmdb", "tomatoes", "metacritic"];

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
            if (ratingsBySource.TryGetValue(source, out var rating))
            {
                result.Add(rating);
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
