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

    // The schedule repeats every week, so a change that ever comes comes within this
    private static readonly TimeSpan LookAhead = TimeSpan.FromDays(8);

    /// <summary>
    /// The access window at <paramref name="now"/> for a server in <paramref name="zone"/>, judged
    /// the way Jellyfin judges it: allowed when no entries, or when any entry's day matches the
    /// server's local day and the local hour is between its start and end, both included. Adjacent
    /// and overlapping entries run together, so the durations are to the real change, and the
    /// server's daylight saving changes are counted.
    /// </summary>
    internal static AccessWindowResponse Evaluate(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        var start = now.ToUniversalTime();
        var allowed = IsAllowed(entries, start, zone);
        var response = new AccessWindowResponse { Allowed = allowed };
        if (entries.Count == 0)
        {
            return response;
        }

        var change = NextChange(entries, start, zone, allowed);
        if (change is { } at)
        {
            // Rounded up, so a timer set from it never fires before the change
            var seconds = (long)Math.Ceiling((at - start).TotalSeconds);
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

    /// <summary>Jellyfin's own rule (UserEntityExtensions.IsParentalScheduleAllowed), on <paramref name="zone"/>.</summary>
    internal static bool IsAllowed(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset instant, TimeZoneInfo zone)
    {
        if (entries.Count == 0)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var hour = local.TimeOfDay.TotalHours;
        foreach (var entry in entries)
        {
            if (entry.Covers(local.DayOfWeek) && hour >= entry.StartHour && hour <= entry.EndHour)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first instant after <paramref name="start"/> at which <see cref="IsAllowed"/> stops
    /// being <paramref name="allowed"/>, or null when it doesn't within the look-ahead. The answer
    /// can only change at an entry's start, just after an entry's end, or where the server's offset
    /// changes, so only those instants are tried.
    /// </summary>
    private static DateTimeOffset? NextChange(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset start, TimeZoneInfo zone, bool allowed)
    {
        var limit = start + LookAhead;
        var candidates = new List<DateTimeOffset>();

        var today = TimeZoneInfo.ConvertTime(start, zone).DateTime.Date;
        for (var date = today.AddDays(-1); date <= today.Add(LookAhead).AddDays(1); date = date.AddDays(1))
        {
            foreach (var entry in entries)
            {
                if (!entry.Covers(date.DayOfWeek))
                {
                    continue;
                }

                AddInstants(candidates, date.AddTicks(HourTicks(entry.StartHour)), zone);
                AddInstants(candidates, date.AddTicks(HourTicks(entry.EndHour)), zone);
            }
        }

        AddOffsetChanges(candidates, start, limit, zone);

        candidates.Sort();
        foreach (var candidate in candidates)
        {
            if (candidate < start || candidate > limit)
            {
                continue;
            }

            // An opening happens at the start itself; a closing a moment after the end
            if (IsAllowed(entries, candidate, zone) != allowed)
            {
                return candidate;
            }

            var after = candidate.AddTicks(1);
            if (IsAllowed(entries, after, zone) != allowed)
            {
                return after;
            }
        }

        return null;
    }

    private static long HourTicks(double hours) =>
        (long)Math.Ceiling(Math.Clamp(hours, 0, 48) * TimeSpan.TicksPerHour);

    /// <summary>
    /// The instants at which the server's wall clock reads <paramref name="local"/>: none in an
    /// hour skipped by daylight saving, two in an hour repeated by it.
    /// </summary>
    private static void AddInstants(List<DateTimeOffset> into, DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var nominal = new DateTimeOffset(wall, TimeSpan.Zero);
        var offsets = new HashSet<TimeSpan>
        {
            zone.GetUtcOffset(nominal.AddDays(-1)),
            zone.GetUtcOffset(nominal),
            zone.GetUtcOffset(nominal.AddDays(1)),
        };

        foreach (var offset in offsets)
        {
            var instant = new DateTimeOffset(wall.Ticks - offset.Ticks, TimeSpan.Zero);
            if (TimeZoneInfo.ConvertTime(instant, zone).DateTime == wall)
            {
                into.Add(instant);
            }
        }
    }

    /// <summary>Every instant between the two at which the zone's offset changes, to the tick.</summary>
    private static void AddOffsetChanges(List<DateTimeOffset> into, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var previous = from;
        var offset = zone.GetUtcOffset(from);
        for (var probe = from.AddHours(1); previous < to; probe = probe.AddHours(1))
        {
            var next = zone.GetUtcOffset(probe);
            if (next != offset)
            {
                var low = previous;
                var high = probe;
                while (high.UtcTicks - low.UtcTicks > 1)
                {
                    var middle = low.AddTicks((high.UtcTicks - low.UtcTicks) / 2);
                    if (zone.GetUtcOffset(middle) == offset)
                    {
                        low = middle;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                into.Add(high);
                offset = next;
            }

            previous = probe;
        }
    }
}

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
