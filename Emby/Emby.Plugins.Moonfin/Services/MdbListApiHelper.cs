using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Shared helpers for talking to api.mdblist.com from the API services and scheduled tasks.
    /// </summary>
    internal static class MdbListApiHelper
    {
        public const string BaseUrl = "https://api.mdblist.com";

        /// <summary>
        /// Shared serializer options for all MDBList payloads. MDBList emits some numeric
        /// fields as strings, so AllowReadingFromString is required everywhere.
        /// </summary>
        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

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
                    logger.Warn("MDBList /user returned status " + (int)response.StatusCode + ", key invalid or service unavailable");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<MdbListAccountInfo>(json, JsonOptions);
                if (info == null || string.IsNullOrEmpty(info.Username))
                {
                    logger.Warn("MDBList /user returned an unexpected payload");
                    return null;
                }

                logger.Info(
                    "MDBList key ok: user " + info.Username +
                    ", plan " + (info.Plan ?? (info.IsSupporter ? "Supporter" : "Free")) +
                    ", requests today " + (info.ApiRequestsCount ?? 0) + "/" + (info.ApiRequests ?? 0), 0);
                return info;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Warn("Failed to fetch MDBList account info: " + ex.Message);
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
            if (string.IsNullOrWhiteSpace(url)) return null;

            if (!url!.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;

            var marker = url.IndexOf("/t/p/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return url;

            // Skip the size segment that follows /t/p/ (e.g. w200, original).
            var pathStart = url.IndexOf('/', marker + 5);
            return pathStart < 0 ? url : url.Substring(pathStart);
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
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("plan")] public string? Plan { get; set; }
        [JsonPropertyName("is_supporter")] public bool IsSupporter { get; set; }
        [JsonPropertyName("api_requests")] public int? ApiRequests { get; set; }
        [JsonPropertyName("api_requests_count")] public int? ApiRequestsCount { get; set; }
        [JsonPropertyName("rate_limit_remaining")] public int? RateLimitRemaining { get; set; }
    }
}
