using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Sends push through the hosted relay, which holds the shared service account and forwards to
/// FCM. The plugin only posts device tokens plus the payload, so distributed builds never carry
/// the owner's credentials. One request carries all of a user's tokens; the relay reports which
/// ones are dead so the caller can prune them.
/// </summary>
public class RelaySender
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RelaySender> _logger;

    public RelaySender(IHttpClientFactory httpClientFactory, ILogger<RelaySender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Posts the given tokens and payload to the relay. Returns the tokens the relay flagged as
    /// dead. Fails safe: any non-200 response or exception returns no dead tokens so nothing is
    /// pruned on a transient error.
    /// </summary>
    public async Task<IReadOnlyList<PushResult>> SendAsync(
        IReadOnlyList<string> tokens,
        string title,
        string body,
        string route,
        IReadOnlyDictionary<string, string>? data = null,
        string? apnsCategory = null,
        CancellationToken cancellationToken = default)
    {
        var empty = Array.Empty<PushResult>();

        if (tokens.Count == 0)
        {
            return empty;
        }

        var config = MoonfinPlugin.Instance?.Configuration;
        var relayUrl = config?.PushRelayUrl;
        var appKey = config?.GetRelayAppKey();
        if (config?.PushRelayEnabled != true || string.IsNullOrWhiteSpace(relayUrl) || string.IsNullOrWhiteSpace(appKey))
        {
            return empty;
        }

        // Only the base fields when no request extras are set, so old behavior is byte-identical.
        var payload = new Dictionary<string, object>
        {
            ["tokens"] = tokens,
            ["title"] = title,
            ["body"] = body,
            ["route"] = route
        };
        if (data != null)
        {
            payload["data"] = data;
        }

        if (!string.IsNullOrEmpty(apnsCategory))
        {
            payload["apnsCategory"] = apnsCategory!;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var request = new HttpRequestMessage(HttpMethod.Post, relayUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + appKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Push relay returned status {Status}", (int)response.StatusCode);
                return empty;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseDeadTokens(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Push relay request failed");
            return empty;
        }
    }

    // Reads results[].token where dead == true. Malformed bodies prune nothing.
    private static IReadOnlyList<PushResult> ParseDeadTokens(string responseBody)
    {
        var dead = new List<PushResult>();
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return dead;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return dead;
            }

            foreach (var entry in results.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("dead", out var deadProp) ||
                    deadProp.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                if (entry.TryGetProperty("token", out var tokenProp) &&
                    tokenProp.ValueKind == JsonValueKind.String)
                {
                    var token = tokenProp.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        dead.Add(new PushResult(token));
                    }
                }
            }
        }
        catch
        {
            // Non-JSON or unexpected shape; prune nothing.
        }

        return dead;
    }
}

/// <summary>A token the relay flagged as dead, so the caller can prune it.</summary>
public sealed record PushResult(string Token);
