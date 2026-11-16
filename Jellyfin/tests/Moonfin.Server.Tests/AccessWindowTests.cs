using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class AccessWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TimeZoneInfo Fixed(string offset) =>
        TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.Parse(offset), "Test", "Test");

    // Null on a host with no tz database, where the test is skipped quietly
    private static TimeZoneInfo? Named(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
    }

    private static ScheduleEntry Entry(string day, double start, double end) => new(day, start, end);

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void NoSchedule_IsAllowedWithNoClosing()
    {
        var window = AccessWindowController.Evaluate([], At(2026, 11, 16, 12), Utc);

        Assert.True(window.Allowed);
        Assert.Null(window.SecondsUntilClose);
        Assert.Null(window.SecondsUntilOpen);
    }

    [Fact]
    public void EveryHourOfEveryDay_NeverCloses()
    {
        var window = AccessWindowController.Evaluate([Entry("Everyday", 0, 24)], At(2026, 11, 16, 12), Utc);

        Assert.True(window.Allowed);
        Assert.Null(window.SecondsUntilClose);
    }

    [Fact]
    public void Allowed_ClosesTheSecondAfterTheEndHour()
    {
        // Monday 2026-11-16 19:00; Jellyfin still allows 20:00:00 itself
        var window = AccessWindowController.Evaluate([Entry("Weekday", 8, 20)], At(2026, 11, 16, 19), Utc);

        Assert.True(window.Allowed);
        Assert.Equal(3601, window.SecondsUntilClose);
        Assert.Null(window.SecondsUntilOpen);
    }

    [Fact]
    public void Blocked_OpensAtTheStartHour()
    {
        // Monday 21:00, next opening Tuesday 08:00
        var window = AccessWindowController.Evaluate([Entry("Weekday", 8, 20)], At(2026, 11, 16, 21), Utc);

        Assert.False(window.Allowed);
        Assert.Equal(11 * 3600, window.SecondsUntilOpen);
        Assert.Null(window.SecondsUntilClose);
    }

    [Fact]
    public void Blocked_OverTheWeekendOpensMonday()
    {
        // Friday 2026-11-20 21:00 to Monday 08:00
        var window = AccessWindowController.Evaluate([Entry("Weekday", 8, 20)], At(2026, 11, 20, 21), Utc);

        Assert.Equal((3 + 2 * 24 + 8) * 3600, window.SecondsUntilOpen);
    }

    [Fact]
    public void AdjacentEntries_RunTogether()
    {
        // Monday 20:00-24:00 then Tuesday 00:00-08:00 is one spell, closing after Tuesday 08:00
        var entries = new[] { Entry("Monday", 20, 24), Entry("Tuesday", 0, 8) };
        var window = AccessWindowController.Evaluate(entries, At(2026, 11, 16, 21), Utc);

        Assert.True(window.Allowed);
        Assert.Equal(11 * 3600 + 1, window.SecondsUntilClose);
    }

    [Fact]
    public void HalfHours_AreHonoured()
    {
        var window = AccessWindowController.Evaluate([Entry("Everyday", 7.5, 21.5)], At(2026, 11, 16, 6), Utc);

        Assert.False(window.Allowed);
        Assert.Equal(5400, window.SecondsUntilOpen);
    }

    [Fact]
    public void JudgedOnTheServersZone()
    {
        // 19:00Z is 00:30 Tuesday in India: closed for a Monday-only schedule, open for Tuesday
        var india = Fixed("05:30");
        Assert.False(AccessWindowController.Evaluate([Entry("Monday", 0, 24)], At(2026, 11, 16, 19), india).Allowed);
        Assert.True(AccessWindowController.Evaluate([Entry("Tuesday", 0, 1)], At(2026, 11, 16, 19), india).Allowed);
    }

    [Fact]
    public void AnEntryThatNeverOpens_GivesNoOpening()
    {
        var window = AccessWindowController.Evaluate([Entry("Everyday", 20, 8)], At(2026, 11, 16, 12), Utc);

        Assert.False(window.Allowed);
        Assert.Null(window.SecondsUntilOpen);
    }

    [Fact]
    public void FallBack_CountsTheRepeatedHour()
    {
        var newYork = Named("America/New_York");
        if (newYork == null)
        {
            return;
        }

        // Sunday 2026-11-01 00:30 EDT (04:30Z); 03:00 EST is 08:00Z, three and a half hours on
        var window = AccessWindowController.Evaluate([Entry("Sunday", 3, 12)], At(2026, 11, 1, 4, 30), newYork);

        Assert.False(window.Allowed);
        Assert.Equal(12600, window.SecondsUntilOpen);
    }

    [Fact]
    public void SpringForward_OpensWhenTheClockJumpsIntoTheWindow()
    {
        var newYork = Named("America/New_York");
        if (newYork == null)
        {
            return;
        }

        // Sunday 2026-03-08 01:00 EST (06:00Z). 02:30 never happens; at 07:00Z the clock jumps
        // from 02:00 EST to 03:00 EDT, inside 02:30-04:00
        var window = AccessWindowController.Evaluate([Entry("Sunday", 2.5, 4)], At(2026, 3, 8, 6), newYork);

        Assert.False(window.Allowed);
        Assert.Equal(3600, window.SecondsUntilOpen);
    }

    public static IEnumerable<object[]> Zones() =>
        new[] { "America/New_York", "Australia/Sydney", "Europe/London", "Asia/Kolkata", "Pacific/Chatham" }
            .Select(id => new object[] { id });

    private static readonly ScheduleEntry[][] Schedules =
    [
        [Entry("Weekday", 8, 20)],
        [Entry("Everyday", 7.5, 21.5)],
        [Entry("Monday", 20, 24), Entry("Tuesday", 0, 8)],
        [Entry("Saturday", 1.5, 3), Entry("Sunday", 1.5, 3), Entry("Weekend", 2, 2.5)],
        [Entry("Sunday", 0, 24), Entry("Wednesday", 2, 3)],
        [Entry("Everyday", 0, 24)],
    ];

    /// <summary>
    /// Evaluate agrees with a scan of Jellyfin's rule a minute at a time, refined to the tick,
    /// over weeks that hold each zone's daylight saving changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Zones))]
    public void AgreesWithAScan(string zoneId)
    {
        var zone = Named(zoneId);
        if (zone == null)
        {
            return;
        }

        var starts = new[] { At(2026, 3, 7, 0), At(2026, 3, 28, 0), At(2026, 4, 4, 0), At(2026, 9, 26, 0), At(2026, 10, 24, 0), At(2026, 10, 31, 0) };
        foreach (var entries in Schedules)
        {
            foreach (var weekStart in starts)
            {
                for (var now = weekStart; now < weekStart.AddDays(2); now = now.AddMinutes(97))
                {
                    var window = AccessWindowController.Evaluate(entries, now, zone);
                    var allowed = AccessWindowController.IsAllowed(entries, now, zone);
                    var expected = Scan(entries, now, zone, allowed);

                    Assert.Equal(allowed, window.Allowed);
                    Assert.Equal(allowed ? expected : null, window.SecondsUntilClose);
                    Assert.Equal(allowed ? null : expected, window.SecondsUntilOpen);
                }
            }
        }
    }

    private static long? Scan(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset now, TimeZoneInfo zone, bool allowed)
    {
        for (var minute = 1; minute <= 8 * 24 * 60; minute++)
        {
            var probe = now.AddMinutes(minute);
            if (AccessWindowController.IsAllowed(entries, probe, zone) == allowed)
            {
                continue;
            }

            var low = probe.AddMinutes(-1);
            var high = probe;
            while (high.UtcTicks - low.UtcTicks > 1)
            {
                var middle = low.AddTicks((high.UtcTicks - low.UtcTicks) / 2);
                if (AccessWindowController.IsAllowed(entries, middle, zone) == allowed)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return (long)Math.Ceiling((high - now).TotalSeconds);
        }

        return null;
    }

    private enum FakeDay
    {
        Sunday, Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Everyday, Weekday, Weekend,
    }

    private sealed class FakeSchedule
    {
        public FakeDay DayOfWeek { get; set; }

        public double StartHour { get; set; }

        public double EndHour { get; set; }
    }

    private sealed class FakeUser
    {
        public ICollection<FakeSchedule> AccessSchedules { get; set; } = new List<FakeSchedule>();
    }

    [Fact]
    public void ReadSchedule_ReadsJellyfinsShapeByName()
    {
        var user = new FakeUser();
        user.AccessSchedules.Add(new FakeSchedule { DayOfWeek = FakeDay.Weekday, StartHour = 8, EndHour = 20.5 });
        user.AccessSchedules.Add(new FakeSchedule { DayOfWeek = FakeDay.Sunday, StartHour = 0, EndHour = 24 });

        var entries = AccessWindowController.ReadSchedule(user);

        Assert.Equal([Entry("Weekday", 8, 20.5), Entry("Sunday", 0, 24)], entries);
    }

    [Fact]
    public void ReadSchedule_IsNullForAnotherShape()
    {
        Assert.Null(AccessWindowController.ReadSchedule(new object()));
    }

    [Fact]
    public void Response_UsesTheKeysClientsRead()
    {
        var open = JsonSerializer.Serialize(new AccessWindowResponse { Allowed = true, SecondsUntilClose = 60 });
        var closed = JsonSerializer.Serialize(new AccessWindowResponse { Allowed = false });

        Assert.Equal("{\"allowed\":true,\"secondsUntilClose\":60}", open);
        Assert.Equal("{\"allowed\":false}", closed);
    }

    [Theory]
    [InlineData(typeof(AccessWindowController), nameof(AccessWindowController.GetAccessWindow))]
    [InlineData(typeof(MoonfinController), nameof(MoonfinController.Ping))]
    public void Endpoint_IsReachableOutsideTheAccessSchedule(Type controller, string action)
    {
        var authorize = controller.GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("IgnoreParentalControl", authorize!.Policy);
    }
}
