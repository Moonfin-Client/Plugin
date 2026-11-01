using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class ServerTimeTests
{
    private static TimeZoneInfo Zone(string offset) =>
        TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.Parse(offset), "Test", "Test");

    [Fact]
    public void Describe_ReportsUtcAndTheServerWallClock()
    {
        var time = ServerTimeController.Describe(new DateTimeOffset(2026, 3, 1, 23, 30, 0, TimeSpan.Zero), Zone("05:30"));

        Assert.Equal("2026-03-01T23:30:00Z", time.UtcTime);
        Assert.Equal("2026-03-02T05:00:00", time.LocalTime);
        Assert.Equal(19800, time.UtcOffsetSeconds);
        Assert.Equal("Test/Zone", time.TimeZoneId);
    }

    [Fact]
    public void Describe_NegativeOffsetRollsTheDayBack()
    {
        var time = ServerTimeController.Describe(new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.FromHours(3)), Zone("-08:00"));

        Assert.Equal("2026-02-28T23:00:00Z", time.UtcTime);
        Assert.Equal("2026-02-28T15:00:00", time.LocalTime);
        Assert.Equal(-28800, time.UtcOffsetSeconds);
    }

    // Skipped quietly on a host with no tz database, where the zone can't be looked up.
    [Fact]
    public void Describe_FollowsDaylightSavingTransitions()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        var winter = ServerTimeController.Describe(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), zone);
        var summer = ServerTimeController.Describe(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), zone);

        Assert.Equal(-18000, winter.UtcOffsetSeconds);
        Assert.False(winter.IsDaylightSavingTime);
        Assert.Equal(-14400, summer.UtcOffsetSeconds);
        Assert.True(summer.IsDaylightSavingTime);
    }

    // Clients read these exact keys, so a rename would leave them on the device clock unnoticed.
    [Fact]
    public void Response_UsesTheKeysClientsRead()
    {
        var json = JsonSerializer.Serialize(ServerTimeController.Describe(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

        foreach (var key in new[] { "utcTime", "localTime", "utcOffsetSeconds", "timeZoneId", "isDaylightSavingTime", "nextOffsetChangeUtc", "nextUtcOffsetSeconds" })
        {
            Assert.Contains($"\"{key}\"", json);
        }
    }

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

    [Fact]
    public void Describe_AFixedOffsetHasNoNextChange()
    {
        var time = ServerTimeController.Describe(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), Zone("05:30"));

        Assert.Null(time.NextOffsetChangeUtc);
        Assert.Null(time.NextUtcOffsetSeconds);
    }

    // The night US daylight saving ended in 2026: 02:00 EDT became 01:00 EST at 06:00 UTC
    [Fact]
    public void Describe_NamesTheNextChangeToTheSecond()
    {
        if (Named("America/New_York") is not { } zone)
        {
            return;
        }

        var time = ServerTimeController.Describe(new DateTimeOffset(2026, 11, 1, 5, 16, 4, TimeSpan.Zero), zone);

        Assert.Equal(-14400, time.UtcOffsetSeconds);
        Assert.Equal("2026-11-01T06:00:00Z", time.NextOffsetChangeUtc);
        Assert.Equal(-18000, time.NextUtcOffsetSeconds);
    }

    [Fact]
    public void Describe_LooksMonthsAhead()
    {
        if (Named("America/New_York") is not { } zone)
        {
            return;
        }

        var summer = ServerTimeController.Describe(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), zone);
        var winter = ServerTimeController.Describe(new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero), zone);

        Assert.Equal("2026-11-01T06:00:00Z", summer.NextOffsetChangeUtc);
        Assert.Equal(-18000, summer.NextUtcOffsetSeconds);
        Assert.Equal("2027-03-14T07:00:00Z", winter.NextOffsetChangeUtc);
        Assert.Equal(-14400, winter.NextUtcOffsetSeconds);
    }

    // Southern hemisphere: Sydney leaves daylight saving in April, 03:00 AEDT back to 02:00 AEST
    [Fact]
    public void Describe_FollowsASouthernZone()
    {
        if (Named("Australia/Sydney") is not { } zone)
        {
            return;
        }

        var time = ServerTimeController.Describe(new DateTimeOffset(2027, 1, 10, 0, 0, 0, TimeSpan.Zero), zone);

        Assert.Equal(39600, time.UtcOffsetSeconds);
        Assert.Equal("2027-04-03T16:00:00Z", time.NextOffsetChangeUtc);
        Assert.Equal(36000, time.NextUtcOffsetSeconds);
    }

    [Fact]
    public void NextOffsetChange_IsNullWithoutChanges()
    {
        Assert.Null(ServerTimeController.NextOffsetChange(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
    }

    // Jellyfin's default policy refuses a user outside their access schedule, which is when the
    // client most needs the server clock and the Ping flag that says it can ask for it.
    [Theory]
    [InlineData(typeof(ServerTimeController), nameof(ServerTimeController.GetTime))]
    [InlineData(typeof(MoonfinController), nameof(MoonfinController.Ping))]
    public void Endpoint_IsReachableOutsideTheAccessSchedule(Type controller, string action)
    {
        var authorize = controller.GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("IgnoreParentalControl", authorize!.Policy);
    }
}
