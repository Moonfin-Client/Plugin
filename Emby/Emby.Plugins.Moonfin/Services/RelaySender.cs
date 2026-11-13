using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Sends push through the hosted relay, which holds the shared service account and forwards to
    /// FCM. The plugin only posts device tokens plus the payload, so distributed builds never carry
    /// the owner's credentials. One request carries all of a user's tokens. The relay reports which
    /// ones are dead so the caller can prune them.
    /// </summary>
    public class RelaySender
    {
        private readonly ILogger _logger;

        public RelaySender(ILogger logger)
        {
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
            bool dataOnly = false,
            CancellationToken cancellationToken = default)
        {
            var empty = Array.Empty<PushResult>();

            if (tokens.Count == 0) return empty;

            var config = Plugin.Instance?.Configuration;
            var relayUrl = config?.PushRelayUrl;
            var appKey = config?.GetRelayAppKey();
            if (config?.PushRelayEnabled != true || string.IsNullOrWhiteSpace(relayUrl) || string.IsNullOrWhiteSpace(appKey))
                return empty;

            var payload = new Dictionary<string, object>
            {
                ["tokens"] = tokens,
                ["title"] = title,
                ["body"] = body,
                ["route"] = route
            };
            if (data != null)
                payload["data"] = data;
            if (!string.IsNullOrEmpty(apnsCategory))
                payload["apnsCategory"] = apnsCategory!;
            if (dataOnly)
                payload["dataOnly"] = true;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                using var request = new HttpRequestMessage(HttpMethod.Post, relayUrl);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + appKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("Push relay returned status " + (int)response.StatusCode);
                    return empty;
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseDeadTokens(responseBody);
            }
            catch (Exception ex)
            {
                _logger.Warn("Push relay request failed: " + ex.Message);
                return empty;
            }
        }

        // Reads results[].token where dead == true. Malformed bodies prune nothing.
        private static IReadOnlyList<PushResult> ParseDeadTokens(string responseBody)
        {
            var dead = new List<PushResult>();
            if (string.IsNullOrWhiteSpace(responseBody)) return dead;

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                    return dead;

                foreach (var entry in results.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;

                    if (!entry.TryGetProperty("dead", out var deadProp) ||
                        deadProp.ValueKind != JsonValueKind.True)
                        continue;

                    if (entry.TryGetProperty("token", out var tokenProp) &&
                        tokenProp.ValueKind == JsonValueKind.String)
                    {
                        var token = tokenProp.GetString();
                        if (!string.IsNullOrEmpty(token))
                            dead.Add(new PushResult(token));
                    }
                }
            }
            catch
            {
                // Non-JSON or unexpected shape. Prune nothing.
            }

            return dead;
        }
    }

    /// <summary>A relay result for a single token that should be pruned.</summary>
    public sealed class PushResult
    {
        public PushResult(string token)
        {
            Token = token;
        }

        public string Token { get; }
    }
}
