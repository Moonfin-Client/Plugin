using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Websocket relay endpoints for clients without TLS socket support, such as Roku.
/// The relay identity is always the caller's own user id, device id and token taken
/// from the auth claims, never from the request body.
/// </summary>
[ApiController]
[Route("Moonfin/SocketRelay")]
[Produces(MediaTypeNames.Application.Json)]
public class SocketRelayController : ControllerBase
{
    private const int MaxWaitMs = 25000;

    private readonly SocketRelayManager _relayManager;

    public SocketRelayController(SocketRelayManager relayManager)
    {
        _relayManager = relayManager;
    }

    [HttpPost("Open")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SocketRelayOpenResponse> Open()
    {
        if (!RelayEnabled())
        {
            return Disabled();
        }

        if (!TryGetIdentity(out var userId, out var deviceId, out var token))
        {
            return Unauthorized(new { Error = "Device session required" });
        }

        var result = _relayManager.EnsureRelay(userId, deviceId, token);
        if (result == null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { Error = "Too many socket relays open" });
        }

        return Ok(new SocketRelayOpenResponse
        {
            Active = true,
            Created = result.Created,
            State = result.State,
            Cursor = result.Cursor
        });
    }

    [HttpGet("Poll")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<SocketRelayPollResponse>> Poll([FromQuery] long cursor = 0, [FromQuery] int waitMs = MaxWaitMs)
    {
        if (!RelayEnabled())
        {
            // Gone rather than 503 so a polling client goes back to Open and backs off there
            return StatusCode(StatusCodes.Status410Gone, new { Error = "Socket relay is disabled" });
        }

        if (!TryGetIdentity(out var userId, out var deviceId, out var token))
        {
            return Unauthorized(new { Error = "Device session required" });
        }

        var wait = TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 0, MaxWaitMs));
        var result = await _relayManager.PollAsync(userId, deviceId, token, cursor, wait, HttpContext.RequestAborted).ConfigureAwait(false);
        if (result == null)
        {
            return NotFound(new { Error = "No relay open" });
        }

        if (result.State == "gone")
        {
            return StatusCode(StatusCodes.Status410Gone, new { Error = "Relay gone" });
        }

        return Ok(new SocketRelayPollResponse
        {
            Cursor = result.Cursor,
            Messages = result.Messages,
            State = result.State,
            Reset = result.Reset
        });
    }

    [HttpPost("Close")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Close()
    {
        if (!RelayEnabled())
        {
            return Disabled();
        }

        if (!TryGetIdentity(out var userId, out var deviceId, out var token))
        {
            return Unauthorized(new { Error = "Device session required" });
        }

        _relayManager.CloseRelay(userId, deviceId, token);
        return NoContent();
    }

    // Turning the setting off also stops whatever relays are still running
    private bool RelayEnabled()
    {
        if (MoonfinPlugin.Instance?.Configuration?.EnableSocketRelay == true)
        {
            return true;
        }

        _relayManager.CloseAll();
        return false;
    }

    private ObjectResult Disabled()
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { Error = "Socket relay is disabled" });

    private bool TryGetIdentity(out Guid userId, out string deviceId, out string token)
    {
        userId = this.GetUserIdFromClaims() ?? Guid.Empty;
        deviceId = this.GetDeviceIdFromClaims() ?? string.Empty;
        token = this.GetTokenFromClaims() ?? string.Empty;

        // API keys have no device session for the relay to attach to
        if (this.GetIsApiKeyFromClaims())
        {
            return false;
        }

        return userId != Guid.Empty && deviceId.Length > 0 && token.Length > 0;
    }
}

public class SocketRelayOpenResponse
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("created")]
    public bool Created { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }
}

public class SocketRelayPollResponse
{
    [JsonPropertyName("cursor")]
    public long Cursor { get; set; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<string> Messages { get; set; } = [];

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>True when messages after the cursor were dropped, so the client should resync.</summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; set; }
}
