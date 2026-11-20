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
    public void AnEndPastMidnight_ClosesAtMidnight()
    {
        // Jellyfin stores hours as given; Monday 08:00-25:00 ends when Monday does
        var window = AccessWindowController.Evaluate([Entry("Monday", 8, 25)], At(2026, 11, 16, 19), Utc);

        Assert.True(window.Allowed);
        Assert.Equal(5 * 3600, window.SecondsUntilClose);
    }

    [Fact]
    public void AStartBeforeMidnight_OpensAtMidnight()
    {
        var window = AccessWindowController.Evaluate([Entry("Tuesday", -2, 6)], At(2026, 11, 16, 19), Utc);

        Assert.False(window.Allowed);
        Assert.Equal(5 * 3600, window.SecondsUntilOpen);
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

    [Fact]
    public void FallBack_SplitsAPeriodEndingInTheRepeatedHour()
    {
        var newYork = Named("America/New_York");
        if (newYork == null)
        {
            return;
        }

        // Sunday 2026-11-01 00:00-01:30: 04:00Z-05:30Z on EDT, then the clock goes back to 01:00 EST
        // at 06:00Z and reads 01:00-01:30 again until 06:30Z
        var periods = AccessWindowController.BuildPeriods([Entry("Sunday", 0, 1.5)], At(2026, 11, 1, 0), At(2026, 11, 2, 0), newYork);

        Assert.Equal(
            [new AccessPeriod(At(2026, 11, 1, 4), At(2026, 11, 1, 5, 30).AddTicks(1)), new AccessPeriod(At(2026, 11, 1, 6), At(2026, 11, 1, 6, 30).AddTicks(1))],
            periods);
    }

    [Fact]
    public void BuildPeriods_CoversTheRangeAskedFor()
    {
        // Two weeks from Monday 2026-11-16: ten weekdays, the last cut off at the range's end
        var periods = AccessWindowController.BuildPeriods([Entry("Weekday", 8, 20)], At(2026, 11, 16, 0), At(2026, 11, 27, 12), Utc);

        Assert.Equal(10, periods.Count);
        Assert.Equal(new AccessPeriod(At(2026, 11, 16, 8), At(2026, 11, 16, 20).AddTicks(1)), periods[0]);
        Assert.Equal(new AccessPeriod(At(2026, 11, 27, 8), At(2026, 11, 27, 12)), periods[^1]);
    }

    [Fact]
    public void BuildPeriods_FindsBothChangesInAYear()
    {
        var newYork = Named("America/New_York");
        if (newYork == null)
        {
            return;
        }

        // All of 2026, EST at both ends; Sundays 02:30-04:00 local
        var periods = AccessWindowController.BuildPeriods([Entry("Sunday", 2.5, 4)], At(2026, 1, 1, 0), At(2027, 1, 1, 0), newYork);

        Assert.Equal(52, periods.Count);
        // EST: 07:30Z-09:00Z
        Assert.Contains(new AccessPeriod(At(2026, 3, 1, 7, 30), At(2026, 3, 1, 9).AddTicks(1)), periods);
        // Spring forward: 02:30 never comes, the clock jumps to 03:00 EDT at 07:00Z
        Assert.Contains(new AccessPeriod(At(2026, 3, 8, 7), At(2026, 3, 8, 8).AddTicks(1)), periods);
        // EDT: 06:30Z-08:00Z
        Assert.Contains(new AccessPeriod(At(2026, 7, 5, 6, 30), At(2026, 7, 5, 8).AddTicks(1)), periods);
        // Fall back at 06:00Z: 02:30-04:00 EST
        Assert.Contains(new AccessPeriod(At(2026, 11, 1, 7, 30), At(2026, 11, 1, 9).AddTicks(1)), periods);
    }

    [Fact]
    public void SpringForward_SkippedWindow_NamesTheOneAfter()
    {
        var newYork = Named("America/New_York");
        if (newYork == null)
        {
            return;
        }

        // Sunday 02:30-02:45 never happens on 2026-03-08. Just after the 2026-03-01 window (02:46 EST,
        // 07:46Z) the next opening is 2026-03-15 02:30 EDT (06:30Z), 13 days 22:44 on
        var window = AccessWindowController.Evaluate([Entry("Sunday", 2.5, 2.75)], At(2026, 3, 1, 7, 46), newYork);

        Assert.False(window.Allowed);
        Assert.Equal(1205040, window.SecondsUntilOpen);
    }

    [Fact]
    public void AFractionalEnd_ClosesAtJellyfinsTick()
    {
        // Jellyfin still lets the user in at 08:02:24 exactly for an end of 8.04, where the hours times
        // the ticks in an hour rounds to just below that tick
        var monday = At(2026, 11, 16, 0);
        var periods = AccessWindowController.BuildPeriods([Entry("Monday", 8, 8.04)], monday, monday.AddDays(1), Utc);

        Assert.Equal(new AccessPeriod(At(2026, 11, 16, 8), At(2026, 11, 16, 8, 2).AddSeconds(24).AddTicks(1)), Assert.Single(periods));
        Assert.Equal(2, AccessWindowController.Evaluate([Entry("Monday", 8, 8.04)], At(2026, 11, 16, 8, 2).AddSeconds(23), Utc).SecondsUntilClose);
    }

    [Fact]
    public void AbsurdHours_AreSkippedWithoutBreakingTheRest()
    {
        // Jellyfin stores the hours as given; these never let the user in, and mustn't stop the valid entry
        var entries = new[] { Entry("Monday", 8, -100_000_000), Entry("Monday", 100_000_000, 200_000_000), Entry("Monday", double.NaN, 12), Entry("Monday", 9, 17) };
        var window = AccessWindowController.Evaluate(entries, At(2026, 11, 16, 12), Utc);

        Assert.True(window.Allowed);
        // The end hour is included, so it closes a tick after 17:00, rounded up to the next second
        Assert.Equal(5 * 3600 + 1, window.SecondsUntilClose);
    }

    [Fact]
    public void LookAhead_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessWindowController.Evaluate([Entry("Weekday", 8, 20)], At(2026, 11, 16, 12), Utc, TimeSpan.Zero));
    }

    [Fact]
    public void LookAhead_LeavesOutChangesBeyondIt()
    {
        // Friday 21:00, opens Monday 08:00: 59 hours on, past a two-day look-ahead
        var window = AccessWindowController.Evaluate([Entry("Weekday", 8, 20)], At(2026, 11, 20, 21), Utc, TimeSpan.FromDays(2));

        Assert.False(window.Allowed);
        Assert.Null(window.SecondsUntilOpen);
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
        [Entry("Monday", 8, 25), Entry("Friday", -1, 3)],
        [Entry("Sunday", 0, 1.5)],
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
                    var allowed = IsAllowed(entries, now, zone);
                    var expected = Scan(entries, now, zone, allowed);

                    Assert.Equal(allowed, window.Allowed);
                    Assert.Equal(allowed ? expected : null, window.SecondsUntilClose);
                    Assert.Equal(allowed ? null : expected, window.SecondsUntilOpen);
                }
            }
        }
    }

    // Jellyfin's rule (UserEntityExtensions.IsParentalScheduleAllowed) copied as written, in the
    // server's zone; IsAllowed_AgreesWithJellyfin ties it to Jellyfin's own code
    private static bool IsAllowed(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var hour = local.TimeOfDay.TotalHours;
        return entries.Count == 0 || entries.Any(e => e.Covers(local.DayOfWeek) && hour >= e.StartHour && hour <= e.EndHour);
    }

    private static long? Scan(IReadOnlyList<ScheduleEntry> entries, DateTimeOffset now, TimeZoneInfo zone, bool allowed)
    {
        for (var minute = 1; minute <= AccessWindowController.DefaultLookAhead.TotalMinutes; minute++)
        {
            var probe = now.AddMinutes(minute);
            if (IsAllowed(entries, probe, zone) == allowed)
            {
                continue;
            }

            var low = probe.AddMinutes(-1);
            var high = probe;
            while (high.UtcTicks - low.UtcTicks > 1)
            {
                var middle = low.AddTicks((high.UtcTicks - low.UtcTicks) / 2);
                if (IsAllowed(entries, middle, zone) == allowed)
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

    // Jellyfin 10.10's own check of one entry, User.IsParentalScheduleAllowed(AccessSchedule, DateTime),
    // on its own types. It reads date.ToLocalTime(), which leaves a DateTime of kind Local as it is,
    // so it can be handed exact wall-clock times without touching this process's time zone. 10.11
    // moved the same check to UserEntityExtensions.
    private static readonly MethodInfo JellyfinEntryCheck = typeof(Jellyfin.Data.Entities.User)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == "IsParentalScheduleAllowed" && m.GetParameters().Length == 2);

    // The user-level rule around it: allowed with no entries, or when any entry allows
    private static bool JellyfinAllows(IReadOnlyList<ScheduleEntry> entries, DateTime wallClock)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Local);
        return entries.Count == 0 || entries.Any(e => (bool)JellyfinEntryCheck.Invoke(null, [JellyfinSchedule(e), local])!);
    }

    private static Jellyfin.Data.Entities.AccessSchedule JellyfinSchedule(ScheduleEntry entry) =>
        new(Enum.Parse<Jellyfin.Data.Enums.DynamicDayOfWeek>(entry.Day), entry.StartHour, entry.EndHour, Guid.Empty);

    private static readonly ScheduleEntry[][] JellyfinCases = Schedules.Concat(
    [
        [Entry("Sunday", 0, 0)],
        [Entry("Saturday", 23.5, 24), Entry("Weekend", 12, 12.5)],
        [Entry("Wednesday", 22, 6)],
        [Entry("Thursday", 6.25, 18.75), Entry("Tuesday", 0, 24)],
        [],
    ]).ToArray();

    public static IEnumerable<object[]> JellyfinSchedules() =>
        Enumerable.Range(0, JellyfinCases.Length).Select(i => new object[] { i });

    /// <summary>
    /// IsAllowed agrees with Jellyfin's own check on every day of a week: every 7 minutes, and a
    /// second either side of each entry's start and end.
    /// </summary>
    [Theory]
    [MemberData(nameof(JellyfinSchedules))]
    public void IsAllowed_AgreesWithJellyfin(int schedule)
    {
        var entries = JellyfinCases[schedule];
        var monday = new DateTime(2026, 11, 16);
        var moments = new List<DateTime>();
        for (var minute = 0; minute < 7 * 24 * 60; minute += 7)
        {
            moments.Add(monday.AddMinutes(minute));
        }

        for (var day = 0; day < 7; day++)
        {
            foreach (var entry in entries)
            {
                foreach (var hour in new[] { entry.StartHour, entry.EndHour })
                {
                    if (hour is < 0 or > 24)
                    {
                        continue;
                    }

                    var at = monday.AddDays(day).AddTicks((long)(hour * TimeSpan.TicksPerHour));
                    moments.AddRange([at.AddSeconds(-1), at, at.AddSeconds(1)]);
                }
            }
        }

        foreach (var moment in moments)
        {
            var ours = IsAllowed(entries, new DateTimeOffset(moment, TimeSpan.Zero), Utc);
            Assert.True(JellyfinAllows(entries, moment) == ours, $"{moment:ddd HH:mm:ss}: Jellyfin and IsAllowed differ ({string.Join(", ", entries)})");
        }
    }

    [Fact]
    public void ReadSchedule_ReadsJellyfinsUser()
    {
        var user = new Jellyfin.Data.Entities.User("child", "auth", "reset");
        user.AccessSchedules.Add(new Jellyfin.Data.Entities.AccessSchedule(Jellyfin.Data.Enums.DynamicDayOfWeek.Weekday, 8, 20.5, Guid.Empty));
        user.AccessSchedules.Add(new Jellyfin.Data.Entities.AccessSchedule(Jellyfin.Data.Enums.DynamicDayOfWeek.Sunday, 0, 24, Guid.Empty));

        Assert.Equal([Entry("Weekday", 8, 20.5), Entry("Sunday", 0, 24)], AccessWindowController.ReadSchedule(user));
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
    public void Endpoint_IsReachableOutsideTheAccessSchedule(Type controller, string action)
    {
        var authorize = controller.GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("IgnoreParentalControl", authorize!.Policy);
    }
}
