using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Handles notification preferences, push-device registration, the admin webhook-info
    /// endpoint, and the anonymous Seerr webhook. Services are reached statically through
    /// Plugin.Instance, and auth comes from the route DTO attributes.
    /// </summary>
    public class NotificationsService : IService, IRequiresRequest, IHasResultFactory
    {
        private const string WebhookSecretHeader = "X-Moonfin-Webhook-Secret";

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        private NotificationStore Store => Plugin.Instance?.NotificationStore
            ?? throw new InvalidOperationException("NotificationStore not initialized");

        public NotificationsService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetNotificationPrefsRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var prefs = Store.GetPrefs(userId.Value);
            return Json(new
            {
                notifyOnNewRequests = prefs.NotifyOnNewRequests,
                notifyOnLibraryAdded = prefs.NotifyOnLibraryAdded,
                notifyOnIssues = prefs.NotifyOnIssues,
                notifyOnNewMedia = prefs.NotifyOnNewMedia
            });
        }

        public async Task<object> Post(SaveNotificationPrefsRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var body = await MoonfinJson.ReadBodyAsync<NotificationPrefsBody>(request.RequestStream).ConfigureAwait(false)
                ?? new NotificationPrefsBody();

            Store.SavePrefs(userId.Value, body.NotifyOnNewRequests, body.NotifyOnLibraryAdded, body.NotifyOnIssues, body.NotifyOnNewMedia);
            return Json(new { success = true });
        }

        public async Task<object> Post(RegisterDeviceRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var body = await MoonfinJson.ReadBodyAsync<DeviceRegistrationBody>(request.RequestStream).ConfigureAwait(false);
            if (body == null || string.IsNullOrWhiteSpace(body.Token))
                return Json(400, new { error = "Token is required" });

            Store.RegisterDevice(userId.Value, body.Token!, body.Platform, body.DeviceId);
            return Json(new { success = true });
        }

        public async Task<object> Delete(UnregisterDeviceRequest request)
        {
            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var body = await MoonfinJson.ReadBodyAsync<DeviceRegistrationBody>(request.RequestStream).ConfigureAwait(false)
                ?? new DeviceRegistrationBody();

            Store.UnregisterDevice(userId.Value, body.Token, body.DeviceId);
            return Json(new { success = true });
        }

        // Admin-only (see route DTO). Returns everything an admin needs to paste Moonfin's
        // webhook into Seerr by hand when auto-provisioning can't run.
        public async Task<object> Get(GetWebhookInfoRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            var secret = config?.SeerrWebhookSecret;
            if (string.IsNullOrEmpty(secret) && config != null)
            {
                // Generate on first use so the info endpoint always yields a usable secret.
                if (config.EnsureWebhookSecret())
                {
                    Plugin.Instance?.SaveConfiguration();
                    secret = config.SeerrWebhookSecret;
                }
            }
            secret = secret ?? string.Empty;

            var baseUrl = ResolvePublicBaseUrl(config?.PublicServerUrl);
            var url = $"{baseUrl}/Moonfin/Seerr/Webhook?secret={Uri.EscapeDataString(secret)}";

            var provisioning = Plugin.Instance?.SeerrProvisioning;
            var status = provisioning?.LastStatus.ToString()
                ?? ProvisioningStatus.NotAttempted.ToString();
            var message = provisioning?.LastMessage ?? string.Empty;

            int? currentTypes = null;
            if (provisioning != null)
                currentTypes = await provisioning.GetLiveWebhookTypesAsync(default).ConfigureAwait(false);

            return Json(new
            {
                url,
                secret,
                header = WebhookSecretHeader,
                types = "MEDIA_PENDING,MEDIA_APPROVED,MEDIA_AVAILABLE,MEDIA_DECLINED,ISSUE_CREATED,ISSUE_COMMENT,ISSUE_RESOLVED,ISSUE_REOPENED",
                status,
                message,
                likelyUnreachable = provisioning?.LastResolvedUrlLikelyUnreachable ?? false,
                currentTypes,
                expectedTypes = SeerrProvisioningService.TargetTypes
            });
        }

        // Admin-only. Clears the provisioning throttle and re-registers the webhook immediately.
        public async Task<object> Post(ReprovisionWebhookRequest request)
        {
            var provisioning = Plugin.Instance?.SeerrProvisioning;
            if (provisioning == null)
                return Json(503, new { error = "Provisioning not available" });

            var result = await provisioning.ForceReprovisionAsync(default).ConfigureAwait(false);
            return Json(new
            {
                status = result.Status.ToString(),
                message = result.Message
            });
        }

        // Anonymous. Authenticated by the shared secret (query or header), not an Emby token.
        public async Task<object?> Post(SeerrWebhookRequest request)
        {
            var configured = Plugin.Instance?.Configuration?.SeerrWebhookSecret;

            var presented = request.Secret;
            if (string.IsNullOrEmpty(presented))
                presented = Request.QueryString["secret"];
            if (string.IsNullOrEmpty(presented))
                presented = Request.Headers[WebhookSecretHeader];

            if (!IsSecretValid(configured, presented))
            {
                Request.Response.StatusCode = 401;
                return null;
            }

            string raw;
            using (var ms = new MemoryStream())
            {
                if (request.RequestStream != null)
                    await request.RequestStream.CopyToAsync(ms).ConfigureAwait(false);
                raw = Encoding.UTF8.GetString(ms.ToArray());
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                Request.Response.StatusCode = 200;
                return null;
            }

            JsonElement payload;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                payload = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Json(400, new { error = "Invalid JSON payload" });
            }

            var webhook = Plugin.Instance?.SeerrWebhook;
            if (webhook != null)
                _ = Task.Run(() => webhook.HandleWebhookAsync(payload));

            Request.Response.StatusCode = 200;
            return null;
        }

        // Manual constant-time compare: netstandard2.1 has no CryptographicOperations.FixedTimeEquals.
        private static bool IsSecretValid(string? configured, string? presented)
        {
            if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(presented))
                return false;

            var a = Encoding.UTF8.GetBytes(configured!);
            var b = Encoding.UTF8.GetBytes(presented!);

            var diff = a.Length ^ b.Length;
            var max = Math.Max(a.Length, b.Length);
            for (var i = 0; i < max; i++)
            {
                var av = i < a.Length ? a[i] : 0;
                var bv = i < b.Length ? b[i] : 0;
                diff |= av ^ bv;
            }
            return diff == 0;
        }

        // PublicServerUrl wins when set, otherwise derive from the incoming request's authority.
        private string ResolvePublicBaseUrl(string? configuredUrl)
        {
            if (!string.IsNullOrWhiteSpace(configuredUrl))
                return configuredUrl!.TrimEnd('/');

            var fromRequest = Http.BaseUrl(Request);
            return string.IsNullOrEmpty(fromRequest) ? string.Empty : fromRequest;
        }
    }
}
