using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.LiveTv;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Who owns each Live TV timer, series rule and recording (My Recordings): recording a program
/// or its series as a user, claiming existing ones, and looking owners up. All of it answers 404
/// with {"error": "Recording owners are not available"} while the recording owners setting is off.
/// </summary>
[ApiController]
[Route("Moonfin/LiveTv/Owners")]
[Produces(MediaTypeNames.Application.Json)]
public class RecordingOwnersController : ControllerBase
{
    private const string LiveTvAccess = "LiveTvAccess";
    private const string LiveTvManagement = "LiveTvManagement";

    private readonly RecordingOwnershipService _service;
    private readonly ILiveTvRecordingHost _host;

    public RecordingOwnersController(RecordingOwnershipService service, ILiveTvRecordingHost host)
    {
        _service = service;
        _host = host;
    }

    /// <summary>Whether the recording owners setting is on. Replaceable for tests, which have no plugin instance.</summary>
    internal Func<bool> IsEnabled { get; set; } = RecordingOwnershipConfig.Enabled;

    // The same body Emby answers with, so a client can tell "feature off" from "no such program".
    internal static NotFoundObjectResult OwnersNotAvailable() =>
        new(new ErrorResponse("Recording owners are not available"));

    /// <summary>Records a program as the caller, or joins the timer that already records it.</summary>
    [HttpPost("Record")]
    [Authorize(Policy = LiveTvManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnershipResponse>> Record([FromBody] RecordOwnerBody body, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return OwnersNotAvailable();
        }

        if (string.IsNullOrWhiteSpace(body?.ProgramId))
        {
            return BadRequest("programId is required.");
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (!ProgramVisible(body.ProgramId, userId.Value))
        {
            return NotFound(new ErrorResponse("No guide program with that id"));
        }

        var attempt = await _service.TryRecordProgramAsync(body.ProgramId, userId.Value, cancellationToken).ConfigureAwait(false);
        return attempt.Outcome == null
            ? Conflict(new ErrorResponse(attempt.ErrorOr("The server could not schedule the recording.")))
            : Ok(OwnershipResponse.From(attempt.Outcome));
    }

    /// <summary>Records a program's series as the caller, or joins the rule that already records it.</summary>
    [HttpPost("RecordSeries")]
    [Authorize(Policy = LiveTvManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnershipResponse>> RecordSeries([FromBody] RecordSeriesOwnerBody body, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return OwnersNotAvailable();
        }

        if (string.IsNullOrWhiteSpace(body?.ProgramId))
        {
            return BadRequest("programId is required.");
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (!ProgramVisible(body.ProgramId, userId.Value))
        {
            return NotFound(new ErrorResponse("No guide program with that id"));
        }

        var attempt = await _service.TryRecordSeriesAsync(body.ProgramId, userId.Value, body.Settings, cancellationToken).ConfigureAwait(false);
        return attempt.Outcome == null
            ? Conflict(new ErrorResponse(attempt.ErrorOr("The server could not create the series recording.")))
            : Ok(OwnershipResponse.From(attempt.Outcome));
    }

    /// <summary>Adds the caller to the owners of an existing timer, series rule or recording.</summary>
    [HttpPost("Claim")]
    [Authorize(Policy = LiveTvManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClaimResponse>> Claim([FromBody] ClaimBody body, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return OwnersNotAvailable();
        }

        if (string.IsNullOrWhiteSpace(body?.Id))
        {
            return BadRequest("id is required.");
        }

        OwnedKind kind;
        switch (body.Kind?.ToLowerInvariant())
        {
            case "timer":
                kind = OwnedKind.Timer;
                break;
            case "series":
                kind = OwnedKind.Series;
                break;
            case "item":
                kind = OwnedKind.Item;
                break;
            default:
                return BadRequest("kind must be timer, series or item.");
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        var id = body.Id;
        if (kind == OwnedKind.Item)
        {
            if (!Guid.TryParse(id, out var itemId) || !_host.IsVisibleTo(itemId, userId.Value))
            {
                return NotFound(new ErrorResponse("No recording with that id"));
            }

            id = itemId.ToString("N");
        }

        var owners = await _service.ClaimAsync(kind, id, userId.Value, cancellationToken).ConfigureAwait(false);
        return owners == null
            ? NotFound(new ErrorResponse("Nothing to claim with that id"))
            : Ok(new ClaimResponse { Owners = owners.Select(OwnerDto.From).ToList() });
    }

    /// <summary>The owners of the given timers, series rules and recordings, in one call.</summary>
    [HttpPost("Lookup")]
    [Authorize(Policy = LiveTvAccess)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LookupResponse>> Lookup([FromBody] LookupBody body, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return OwnersNotAvailable();
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        var itemIds = (body?.ItemIds ?? new List<string>())
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var lookup = await _service.LookupAsync(
            body?.TimerIds ?? new List<string>(),
            body?.SeriesTimerIds ?? new List<string>(),
            itemIds,
            userId.Value,
            cancellationToken).ConfigureAwait(false);

        return Ok(new LookupResponse
        {
            Timers = lookup.Timers.ToDictionary(p => p.Key, p => p.Value.Select(OwnerDto.From).ToList()),
            SeriesTimers = lookup.SeriesTimers.ToDictionary(p => p.Key, p => p.Value.Select(OwnerDto.From).ToList()),
            Items = lookup.Items.ToDictionary(p => p.Key.ToString("N"), p => p.Value.Select(OwnerDto.From).ToList()),
            RecordingItemIds = lookup.RecordingItemIds?.Select(i => i.ToString("N")).ToList(),
        });
    }

    /// <summary>Everything the caller owns. Timer ids may be stale; clients intersect them with the live timer list.</summary>
    [HttpGet("Mine")]
    [Authorize(Policy = LiveTvAccess)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MineResponse>> Mine(CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return OwnersNotAvailable();
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        var mine = await _service.GetMineAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        return Ok(new MineResponse
        {
            TimerIds = mine.TimerIds.ToList(),
            SeriesTimerIds = mine.SeriesTimerIds.ToList(),
            ItemIds = mine.ItemIds.Select(i => i.ToString("N")).ToList(),
        });
    }

    private bool ProgramVisible(string programId, Guid userId)
    {
        var program = _host.GetProgram(programId);
        return program != null && _host.IsVisibleTo(program.ItemId, userId);
    }
}

/// <summary>An error as both servers' owners endpoints send it: {"error": "..."}.</summary>
public sealed class ErrorResponse
{
    public ErrorResponse(string error)
    {
        Error = error;
    }

    [JsonPropertyName("error")]
    public string Error { get; }
}

public sealed class RecordOwnerBody
{
    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }
}

public sealed class RecordSeriesOwnerBody
{
    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement>? Settings { get; set; }
}

public sealed class ClaimBody
{
    /// <summary>timer, series or item.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

public sealed class LookupBody
{
    [JsonPropertyName("timerIds")]
    public List<string>? TimerIds { get; set; }

    [JsonPropertyName("seriesTimerIds")]
    public List<string>? SeriesTimerIds { get; set; }

    [JsonPropertyName("itemIds")]
    public List<string>? ItemIds { get; set; }
}

public sealed class OwnerDto
{
    /// <summary>The user's id in 32-character N format.</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }

    public static OwnerDto From(OwnerInfo owner) => new()
    {
        UserId = owner.UserId.ToString("N"),
        Name = owner.Name,
        ImageTag = owner.ImageTag,
    };
}

public sealed class OwnershipResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("owners")]
    public List<OwnerDto> Owners { get; set; } = new();

    [JsonPropertyName("joined")]
    public bool Joined { get; set; }

    public static OwnershipResponse From(OwnershipOutcome outcome) => new()
    {
        Id = outcome.Id,
        Owners = outcome.Owners.Select(OwnerDto.From).ToList(),
        Joined = outcome.Joined,
    };
}

public sealed class ClaimResponse
{
    [JsonPropertyName("owners")]
    public List<OwnerDto> Owners { get; set; } = new();
}

public sealed class LookupResponse
{
    [JsonPropertyName("timers")]
    public Dictionary<string, List<OwnerDto>> Timers { get; set; } = new();

    [JsonPropertyName("seriesTimers")]
    public Dictionary<string, List<OwnerDto>> SeriesTimers { get; set; } = new();

    [JsonPropertyName("items")]
    public Dictionary<string, List<OwnerDto>> Items { get; set; } = new();

    /// <summary>
    /// The asked items that are Live TV recordings the caller may see, owned or not, in N format.
    /// Left out when the server can't tell, which clients read as "unknown", never "not a recording".
    /// </summary>
    [JsonPropertyName("recordingItemIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RecordingItemIds { get; set; }
}

public sealed class MineResponse
{
    [JsonPropertyName("timerIds")]
    public List<string> TimerIds { get; set; } = new();

    [JsonPropertyName("seriesTimerIds")]
    public List<string> SeriesTimerIds { get; set; } = new();

    [JsonPropertyName("itemIds")]
    public List<string> ItemIds { get; set; } = new();
}
