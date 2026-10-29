using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// A user's profile should only hold what the user chose. Values that repeat what the user
/// would inherit are dropped on push and by the admin cleanup, and an admin can clear chosen
/// settings so they fall back to the defaults.
/// </summary>
public sealed class InheritedSettingsTests : IDisposable
{
    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public InheritedSettingsTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-inherited-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private async Task<Guid> UserWithGlobal(MoonfinSettingsProfile global)
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(userId, "global", global, "test", notifySettingsChanged: false);
        return userId;
    }

    private async Task Push(Guid userId, string profile, MoonfinSettingsProfile values) =>
        await _service.SaveProfileAsync(userId, profile, values, "test", notifySettingsChanged: false, dropInheritedValues: true);

    [Fact]
    public async Task APushedValueEqualToWhatTheUserInheritsIsNotStored()
    {
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { KeyboardStyle = "docked" });

        await Push(userId, "mobile", new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowDate = false });

        var settings = await _service.GetUserSettingsAsync(userId);
        Assert.Null(settings!.Mobile!.KeyboardStyle);
        Assert.False(settings.Mobile.RadarrCalendarShowDate);
    }

    [Fact]
    public async Task SwitchingBackToTheInheritedValueClearsTheChoice()
    {
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { KeyboardStyle = "docked" });
        await Push(userId, "mobile", new MoonfinSettingsProfile { KeyboardStyle = "floating" });

        await Push(userId, "mobile", new MoonfinSettingsProfile { KeyboardStyle = "docked" });

        var settings = await _service.GetUserSettingsAsync(userId);
        Assert.Null(settings!.Mobile!.KeyboardStyle);
        Assert.Equal("docked", (await _service.GetResolvedProfileAsync(userId, "mobile"))!.KeyboardStyle);
    }

    [Fact]
    public async Task OnlyThePushedSettingsAreLookedAt()
    {
        // Older repeats stay until the admin cleanup, so a push never changes more than it sent.
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowDate = true });
        await _service.SaveProfileAsync(userId, "mobile", new MoonfinSettingsProfile { KeyboardStyle = "docked" }, "old-client", notifySettingsChanged: false);

        await Push(userId, "mobile", new MoonfinSettingsProfile { RadarrCalendarShowDate = true });

        Assert.Equal("docked", (await _service.GetUserSettingsAsync(userId))!.Mobile!.KeyboardStyle);
    }

    [Fact]
    public async Task AValueWithNothingBelowItIsStored()
    {
        var userId = Guid.NewGuid();

        await Push(userId, "mobile", new MoonfinSettingsProfile { RadarrCalendarShowCinema = true });

        Assert.True((await _service.GetUserSettingsAsync(userId))!.Mobile!.RadarrCalendarShowCinema);
    }

    [Fact]
    public async Task TheHomeLayoutIsNeverDropped()
    {
        var rows = new List<MoonfinHomeSectionConfig> { new() { Kind = "builtin", Type = "nextup", Enabled = true, Order = 0 } };
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { HomeSections = rows });

        await Push(userId, "mobile", new MoonfinSettingsProfile { HomeSections = rows });

        Assert.NotNull((await _service.GetUserSettingsAsync(userId))!.Mobile!.HomeSections);
    }

    [Fact]
    public void TheCleanupComparesDevicesWithGlobalAndGlobalWithTheDefaults()
    {
        var settings = new MoonfinUserSettings
        {
            Global = new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowDate = true },
            Mobile = new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowCinema = true },
            Tv = new MoonfinSettingsProfile { KeyboardStyle = "floating" }
        };
        var defaults = new MoonfinSettingsProfile { RadarrCalendarShowDate = true };

        var removed = MoonfinSettingsService.RemoveRepeatedValues(settings, defaults);

        Assert.Equal(2, removed);
        Assert.Null(settings.Mobile.KeyboardStyle);
        Assert.True(settings.Mobile.RadarrCalendarShowCinema);
        Assert.Equal("floating", settings.Tv.KeyboardStyle);
        Assert.Null(settings.Global.RadarrCalendarShowDate);
        Assert.Equal("docked", settings.Global.KeyboardStyle);
    }

    [Fact]
    public async Task TheCleanupChangesNothingTheUserGets()
    {
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { KeyboardStyle = "docked" });
        await _service.SaveProfileAsync(userId, "mobile",
            new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowCinema = true }, "old-client", notifySettingsChanged: false);
        var before = await _service.GetResolvedProfileAsync(userId, "mobile");

        var dryRun = await _service.RemoveRepeatedValuesAsync(new[] { userId }, dryRun: true);
        Assert.Equal((1, 1), dryRun);
        Assert.Equal("docked", (await _service.GetUserSettingsAsync(userId))!.Mobile!.KeyboardStyle);

        var run = await _service.RemoveRepeatedValuesAsync(new[] { userId }, dryRun: false);
        Assert.Equal((1, 1), run);

        var after = await _service.GetResolvedProfileAsync(userId, "mobile");
        Assert.Null((await _service.GetUserSettingsAsync(userId))!.Mobile!.KeyboardStyle);
        Assert.Equal(before!.KeyboardStyle, after!.KeyboardStyle);
        Assert.Equal(before.RadarrCalendarShowCinema, after.RadarrCalendarShowCinema);
    }

    [Fact]
    public async Task ClearingASettingRemovesItFromEveryProfile()
    {
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { RadarrCalendarShowCinema = true });
        await Push(userId, "tv", new MoonfinSettingsProfile { RadarrCalendarShowCinema = false, KeyboardStyle = "docked" });
        var other = await UserWithGlobal(new MoonfinSettingsProfile { RadarrCalendarShowCinema = true });

        var result = await _service.ClearSettingsAsync(new[] { "radarrCalendarShowCinema" }, new[] { userId });

        Assert.Equal((1, 2), result);
        var settings = await _service.GetUserSettingsAsync(userId);
        Assert.Null(settings!.Global!.RadarrCalendarShowCinema);
        Assert.Null(settings.Tv!.RadarrCalendarShowCinema);
        Assert.Equal("docked", settings.Tv.KeyboardStyle);
        Assert.True((await _service.GetUserSettingsAsync(other))!.Global!.RadarrCalendarShowCinema);
    }

    [Fact]
    public async Task ClearingTheHomeLayoutRemovesTheRowsAndTheirOrder()
    {
        var userId = await UserWithGlobal(new MoonfinSettingsProfile
        {
            HomeSections = new List<MoonfinHomeSectionConfig> { new() { Kind = "builtin", Type = "nextup", Enabled = true, Order = 0 } },
            HomeRowOrder = new List<string> { "nextup" }
        });

        await _service.ClearSettingsAsync(new[] { "homeLayout" }, new[] { userId });

        var global = (await _service.GetUserSettingsAsync(userId))!.Global!;
        Assert.Null(global.HomeSections);
        Assert.Null(global.HomeRowOrder);
    }

    [Fact]
    public async Task UnknownOrProtectedSettingsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ClearSettingsAsync(new[] { "notASetting" }, new[] { Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ClearSettingsAsync(new[] { "hiddenContinueWatchingItems" }, new[] { Guid.NewGuid() }));
    }

    [Fact]
    public async Task TheOverviewCountsWhatEachProfileStoresAndWhatRepeats()
    {
        var rows = new List<MoonfinHomeSectionConfig> { new() { Kind = "builtin", Type = "nextup", Enabled = true, Order = 0 } };
        var userId = await UserWithGlobal(new MoonfinSettingsProfile { KeyboardStyle = "docked" });
        await _service.SaveProfileAsync(userId, "tv",
            new MoonfinSettingsProfile { KeyboardStyle = "docked", RadarrCalendarShowCinema = true, HomeSections = rows, HomeRowOrder = new List<string> { "nextup" } },
            "old-client", notifySettingsChanged: false);
        var neverSynced = Guid.NewGuid();

        var overview = await _service.GetSettingsOverviewAsync(new[] { userId, neverSynced });

        var user = overview.Single(e => e.UserId == userId);
        Assert.Equal(3, user.Tv);
        Assert.Equal(1, user.Global);
        Assert.Equal(0, user.Mobile);
        Assert.Equal(1, user.Repeated);
        Assert.Equal("docked", (await _service.GetUserSettingsAsync(userId))!.Tv!.KeyboardStyle);

        var other = overview.Single(e => e.UserId == neverSynced);
        Assert.False(other.HasSettingsFile);
        Assert.Equal(0, other.Repeated);
    }
}
