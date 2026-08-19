using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Automatically registers Moonfin's webhook in Seerr using a stored admin session,
    /// so admins don't have to paste the URL by hand. Guardrailed: it never overwrites a
    /// third-party webhook, and it's idempotent. The manual WebhookInfo endpoint remains
    /// the fallback whenever auto-provisioning can't run.
    /// </summary>
    public class SeerrProvisioningService
    {
        private const int AdminBit = 2;
        private const int OwnerSeerrUserId = 1;

        // MEDIA_PENDING (2) | MEDIA_APPROVED (4) | MEDIA_AVAILABLE (8) | MEDIA_DECLINED (64) |
        // ISSUE_CREATED (256) | ISSUE_COMMENT (512) | ISSUE_RESOLVED (1024) | ISSUE_REOPENED (2048).
        public const int TargetTypes = 3918;

        private const string WebhookPath = "/Moonfin/Seerr/Webhook";
        private const string SettingsPath = "settings/notifications/webhook";

        // The template Seerr stores and expands per event. Double-brace keys are Seerr's
        // conditional-inclusion syntax (it replaces {{media}} -> media and strips the block
        // when absent), so the stored string is always valid JSON when our parser reads it.
        private const string PayloadTemplate = @"{
    ""notification_type"": ""{{notification_type}}"",
    ""subject"": ""{{subject}}"",
    ""message"": ""{{message}}"",
    ""notifyuser_username"": ""{{notifyuser_username}}"",
    ""{{media}}"": {
        ""media_type"": ""{{media_type}}"",
        ""tmdbId"": ""{{media_tmdbid}}"",
        ""tvdbId"": ""{{media_tvdbid}}"",
        ""status"": ""{{media_status}}""
    },
    ""{{request}}"": {
        ""request_id"": ""{{request_id}}"",
        ""requestedBy_username"": ""{{requestedBy_username}}"",
        ""requestedBy_jellyfinUserId"": ""{{requestedBy_jellyfinUserId}}""
    },
    ""{{issue}}"": {
        ""issue_id"": ""{{issue_id}}"",
        ""issue_type"": ""{{issue_type}}"",
        ""issue_status"": ""{{issue_status}}"",
        ""reportedBy_username"": ""{{reportedBy_username}}""
    },
    ""{{comment}}"": {
        ""comment_message"": ""{{comment_message}}"",
        ""commentedBy_username"": ""{{commentedBy_username}}""
    },
    ""{{extra}}"": []
}";

        private readonly SeerrSessionService _sessionService;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger _logger;

        // Best-effort throttle so a busy login path doesn't hammer Seerr's settings API.
        private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(5);
        private long _lastAttemptTicks;
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public SeerrProvisioningService(
            SeerrSessionService sessionService,
            IServerApplicationHost appHost,
            ILogger logger)
        {
            _sessionService = sessionService;
            _appHost = appHost;
            _logger = logger;
        }

        /// <summary>The status of the last provisioning attempt, for surfacing to admins.</summary>
        public ProvisioningStatus LastStatus { get; private set; } = ProvisioningStatus.NotAttempted;

        /// <summary>
        /// What went wrong in the last attempt, in words. The status alone names
        /// a condition without saying what to do about it, and an admin reading
        /// NoAdminSession has nowhere to go from there.
        /// </summary>
        public string LastMessage { get; private set; } = string.Empty;

        /// <summary>
        /// Runs provisioning if it hasn't been tried within the retry window. Fire-and-forget safe.
        /// </summary>
        public async Task<ProvisioningResult> EnsureWebhookAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastAttemptTicks);
            if (last != 0 && now - last < RetryWindow.Ticks)
            {
                return new ProvisioningResult(LastStatus, LastMessage);
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check inside the gate so concurrent callers collapse into one attempt.
                last = Interlocked.Read(ref _lastAttemptTicks);
                if (last != 0 && now - last < RetryWindow.Ticks)
                {
                    return new ProvisioningResult(LastStatus, LastMessage);
                }

                Interlocked.Exchange(ref _lastAttemptTicks, now);
                var result = await ProvisionAsync(cancellationToken).ConfigureAwait(false);
                LastStatus = result.Status;
                LastMessage = result.Message;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Clears the throttle and provisions again immediately. For an admin who needs to push a
        /// fresh webhook config (for example after the target types bitmask changed).
        /// </summary>
        public Task<ProvisioningResult> ForceReprovisionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastAttemptTicks, 0);
            return EnsureWebhookAsync(cancellationToken);
        }

        /// <summary>
        /// Reads the webhook's current types bitmask from Seerr via an admin session. Returns null
        /// when it can't be read. Lets an admin confirm what Seerr will actually send.
        /// </summary>
        public async Task<int?> GetLiveWebhookTypesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var admin = _sessionService.EnumerateSessions().FirstOrDefault(IsAdmin);
                if (admin == null)
                    return null;

                var response = await _sessionService.RequestWithSessionAsync(
                    admin, HttpMethod.Get, SettingsPath, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (response.StatusCode < 200 || response.StatusCode >= 300 || response.Body == null)
                    return null;

                using var doc = JsonDocument.Parse(response.Body);
                if (doc.RootElement.TryGetProperty("types", out var ty) && ty.ValueKind == JsonValueKind.Number &&
                    ty.TryGetInt32(out var types))
                {
                    return types;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not read live webhook types: " + ex.Message);
            }

            return null;
        }

        private async Task<ProvisioningResult> ProvisionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null || !config.SeerrEnabled || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
                {
                    return new ProvisioningResult(ProvisioningStatus.NotConfigured, "Seerr is not enabled or configured.");
                }

                var admin = _sessionService.EnumerateSessions().FirstOrDefault(IsAdmin);
                if (admin == null)
                {
                    return new ProvisioningResult(ProvisioningStatus.NoAdminSession,
                        "No admin Seerr session available yet. Configure the webhook manually or sign in as a Seerr admin.");
                }

                var secret = config.SeerrWebhookSecret ?? string.Empty;
                if (string.IsNullOrEmpty(secret))
                {
                    return new ProvisioningResult(ProvisioningStatus.Failed, "Webhook secret is not set.");
                }

                var baseUrl = ResolvePublicBaseUrl(config.PublicServerUrl);
                if (string.IsNullOrEmpty(baseUrl))
                {
                    return new ProvisioningResult(ProvisioningStatus.NeedsPublicUrl,
                        "Could not determine a public server URL. Set PublicServerUrl in the plugin config.");
                }

                var targetUrl = $"{baseUrl}{WebhookPath}?secret={Uri.EscapeDataString(secret)}";

                var getResponse = await _sessionService.RequestWithSessionAsync(
                    admin, HttpMethod.Get, SettingsPath, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (getResponse.StatusCode == 403)
                {
                    return new ProvisioningResult(ProvisioningStatus.NoAdminSession,
                        "Stored session lacks admin rights on Seerr.");
                }

                if (getResponse.StatusCode < 200 || getResponse.StatusCode >= 300 || getResponse.Body == null)
                {
                    return new ProvisioningResult(ProvisioningStatus.Failed,
                        $"Reading webhook settings failed ({getResponse.StatusCode}).");
                }

                string? existingUrl = null;
                int existingTypes = 0;
                bool existingEnabled = false;
                string? existingPayload = null;
                try
                {
                    using var doc = JsonDocument.Parse(getResponse.Body);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True)
                    {
                        existingEnabled = true;
                    }
                    if (root.TryGetProperty("types", out var ty) && ty.ValueKind == JsonValueKind.Number)
                    {
                        ty.TryGetInt32(out existingTypes);
                    }
                    if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                    {
                        if (opts.TryGetProperty("webhookUrl", out var wu) && wu.ValueKind == JsonValueKind.String)
                        {
                            existingUrl = wu.GetString();
                        }
                        if (opts.TryGetProperty("jsonPayload", out var jp) && jp.ValueKind == JsonValueKind.String)
                        {
                            existingPayload = jp.GetString();
                        }
                    }
                }
                catch (JsonException)
                {
                    // Treat an unreadable body as empty and continue to (re)write below.
                }

                // Guardrail: only touch an empty slot or our own webhook.
                var isOurs = !string.IsNullOrEmpty(existingUrl) &&
                    existingUrl!.IndexOf(WebhookPath, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!string.IsNullOrEmpty(existingUrl) && !isOurs)
                {
                    _logger.Warn("Seerr already has a different webhook configured (" + existingUrl
                        + "); not overwriting. Configure Moonfin's webhook manually (see the WebhookInfo endpoint).");
                    return new ProvisioningResult(ProvisioningStatus.ForeignWebhookPresent,
                        "A different webhook is already configured in Seerr. Configure Moonfin's manually.");
                }

                // Idempotent: same url, enabled, our bits already set, same payload.
                if (isOurs && existingEnabled &&
                    (existingTypes & TargetTypes) == TargetTypes &&
                    string.Equals(existingUrl, targetUrl, StringComparison.Ordinal) &&
                    string.Equals(existingPayload, PayloadTemplate, StringComparison.Ordinal))
                {
                    return new ProvisioningResult(ProvisioningStatus.AlreadyProvisioned,
                        "Webhook already registered.");
                }

                var body = new
                {
                    enabled = true,
                    types = existingTypes | TargetTypes,
                    options = new
                    {
                        webhookUrl = targetUrl,
                        jsonPayload = PayloadTemplate,
                        authHeader = string.Empty
                    }
                };

                var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                var postResponse = await _sessionService.RequestWithSessionAsync(
                    admin, HttpMethod.Post, SettingsPath, bytes, "application/json", cancellationToken).ConfigureAwait(false);

                if (postResponse.StatusCode >= 200 && postResponse.StatusCode < 300)
                {
                    // Log the base URL only, since targetUrl carries the secret in its query string.
                    _logger.Info("Registered Moonfin webhook in Seerr at " + baseUrl + WebhookPath);
                    return new ProvisioningResult(ProvisioningStatus.Provisioned, "Webhook registered.");
                }

                return new ProvisioningResult(ProvisioningStatus.Failed,
                    $"Writing webhook settings failed ({postResponse.StatusCode}).");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Seerr webhook auto-provisioning failed", ex);
                return new ProvisioningResult(ProvisioningStatus.Failed, ex.Message);
            }
        }

        private static bool IsAdmin(SeerrSession session) =>
            session.SeerrUserId == OwnerSeerrUserId || (session.Permissions & AdminBit) != 0;

        /// <summary>
        /// True when the last resolved base URL was a loopback fallback, which a containerized
        /// Seerr almost certainly can't reach. Surfaced by WebhookInfo so admins set PublicServerUrl.
        /// </summary>
        public bool LastResolvedUrlLikelyUnreachable { get; private set; }

        // Resolves a base URL Seerr can actually reach. A loopback URL is useless to a
        // containerized Seerr, so we prefer, in order: the admin override, a LAN IPv4, and
        // only fall back to loopback as a flagged last resort.
        private string? ResolvePublicBaseUrl(string? configuredUrl)
        {
            LastResolvedUrlLikelyUnreachable = false;

            // (1) Admin-set public URL wins.
            if (!string.IsNullOrWhiteSpace(configuredUrl))
            {
                return configuredUrl!.TrimEnd('/');
            }

            // (2) First non-loopback LAN IPv4 address, resolved through the app host so the
            // right scheme/port are applied.
            var lanAddress = GetLanIPv4Address();
            if (lanAddress != null)
            {
                try
                {
                    var url = _appHost.GetLocalApiUrl(lanAddress);
                    if (!string.IsNullOrWhiteSpace(url) && !IsLoopbackUrl(url))
                    {
                        return url.TrimEnd('/');
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not resolve a LAN API URL: " + ex.Message);
                }
            }

            // (3) Loopback, last resort. Flag it so WebhookInfo tells the admin to set PublicServerUrl.
            try
            {
                var url = _appHost.GetLocalApiUrl(IPAddress.Loopback);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    LastResolvedUrlLikelyUnreachable = true;
                    return url.TrimEnd('/');
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not resolve the server's own API URL: " + ex.Message);
            }

            return null;
        }

        // Finds the first non-loopback IPv4 address by enumerating operational network
        // interfaces. The host exposes no bind-address API, so we read the network
        // interfaces directly (System.Net.NetworkInformation).
        private IPAddress? GetLanIPv4Address()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        var addr = ip.Address;
                        if (addr.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr))
                        {
                            return addr;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not enumerate LAN network interfaces: " + ex.Message);
            }

            return null;
        }

        private static bool IsLoopbackUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.IsLoopback ||
                 string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Outcome of a provisioning attempt.</summary>
    public enum ProvisioningStatus
    {
        NotAttempted,
        NotConfigured,
        NoAdminSession,
        NeedsPublicUrl,
        ForeignWebhookPresent,
        AlreadyProvisioned,
        Provisioned,
        Failed
    }

    /// <summary>Result of a provisioning attempt with a human-readable detail.</summary>
    public class ProvisioningResult
    {
        public ProvisioningResult(ProvisioningStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public ProvisioningStatus Status { get; }

        public string Message { get; }
    }
}
