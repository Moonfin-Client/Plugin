using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Calendar;

namespace Moonfin.Server.Api;

/// <summary>
/// The calendar's Remind me button. A reminder fires once, when its episode or movie lands in the
/// library, through the same notifications as Seerr.
/// </summary>
[ApiController]
[Route("Moonfin/Calendar")]
[Produces(MediaTypeNames.Application.Json)]
public class CalendarController : ControllerBase
{
    private readonly CalendarReminderStore _reminders;

    public CalendarController(CalendarReminderStore reminders)
    {
        _reminders = reminders;
    }

    [HttpGet("Reminders")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetReminders()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        return Ok(new { reminders = _reminders.List(userId.Value) });
    }

    [HttpPost("Reminders")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SaveReminder([FromBody] CalendarReminder? body)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        var reminder = CalendarReminders.Normalize(body, out var error);
        if (reminder == null)
        {
            return BadRequest(new { error });
        }

        var stored = _reminders.Upsert(userId.Value, reminder);
        return stored == null ? BadRequest(new { error = CalendarReminders.TooManyError }) : Ok(stored);
    }

    [HttpDelete("Reminders/{key}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteReminder([FromRoute] string key)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "User not authenticated" });
        }

        _reminders.Remove(userId.Value, key);
        return NoContent();
    }
}
