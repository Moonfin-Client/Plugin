using System.Collections;
using System.Net.Mime;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Api;

/// <summary>
/// Whether the signed in user's access schedule lets them in right now, and how long until that
/// changes. Jellyfin judges the schedule against the server's local time, so a client in another
/// time zone can't work this out itself; this answers it with the same rule Jellyfin enforces,
/// as relative durations a client can set a timer from without knowing the server's clock.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class AccessWindowController : ControllerBase
{
    private readonly IServiceProvider _services;

    public AccessWindowController(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>The user's access window: allowed now, and seconds until it closes or next opens.</summary>
    /// <remarks>
    /// Skips the access-schedule check: a user outside their hours is exactly the one whose client
    /// needs to know when the account next opens.
    /// </remarks>
    [HttpGet("AccessWindow")]
    [Authorize(Policy = "IgnoreParentalControl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public ActionResult<AccessWindowResponse> GetAccessWindow()
    {
        // Jellyfin doesn't hold administrators to a schedule
        if (this.IsAdminFromClaims())
        {
            return Ok(new AccessWindowResponse { Allowed = true });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId is not { } id || id == Guid.Empty)
        {
            return BadRequest();
        }

        var user = UserReflection.ResolveUser(_services, id);
        var entries = user == null ? null : ReadSchedule(user);
        if (entries == null)
        {
            // Not Implemented, like a plugin without the endpoint, so clients judge it themselves
            return Problem("The user's access schedule couldn't be read on this server version.", statusCode: StatusCodes.Status501NotImplemented);
        }

        return Ok(Evaluate(entries, DateTimeOffset.UtcNow, TimeZoneInfo.Local));
    }

    /// <summary>
    /// The user's access schedule entries, read by name because Jellyfin's User and AccessSchedule
    /// types moved between 10.10 and 10.11. Null when they can't be read.
    /// </summary>
    internal static List<ScheduleEntry>? ReadSchedule(object user)
    {
        try
        {
            if (user.GetType().GetProperty("AccessSchedules")?.GetValue(user) is not IEnumerable schedules)
            {
                return null;
            }

            var entries = new List<ScheduleEntry>();
            foreach (var schedule in schedules)
            {
                var type = schedule.GetType();
                var day = type.GetProperty("DayOfWeek")?.GetValue(schedule)?.ToString();
                var start = type.GetProperty("StartHour")?.GetValue(schedule);
                var end = type.GetProperty("EndHour")?.GetValue(schedule);
                if (day == null || start == null || end == null)
                {
                    return null;
                }

                entries.Add(new ScheduleEntry(day, Convert.ToDouble(start), Convert.ToDouble(end)));
            }

            return entries;
        }
        catch
        {
            return null;
        }
    }

    // Long enough to find any change. The schedule repeats every week, so if nothing changes within a
    // week, nothing ever will. The extra day covers a daylight saving change moving the next change
    // up to an hour past a week away.
    internal static readonly TimeSpan DefaultLookAhead = TimeSpan.FromDays(8);

    /// <summary>
    /// The access window at <paramref name="now"/> for a server in <paramref name="zone"/>: allowed
    /// when there are no entries or <paramref name="now"/> falls in one of the schedule's periods,
    /// with the time until that period ends or the next one starts. The time is left out when it's
    /// not within <paramref name="lookAhead"/>.
    /// </summary>
    internal static AccessWindowResponse Evaluate(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset now, TimeZoneInfo zone, TimeSpan? lookAhead = null)
    {
        if (entries.Count == 0)
        {
            return new AccessWindowResponse { Allowed = true };
        }

        now = now.ToUniversalTime();
        var limit = now + (lookAhead ?? DefaultLookAhead);
        var periods = BuildPeriods(entries, now, limit, zone);
        var current = periods.Find(p => p.Start <= now && now < p.End);
        var allowed = current != default;
        var change = allowed ? current.End : periods.Find(p => p.Start > now).Start;

        var response = new AccessWindowResponse { Allowed = allowed };
        // A period cut off at the limit doesn't end there, and no next period means no opening
        if (change > now && change < limit)
        {
            // Rounded up, so a timer set from it never fires before the change
            var seconds = (long)Math.Ceiling((change - now).TotalSeconds);
            if (allowed)
            {
                response.SecondsUntilClose = seconds;
            }
            else
            {
                response.SecondsUntilOpen = seconds;
            }
        }

        return response;
    }

    /// <summary>
    /// The schedule as UTC periods between <paramref name="from"/> and <paramref name="to"/> for a
    /// server in <paramref name="zone"/>, sorted, and merged where they overlap or touch, judged the
    /// way Jellyfin judges it (UserEntityExtensions.IsParentalScheduleAllowed): an entry lets the
    /// user in on its days while the server's local hour is between its start and end, both
    /// included. While the zone's UTC offset holds, the local time is UTC plus that offset, so each
    /// entry's hours are shifted by the offset and cut to that stretch. That way an hour skipped by
    /// daylight saving drops out and a repeated one counts twice.
    /// </summary>
    internal static List<AccessPeriod> BuildPeriods(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var periods = new List<AccessPeriod>();
        foreach (var (start, end, offset) in OffsetStretches(from.ToUniversalTime(), to.ToUniversalTime(), zone))
        {
            for (var day = (start + offset).Date; day <= (end + offset).Date; day = day.AddDays(1))
            {
                foreach (var entry in entries.Where(e => e.Covers(day.DayOfWeek)))
                {
                    // Jellyfin compares the local hour of the day, 0 up to 24, with the hours as stored,
                    // so an end past 24 lasts to midnight and a start below 0 is midnight
                    var open = (long)Math.Ceiling(Math.Clamp(entry.StartHour, 0, 24) * TimeSpan.TicksPerHour);
                    var close = (long)Math.Min(Math.Floor(entry.EndHour * TimeSpan.TicksPerHour) + 1, TimeSpan.TicksPerDay);
                    var midnight = day.Ticks - offset.Ticks;
                    var period = new AccessPeriod(
                        Later(start, new DateTimeOffset(midnight + open, TimeSpan.Zero)),
                        Earlier(end, new DateTimeOffset(midnight + close, TimeSpan.Zero)));
                    if (period.Start < period.End)
                    {
                        periods.Add(period);
                    }
                }
            }
        }

        periods.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<AccessPeriod>();
        foreach (var period in periods)
        {
            if (merged.Count > 0 && period.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Later(merged[^1].End, period.End) };
            }
            else
            {
                merged.Add(period);
            }
        }

        return merged;
    }

    // Offset changes are further apart than this in today's rules: the closest are Morocco's around
    // Ramadan, about five weeks apart. Half that leaves room for a rule changed at short notice.
    private static readonly TimeSpan OffsetChangeSpacing = TimeSpan.FromDays(14);

    /// <summary>
    /// The stretches between the two instants over which the zone's UTC offset holds, split where it
    /// changes, found to the tick. .NET has no call for the next change, but a piece shorter than
    /// <see cref="OffsetChangeSpacing"/> holds one at most, so its ends show whether it does, and a
    /// binary search finds it. The default look-ahead is one piece.
    /// </summary>
    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End, TimeSpan Offset)> OffsetStretches(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var start = from;
        var offset = zone.GetUtcOffset(from);
        for (var piece = from; piece < to; piece += OffsetChangeSpacing)
        {
            var low = piece;
            var high = Earlier(piece + OffsetChangeSpacing, to);
            if (zone.GetUtcOffset(high) == offset)
            {
                continue;
            }

            while (high.UtcTicks - low.UtcTicks > 1)
            {
                var middle = low.AddTicks((high.UtcTicks - low.UtcTicks) / 2);
                (low, high) = zone.GetUtcOffset(middle) == offset ? (middle, high) : (low, middle);
            }

            yield return (start, high, offset);
            start = high;
            offset = zone.GetUtcOffset(high);
        }

        yield return (start, to, offset);
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

/// <summary>A time in which the schedule lets the user in, from Start up to but not including End.</summary>
internal readonly record struct AccessPeriod(DateTimeOffset Start, DateTimeOffset End);

/// <summary>One access schedule entry: a day name as Jellyfin's DynamicDayOfWeek writes it, and the hours.</summary>
internal readonly record struct ScheduleEntry(string Day, double StartHour, double EndHour)
{
    /// <summary>Jellyfin's DayOfWeekHelper.Contains.</summary>
    public bool Covers(DayOfWeek day) => Day switch
    {
        "Everyday" => true,
        "Weekday" => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
        "Weekend" => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
        _ => Enum.TryParse<DayOfWeek>(Day, out var named) && named == day,
    };
}

public class AccessWindowResponse
{
    /// <summary>True when the user's schedule lets them in now. Always true for an administrator or a user with no schedule.</summary>
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }

    /// <summary>
    /// When allowed, seconds until the schedule closes, rounded up. Left out when allowed with no
    /// schedule, or when it doesn't close within a week.
    /// </summary>
    [JsonPropertyName("secondsUntilClose")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SecondsUntilClose { get; set; }

    /// <summary>
    /// When not allowed, seconds until the schedule next opens, rounded up. Left out when it
    /// doesn't open within a week.
    /// </summary>
    [JsonPropertyName("secondsUntilOpen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SecondsUntilOpen { get; set; }
}
