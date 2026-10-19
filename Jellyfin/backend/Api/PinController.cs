using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Per-user PINs kept on the server, so a PIN set on one device holds on every other and a wrong
/// guess is counted here rather than where clearing app data would reset it. Nothing here ever
/// returns a PIN or its hash. Clients that don't call it keep using their own local PIN.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class PinController : ControllerBase
{
    private readonly PinService _pins;
    private readonly IUserDirectory _directory;

    public PinController(PinService pins, IUserDirectory directory)
    {
        _pins = pins;
        _directory = directory;
    }

    /// <summary>Whether the caller has a PIN. Never the PIN or its hash.</summary>
    [HttpGet("Pin")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<PinStatusResponse> GetStatus()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        return Ok(new PinStatusResponse { Enabled = _pins.HasPin(userId.Value) });
    }

    /// <summary>
    /// Sets or changes the caller's PIN. When one is already set the current one has to come
    /// with it, and a wrong one counts as a guess.
    /// </summary>
    [HttpPut("Pin")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PinStatusResponse> SetPin([FromBody] PinRequest request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        if (!PinService.IsPinShaped(request.Pin))
        {
            return BadRequest(new { error = "A PIN is exactly four digits" });
        }

        if (_pins.HasPin(userId.Value))
        {
            var refused = RefuseUnlessCurrent(userId.Value, request.CurrentPin);
            if (refused != null)
            {
                return refused;
            }
        }

        _pins.Set(userId.Value, request.Pin);
        return Ok(new PinStatusResponse { Enabled = true });
    }

    /// <summary>Checks a PIN. A wrong guess counts, and too many in a row make the caller wait.</summary>
    [HttpPost("Pin/Verify")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PinVerifyResponse> Verify([FromBody] PinRequest request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        var result = _pins.Verify(userId.Value, request.Pin);
        return result.Check switch
        {
            PinCheck.NoPin => NotFound(new { error = "No PIN is set" }),
            PinCheck.Locked => TooManyAttempts(result.RetryAfterSeconds),
            PinCheck.Valid => Ok(new PinVerifyResponse { Valid = true }),
            _ => Ok(new PinVerifyResponse { Valid = false }),
        };
    }

    /// <summary>Removes the caller's PIN, which takes the current one.</summary>
    [HttpDelete("Pin")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult<PinStatusResponse> RemovePin([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PinRequest? request)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        if (_pins.HasPin(userId.Value))
        {
            var refused = RefuseUnlessCurrent(userId.Value, request?.CurrentPin ?? request?.Pin);
            if (refused != null)
            {
                return refused;
            }
        }

        _pins.Remove(userId.Value);
        return Ok(new PinStatusResponse { Enabled = false });
    }

    /// <summary>Which administrators have a PIN, for the config page.</summary>
    [HttpGet("Admin/Pins")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<AdminPinResponse>> GetAdminPins() =>
        Ok(_directory.Users()
            .Where(u => u.IsAdministrator)
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .Select(u => new AdminPinResponse { Id = u.Id.ToString("N"), Name = u.Name, HasPin = _pins.HasPin(u.Id) }));

    /// <summary>Sets a user's PIN without the current one, for an administrator.</summary>
    [HttpPut("Users/{userId}/Pin")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<PinStatusResponse> SetPinFor([FromRoute] Guid userId, [FromBody] PinRequest request)
    {
        if (_directory.Users().All(u => u.Id != userId))
        {
            return NotFound(new { error = "That user does not exist" });
        }

        if (!PinService.IsPinShaped(request.Pin))
        {
            return BadRequest(new { error = "A PIN is exactly four digits" });
        }

        _pins.Set(userId, request.Pin);
        return Ok(new PinStatusResponse { Enabled = true });
    }

    /// <summary>Removes a user's PIN, for an administrator, so a forgotten one can be reset.</summary>
    [HttpDelete("Users/{userId}/Pin")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PinStatusResponse> RemovePinFor([FromRoute] Guid userId)
    {
        _pins.Remove(userId);
        return Ok(new PinStatusResponse { Enabled = false });
    }

    /// <summary>Null when <paramref name="current"/> is the user's PIN, else the answer to give.</summary>
    private ActionResult? RefuseUnlessCurrent(Guid userId, string? current)
    {
        var result = _pins.Verify(userId, current);
        return result.Check switch
        {
            PinCheck.Valid or PinCheck.NoPin => null,
            PinCheck.Locked => TooManyAttempts(result.RetryAfterSeconds),
            _ => StatusCode(StatusCodes.Status403Forbidden, new { error = "The current PIN is wrong" }),
        };
    }

    private ObjectResult TooManyAttempts(int retryAfterSeconds)
    {
        Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many attempts", retryAfterSeconds });
    }
}

public class PinRequest
{
    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    /// <summary>The PIN being replaced or removed, when there is one.</summary>
    [JsonPropertyName("currentPin")]
    public string? CurrentPin { get; set; }
}

public class PinStatusResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public class PinVerifyResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }
}

public class AdminPinResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hasPin")]
    public bool HasPin { get; set; }
}
