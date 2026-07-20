using System.Linq;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// API controller for Seerr SSO proxy.
/// Handles authentication, session management, and API proxying so that
/// any Moonfin client can access Seerr through the Jellyfin server.
/// </summary>
[ApiController]
[Route("Moonfin/Seerr")]
// Legacy alias kept active so older clients keep working while they migrate to /Moonfin/Seerr.
[Route("Moonfin/Jellyseerr")]
[Produces(MediaTypeNames.Application.Json)]
public class SeerrProxyController : ControllerBase
{
    private const int AdminBit = 2;
    private const int OwnerSeerrUserId = 1;

    private readonly SeerrSessionService _sessionService;
    private readonly SeerrProvisioningService _provisioning;

    public SeerrProxyController(SeerrSessionService sessionService, SeerrProvisioningService provisioning)
    {
        _sessionService = sessionService;
        _provisioning = provisioning;
    }

    /// <summary>
    /// Authenticate with Seerr using Jellyfin credentials.
    /// The session cookie is stored server-side and associated with the Jellyfin user.
    /// Any Moonfin client can then proxy requests through this plugin.
    /// </summary>
    /// <param name="request">Jellyfin credentials for Seerr auth.</param>
    /// <returns>Authentication result with Seerr user info.</returns>
    [HttpPost("Login")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Login([FromBody] SeerrLoginRequest request)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();
        if (config?.SeerrEnabled != true || string.IsNullOrEmpty(seerrUrl))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Seerr integration is not enabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        if (string.IsNullOrEmpty(request.Username))
        {
            return BadRequest(new { error = "Username is required" });
        }

        var result = await _sessionService.AuthenticateAsync(
            userId.Value, request.Username, request.Password,
            request.AuthType);

        if (result == null || !result.Success)
        {
            return Unauthorized(new
            {
                error = result?.Error ?? "Authentication failed",
                success = false
            });
        }

        // Once an admin session exists, auto-register our webhook best-effort (throttled internally).
        if (result.SeerrUserId == OwnerSeerrUserId || (result.Permissions & AdminBit) != 0)
        {
            _ = _provisioning.EnsureWebhookAsync(default);
        }

        return Ok(new
        {
            success = true,
            seerrUserId = result.SeerrUserId,
            jellyseerrUserId = result.SeerrUserId, // legacy alias for pre-rename clients
            displayName = result.DisplayName,
            avatar = result.Avatar,
            permissions = result.Permissions
        });
    }

    /// <summary>
    /// Check the current user's Seerr SSO session status.
    /// </summary>
    /// <returns>Session status including whether authenticated and user info.</returns>
    [HttpGet("Status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();
        if (config?.SeerrEnabled != true || string.IsNullOrEmpty(seerrUrl))
        {
            return Ok(new
            {
                enabled = false,
                authenticated = false,
                url = (string?)null
            });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Ok(new
            {
                enabled = true,
                authenticated = false,
                url = seerrUrl
            });
        }

        var session = await _sessionService.GetSessionAsync(userId.Value, validate: false);

        return Ok(new
        {
            enabled = true,
            authenticated = session != null,
            url = seerrUrl,
            seerrUserId = session?.SeerrUserId,
            jellyseerrUserId = session?.SeerrUserId, // legacy alias for pre-rename clients
            displayName = session?.DisplayName,
            avatar = session?.Avatar,
            permissions = session?.Permissions ?? 0,
            sessionCreated = session?.CreatedAt,
            lastValidated = session?.LastValidated
        });
    }

    /// <summary>
    /// Validate the current session is still active with Seerr.
    /// </summary>
    /// <returns>Whether the session is valid.</returns>
    [HttpGet("Validate")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Ok(new { valid = false, error = "Not authenticated with Jellyfin" });
        }

        var session = await _sessionService.GetSessionAsync(userId.Value, validate: true);

        return Ok(new
        {
            valid = session != null,
            lastValidated = session?.LastValidated
        });
    }

    /// <summary>
    /// Clear the current user's Seerr SSO session.
    /// </summary>
    [HttpDelete("Logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        await _sessionService.ProxyRequestAsync(
            userId.Value,
            HttpMethod.Post,
            "auth/logout");

        await _sessionService.ClearSessionAsync(userId.Value);

        return Ok(new { success = true, message = "Logged out from Seerr" });
    }

    /// <summary>
    /// Proxy GET requests to Seerr API.
    /// Path is relative to /api/v1/ (e.g., "auth/me", "request", "search?query=foo").
    /// </summary>
    [HttpGet("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyGet(string path)
    {
        return await ProxyApiRequest(HttpMethod.Get, path);
    }

    /// <summary>
    /// Proxy POST requests to Seerr API.
    /// </summary>
    [HttpPost("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyPost(string path)
    {
        return await ProxyApiRequest(HttpMethod.Post, path);
    }

    /// <summary>
    /// Proxy PUT requests to Seerr API.
    /// </summary>
    [HttpPut("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyPut(string path)
    {
        return await ProxyApiRequest(HttpMethod.Put, path);
    }

    /// <summary>
    /// Proxy DELETE requests to Seerr API.
    /// </summary>
    [HttpDelete("Api/{**path}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProxyDelete(string path)
    {
        return await ProxyApiRequest(HttpMethod.Delete, path);
    }

    private async Task<IActionResult> ProxyApiRequest(HttpMethod method, string path)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var seerrUrl = config?.GetEffectiveSeerrUrl();
        if (config?.SeerrEnabled != true || string.IsNullOrEmpty(seerrUrl))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Seerr integration is not enabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var targetUserId = userId.Value;
        var requestPath = path?.Trim().ToLowerInvariant() ?? string.Empty;
        if (method == HttpMethod.Get && (requestPath == "settings/radarr" || requestPath == "settings/sonarr"))
        {
            var currentSession = await _sessionService.GetSessionAsync(userId.Value);
            bool isCurrentAdmin = currentSession != null && (currentSession.SeerrUserId == OwnerSeerrUserId || (currentSession.Permissions & AdminBit) != 0);

            if (!isCurrentAdmin)
            {
                var adminSession = _sessionService.EnumerateSessions()
                    .FirstOrDefault(s => s.SeerrUserId == OwnerSeerrUserId || (s.Permissions & AdminBit) != 0);
                if (adminSession != null)
                {
                    targetUserId = adminSession.JellyfinUserId;
                }
            }
        }

        byte[]? body = null;
        string? contentType = null;

        if (method == HttpMethod.Post || method == HttpMethod.Put)
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            body = ms.ToArray();
            contentType = Request.ContentType;
        }

        var result = await _sessionService.ProxyRequestAsync(
            targetUserId,
            method,
            path ?? "",
            Request.QueryString.Value,
            body,
            contentType);

        if (result.Body == null)
        {
            return StatusCode(result.StatusCode);
        }

        var responseContentType = string.IsNullOrWhiteSpace(result.ContentType)
            ? MediaTypeNames.Application.Octet
            : result.ContentType;

        Response.StatusCode = result.StatusCode;
        return File(result.Body, responseContentType);
    }
}

/// <summary>
/// Request body for Seerr login.
/// </summary>
public class SeerrLoginRequest
{
    /// <summary>Username (Jellyfin or local Seerr account).</summary>
    public string? Username { get; set; }

    /// <summary>Password.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Authentication type: "jellyfin" (default) or "local".
    /// Determines which Seerr auth endpoint is used.
    /// </summary>
    public string? AuthType { get; set; }
}
