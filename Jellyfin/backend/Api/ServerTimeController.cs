using System.Globalization;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

/// <summary>
/// The server's clock and time zone. Jellyfin judges a user's access schedule against the server's
/// local time, which no stock endpoint reports, so a client in another zone can't work out when
/// the account is open or next opens without it.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class ServerTimeController : ControllerBase
{
    /// <summary>The server's current time, its UTC offset and its time zone.</summary>
    /// <remarks>
    /// Skips the access-schedule check: a user outside their hours is exactly the one whose client
    /// needs the server clock to say when the account next opens.
    /// </remarks>
    [HttpGet("Time")]
    [Authorize(Policy = "IgnoreParentalControl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<ServerTimeResponse> GetTime()
    {
        return Ok(Describe(DateTimeOffset.UtcNow, TimeZoneInfo.Local));
    }

    /// <summary>Builds the response for an instant and zone, so it can be checked without a clock.</summary>
    internal static ServerTimeResponse Describe(DateTimeOffset now, TimeZoneInfo zone)
    {
        var utc = now.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(utc, zone);

        var time = new ServerTimeResponse
        {
            UtcTime = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            LocalTime = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            UtcOffsetSeconds = (int)zone.GetUtcOffset(utc).TotalSeconds,
            TimeZoneId = zone.Id,
            IsDaylightSavingTime = zone.IsDaylightSavingTime(utc),
        };

        var change = NextOffsetChange(utc, zone);
        if (change is { } next)
        {
            time.NextOffsetChangeUtc = next.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            time.NextUtcOffsetSeconds = (int)zone.GetUtcOffset(next).TotalSeconds;
        }

        return time;
    }

    // Far enough to find the next change in any zone that has them, which come at most a year apart
    private const int LookAheadDays = 400;

    /// <summary>
    /// The first instant after <paramref name="utc"/> at which the zone's offset differs from its
    /// offset now, to the second, or null when it doesn't change within the look-ahead. Found by
    /// stepping a day at a time and then halving the day, so it relies only on GetUtcOffset and
    /// not on how the zone's adjustment rules are written.
    /// </summary>
    internal static DateTimeOffset? NextOffsetChange(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var start = utc.ToUniversalTime();
        var offset = zone.GetUtcOffset(start);
        var before = start;
        DateTimeOffset? after = null;

        for (var day = 1; day <= LookAheadDays; day++)
        {
            var probe = start.AddDays(day);
            if (zone.GetUtcOffset(probe) != offset)
            {
                after = probe;
                break;
            }

            before = probe;
        }

        if (after is not { } high)
        {
            return null;
        }

        // The offset is the old one at before and the new one at high; close in to the second
        var low = before;
        while ((high - low).TotalSeconds > 1)
        {
            var middle = low.AddTicks((high - low).Ticks / 2);
            if (zone.GetUtcOffset(middle) == offset)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return new DateTimeOffset(high.UtcTicks - (high.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}

public class ServerTimeResponse
{
    /// <summary>The instant, as ISO 8601 UTC with a trailing Z.</summary>
    [JsonPropertyName("utcTime")]
    public string UtcTime { get; set; } = string.Empty;

    /// <summary>The same instant on the server's wall clock, with no offset. This is what an access schedule is judged against.</summary>
    [JsonPropertyName("localTime")]
    public string LocalTime { get; set; } = string.Empty;

    /// <summary>The server's offset from UTC right now, in seconds, east positive.</summary>
    [JsonPropertyName("utcOffsetSeconds")]
    public int UtcOffsetSeconds { get; set; }

    /// <summary>The server's time zone id as the host names it.</summary>
    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; set; } = string.Empty;

    [JsonPropertyName("isDaylightSavingTime")]
    public bool IsDaylightSavingTime { get; set; }

    /// <summary>
    /// When the server's offset next changes, as ISO 8601 UTC with a trailing Z. Left out of the
    /// response when it doesn't change within about a year, as it is by plugins that predate it, so
    /// clients treat a missing key as no change. A client turning a future time on the server's
    /// wall clock into an instant uses <see cref="NextUtcOffsetSeconds"/> from this change until
    /// the one after it, which this response doesn't describe: for times past that, ask again
    /// after this change.
    /// </summary>
    [JsonPropertyName("nextOffsetChangeUtc")]
    public string? NextOffsetChangeUtc { get; set; }

    /// <summary>The server's offset from UTC after that change, in seconds, east positive, until the change after it. Left out along with it.</summary>
    [JsonPropertyName("nextUtcOffsetSeconds")]
    public int? NextUtcOffsetSeconds { get; set; }
}
