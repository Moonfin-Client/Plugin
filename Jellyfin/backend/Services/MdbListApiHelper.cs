using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Api;

namespace Moonfin.Server.Services;

/// <summary>
/// Shared helpers for talking to api.mdblist.com.
/// </summary>
internal static class MdbListApiHelper
{
    public const string BaseUrl = "https://api.mdblist.com";

    /// <summary>
    /// Fetches account info for an API key (GET /user). Returns null when the key is
    /// invalid or MDBList is unreachable, so callers should skip their run. It's also
    /// the cheapest way to learn quota state before a sync burns requests.
    /// </summary>
    public static async Task<MdbListAccountInfo?> GetAccountInfoAsync(
        HttpClient client,
        string apiKey,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BaseUrl}/user?apikey={Uri.EscapeDataString(apiKey)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("MDBList /user returned status {Status}, key invalid or service unavailable", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<MdbListAccountInfo>(json, MdbListController.JsonOptions);
            if (info == null || string.IsNullOrEmpty(info.Username))
            {
                logger.LogWarning("MDBList /user returned an unexpected payload");
                return null;
            }

            logger.LogInformation(
                "MDBList key ok: user {User}, plan {Plan}, requests today {Used}/{Limit}",
                info.Username,
                info.Plan ?? (info.IsSupporter ? "Supporter" : "Free"),
                info.ApiRequestsCount ?? 0,
                info.ApiRequests ?? 0);
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch MDBList account info");
            return null;
        }
    }

    /// <summary>
    /// MDBList's append_to_response=poster returns full TMDB image URLs at a fixed size
    /// (e.g. https://image.tmdb.org/t/p/w200/abc.jpg). Clients expect the relative
    /// poster path (/abc.jpg) so they can request their own size, so extract it.
    /// </summary>
    public static string? NormalizeTmdbImagePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var marker = url.IndexOf("/t/p/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return url;
        }

        // Skip the size segment that follows /t/p/ (e.g. w200, original).
        var pathStart = url.IndexOf('/', marker + 5);
        return pathStart < 0 ? url : url[pathStart..];
    }
}

/// <summary>
/// Thrown when MDBList returns 429 so callers can abort a sync run instead of
/// burning further requests against an exhausted quota.
/// </summary>
internal class MdbListRateLimitException : Exception
{
    public MdbListRateLimitException()
        : base("MDBList rate limit reached")
    {
    }
}

internal class MdbListAccountInfo
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
