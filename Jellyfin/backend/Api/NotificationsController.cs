using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// API controller for Moonfin push notification preferences, device registration,
/// and the inbound Seerr webhook.
/// </summary>
[ApiController]
[Route("Moonfin/Notifications")]
[Produces(MediaTypeNames.Application.Json)]
public class NotificationsController : ControllerBase
{
    private const string WebhookSecretHeader = "X-Moonfin-Webhook-Secret";

    private readonly NotificationStore _store;
    private readonly SeerrWebhookService _webhookService;
    private readonly SeerrProvisioningService _provisioning;

    public NotificationsController(
        NotificationStore store,
        SeerrWebhookService webhookService,
        SeerrProvisioningService provisioning)
    {
        _store = store;
        _webhookService = webhookService;
        _provisioning = provisioning;
    }

    /// <summary>
    /// Saves the caller's notification preferences.
    /// </summary>
    [HttpPost("Prefs")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult SavePrefs([FromBody] NotificationPrefsRequest request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        _store.SavePrefs(
            userId.Value,
            request.NotifyOnNewRequests,
            request.NotifyOnLibraryAdded,
            request.NotifyOnIssues,
            request.NotifyOnNewMedia);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Returns the caller's stored notification preferences.
    /// </summary>
    [HttpGet("Prefs")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetPrefs()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var prefs = _store.GetPrefs(userId.Value);
        return Ok(new
        {
            notifyOnNewRequests = prefs.NotifyOnNewRequests,
            notifyOnLibraryAdded = prefs.NotifyOnLibraryAdded,
            notifyOnIssues = prefs.NotifyOnIssues,
            notifyOnNewMedia = prefs.NotifyOnNewMedia
        });
    }

    /// <summary>
    /// Registers a push token for the caller so backgrounded clients receive notifications.
    /// </summary>
    [HttpPost("Register")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RegisterDevice([FromBody] DeviceRegistrationRequest request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { error = "Token is required" });
        }

        _store.RegisterDevice(userId.Value, request.Token, request.Platform, request.DeviceId);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Removes a registered push token for the caller.
    /// </summary>
    [HttpDelete("Register")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult UnregisterDevice([FromBody] DeviceRegistrationRequest request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        _store.UnregisterDevice(userId.Value, request.Token, request.DeviceId);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Returns the webhook URL and secret an admin should paste into Seerr's webhook agent.
    /// </summary>
    [HttpGet("WebhookInfo")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> WebhookInfo()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var secret = config?.SeerrWebhookSecret ?? string.Empty;
        var baseUrl = ResolvePublicBaseUrl(config?.PublicServerUrl);
        var url = $"{baseUrl}/Moonfin/Seerr/Webhook?secret={Uri.EscapeDataString(secret)}";

        var currentTypes = await _provisioning.GetLiveWebhookTypesAsync(HttpContext.RequestAborted);

        return Ok(new
        {
            url,
            secret,
            secretHeader = WebhookSecretHeader,
            notificationTypes = new[]
            {
                "MEDIA_PENDING", "MEDIA_APPROVED", "MEDIA_AVAILABLE", "MEDIA_DECLINED",
                "ISSUE_CREATED", "ISSUE_COMMENT", "ISSUE_RESOLVED", "ISSUE_REOPENED"
            },
            status = _provisioning.LastStatus.ToString(),
            message = _provisioning.LastMessage,
            likelyUnreachable = _provisioning.LastResolvedUrlLikelyUnreachable,
            currentTypes,
            expectedTypes = SeerrProvisioningService.TargetTypes
        });
    }

    /// <summary>
    /// Clears the provisioning throttle and re-registers Moonfin's webhook in Seerr immediately.
    /// </summary>
    [HttpPost("Reprovision")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reprovision()
    {
        var result = await _provisioning.ForceReprovisionAsync(HttpContext.RequestAborted);
        return Ok(new
        {
            status = result.Status.ToString(),
            message = result.Message
        });
    }

    /// <summary>
    /// Inbound Seerr webhook. Authenticated by a shared secret rather than a Jellyfin token.
    /// </summary>
    [HttpPost("/Moonfin/Seerr/Webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SeerrWebhook([FromQuery] string? secret)
    {
        var configured = MoonfinPlugin.Instance?.Configuration?.SeerrWebhookSecret;
        var presented = secret;
        if (string.IsNullOrEmpty(presented) &&
            Request.Headers.TryGetValue(WebhookSecretHeader, out var headerValue))
        {
            presented = headerValue.ToString();
        }

        if (!IsSecretValid(configured, presented))
        {
            return Unauthorized();
        }

        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Ok();
            }

            using var doc = JsonDocument.Parse(raw);
            var payload = doc.RootElement.Clone();
            _ = Task.Run(() => _webhookService.HandleWebhookAsync(payload));
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON payload" });
        }

        return Ok();
    }

    private static bool IsSecretValid(string? configured, string? presented)
    {
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(configured);
        var b = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string ResolvePublicBaseUrl(string? configuredUrl)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host.Value}";
    }
}

/// <summary>Request body for saving notification preferences.</summary>
public class NotificationPrefsRequest
{
    public bool NotifyOnNewRequests { get; set; }
    public bool NotifyOnLibraryAdded { get; set; }

    // Nullable so payloads from clients that predate the field keep the stored value.
    public bool? NotifyOnIssues { get; set; }
    public bool? NotifyOnNewMedia { get; set; }
}

/// <summary>Request body for registering or removing a push device.</summary>
public class DeviceRegistrationRequest
{
    public string? Token { get; set; }
    public string? Platform { get; set; }
    public string? DeviceId { get; set; }
}
