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
    [HttpGet("Time")]
    [Authorize]
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

        return new ServerTimeResponse
        {
            UtcTime = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            LocalTime = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            UtcOffsetSeconds = (int)zone.GetUtcOffset(utc).TotalSeconds,
            TimeZoneId = zone.Id,
            IsDaylightSavingTime = zone.IsDaylightSavingTime(utc),
        };
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
}
