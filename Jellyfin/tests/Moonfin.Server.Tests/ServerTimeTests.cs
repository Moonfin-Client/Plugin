using System.Text.Json;
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

        foreach (var key in new[] { "utcTime", "localTime", "utcOffsetSeconds", "timeZoneId", "isDaylightSavingTime" })
        {
            Assert.Contains($"\"{key}\"", json);
        }
    }
}
