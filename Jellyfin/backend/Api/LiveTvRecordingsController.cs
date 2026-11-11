using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.LiveTv;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Incomplete Live TV recordings: their state for Moonfin's badge and detail line, Keep as
/// Complete, the Record prompt's complete-showing request, and the server-wide settings.
/// Permissions mirror Jellyfin's own Live TV policies.
/// </summary>
[ApiController]
[Route("Moonfin/LiveTv")]
[Produces(MediaTypeNames.Application.Json)]
public class LiveTvRecordingsController : ControllerBase
{
    private const string LiveTvAccess = "LiveTvAccess";
    private const string LiveTvManagement = "LiveTvManagement";

    private readonly IncompleteRecordingService _service;

    public LiveTvRecordingsController(IncompleteRecordingService service)
    {
        _service = service;
    }

    /// <summary>
    /// The incomplete state of each listed item, or of every incomplete item when none are listed.
    /// Items that aren't incomplete are left out.
    /// </summary>
    [HttpGet("Recordings/Incomplete")]
    [Authorize(Policy = LiveTvAccess)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<IncompleteRecordingStateDto>>> GetIncomplete(
        [FromQuery] string? itemIds,
        CancellationToken cancellationToken)
    {
        var ids = (itemIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Take(500)
            .ToList();

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        // A list was asked about but none of it names an item: that is no items, not all of them.
        if (ids.Count == 0 && !string.IsNullOrWhiteSpace(itemIds))
        {
            return Ok(Array.Empty<IncompleteRecordingStateDto>());
        }

        return Ok(await _service.GetStatesAsync(ids.Count > 0 ? ids : null, userId.Value, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Keep as Complete: dismisses the item's incomplete record for everyone and removes its tag.</summary>
    [HttpPost("Recordings/{itemId}/Dismiss")]
    [Authorize(Policy = LiveTvManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Dismiss([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        return await _service.DismissAsync(itemId, userId.Value, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : NotFound();
    }

    /// <summary>
    /// Asks for a later showing of a program's episode to be recorded. Repeating the call for the
    /// same episode joins the same request, so a client can safely retry it.
    /// </summary>
    [HttpPost("CompleteShowing")]
    [Authorize(Policy = LiveTvManagement)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompleteShowingResponse>> RequestCompleteShowing(
        [FromBody] CompleteShowingBody body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.ProgramId))
        {
            return BadRequest("programId is required.");
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        var (request, refusal) = await _service.RequestCompleteShowingAsync(body.ProgramId, userId.Value, cancellationToken).ConfigureAwait(false);
        if (request == null)
        {
            return refusal == CompleteShowingRefusal.NoEpisodeNumbers
                ? BadRequest("The program needs season and episode numbers, or movie classification with a title and description.")
                : NotFound();
        }

        return Ok(new CompleteShowingResponse
        {
            State = IncompleteRecordingRules.DisplayState(request),
            ScheduledStart = request.ScheduledStart,
            ChannelName = request.ScheduledChannelName,
        });
    }

    [HttpGet("CompleteShowing/Options")]
    [Authorize(Policy = LiveTvManagement)]
    public async Task<ActionResult<CompleteShowingOptions>> GetCompleteShowingOptions([FromQuery] string programId, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        return userId == null ? Unauthorized() : Ok(await _service.GetCompleteShowingOptionsAsync(programId, userId.Value, cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("CompleteShowing/Suggestion")]
    [Authorize(Policy = LiveTvManagement)]
    public async Task<ActionResult> ScheduleSuggestedShowing([FromBody] SuggestedShowingBody body, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(body.ProgramId) || body.Showing == null)
        {
            return BadRequest();
        }

        return await _service.ScheduleSuggestedShowingAsync(body.ProgramId, body.Showing, userId.Value, cancellationToken).ConfigureAwait(false)
            ? NoContent() : Conflict("The selected airing is no longer available or no longer matches.");
    }

    /// <summary>The server-wide settings.</summary>
    [HttpGet("Settings/IncompleteRecordings")]
    [Authorize(Policy = LiveTvAccess)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IncompleteRecordingSettings> GetSettings() =>
        Ok(IncompleteRecordingConfig.Read());

    /// <summary>Changes the server-wide settings. Administrators only.</summary>
    [HttpPost("Settings/IncompleteRecordings")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IncompleteRecordingSettings> SaveSettings([FromBody] IncompleteRecordingSettings settings)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        plugin.Configuration.LiveTvDetectIncompleteRecordings = settings.Enabled;
        plugin.Configuration.LiveTvAutoRecordCompleteShowing = settings.AutoRecordComplete;
        plugin.Configuration.LiveTvIncompleteThresholdPercent = IncompleteRecordingRules.ClampThreshold(settings.ThresholdPercent);
        plugin.Configuration.LiveTvDeleteIncompleteCopies = settings.DeleteIncompleteCopies;
        plugin.SaveConfiguration();
        return Ok(IncompleteRecordingConfig.Read());
    }
}

public sealed class SuggestedShowingBody
{
    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }

    [JsonPropertyName("showing")]
    public SuggestedShowing? Showing { get; set; }
}

public sealed class CompleteShowingBody
{
    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }
}

public sealed class CompleteShowingResponse
{
    /// <summary>scheduled, recorded or notFound.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("scheduledStart")]
    public DateTime? ScheduledStart { get; set; }

    [JsonPropertyName("channelName")]
    public string? ChannelName { get; set; }
}
