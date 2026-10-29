using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The admin preview says, for each setting, which layer the user's value comes from and
/// whether a stored value only repeats what the layers below would give.
/// </summary>
public sealed class SettingsExplanationTests
{
    private static SettingExplanation Field(SettingsExplanation explanation, string name) =>
        explanation.Fields.Single(f => f.Name == name);

    [Fact]
    public void TheDeviceValueWinsAndRepeatingTheDefaultIsRedundant()
    {
        var settings = new MoonfinUserSettings
        {
            Mobile = new MoonfinSettingsProfile { RadarrCalendarShowDate = true, RadarrCalendarShowDigital = false }
        };
        var defaults = new MoonfinSettingsProfile { RadarrCalendarShowDate = true, RadarrCalendarShowDigital = true };

        var explanation = MoonfinSettingsService.ExplainProfile(settings, "mobile", defaults);

        var date = Field(explanation, "radarrCalendarShowDate");
        Assert.Equal("device", date.Source);
        Assert.True(date.DeviceRedundant);

        var digital = Field(explanation, "radarrCalendarShowDigital");
        Assert.Equal("device", digital.Source);
        Assert.False(digital.Effective!.GetValue<bool>());
        Assert.False(digital.DeviceRedundant);
    }

    [Fact]
    public void AValueWithNothingBelowItIsNeverRedundant()
    {
        // The server can't know the app's own default, so it can't call this a repeat.
        var settings = new MoonfinUserSettings { Mobile = new MoonfinSettingsProfile { RadarrCalendarShowCinema = true } };

        var cinema = Field(MoonfinSettingsService.ExplainProfile(settings, "mobile", null), "radarrCalendarShowCinema");

        Assert.Equal("device", cinema.Source);
        Assert.False(cinema.DeviceRedundant);
    }

    [Fact]
    public void GlobalAndDefaultsFillWhatTheDeviceLeavesUnset()
    {
        var settings = new MoonfinUserSettings
        {
            Global = new MoonfinSettingsProfile { SeasonalRowEnabled = false },
            Mobile = new MoonfinSettingsProfile()
        };
        var defaults = new MoonfinSettingsProfile { SeasonalRowEnabled = false, RadarrCalendarShowCinema = false };

        var explanation = MoonfinSettingsService.ExplainProfile(settings, "mobile", defaults);

        var seasonal = Field(explanation, "seasonalRowEnabled");
        Assert.Equal("global", seasonal.Source);
        Assert.True(seasonal.GlobalRedundant);
        Assert.Equal("default", Field(explanation, "radarrCalendarShowCinema").Source);
        Assert.Equal("none", Field(explanation, "radarrCalendarShowPhysical").Source);
    }

    [Fact]
    public void TheGlobalProfileSkipsTheDeviceLayer()
    {
        var settings = new MoonfinUserSettings { Tv = new MoonfinSettingsProfile { RadarrCalendarShowDate = false } };

        var date = Field(MoonfinSettingsService.ExplainProfile(settings, "global", null), "radarrCalendarShowDate");

        Assert.Equal("none", date.Source);
        Assert.Null(date.Device);
    }

    [Fact]
    public void TheHomeLayoutComesFromOneLayerWhole()
    {
        var rows = new List<MoonfinHomeSectionConfig>
        {
            new() { Kind = "builtin", Type = "nextup", Enabled = true, Order = 0 }
        };
        var settings = new MoonfinUserSettings
        {
            Tv = new MoonfinSettingsProfile { HomeSections = rows },
            Global = new MoonfinSettingsProfile { HomeSections = rows }
        };

        var explanation = MoonfinSettingsService.ExplainProfile(settings, "tv", null);

        var layout = Field(explanation, "homeLayout");
        Assert.Equal("device", layout.Source);
        // Same rows as global, but never flagged: the cleanup leaves the layout alone.
        Assert.False(layout.DeviceRedundant);
        Assert.DoesNotContain(explanation.Fields, f => f.Name is "homeSections" or "homeRowOrder");
    }

    [Fact]
    public void ApiKeysAreNeverShownInClear()
    {
        var settings = new MoonfinUserSettings { Global = new MoonfinSettingsProfile { MdblistApiKey = "secret-key" } };

        var key = Field(MoonfinSettingsService.ExplainProfile(settings, "global", null), "mdblistApiKey");

        Assert.Equal("(set)", key.Effective!.GetValue<string>());
        Assert.DoesNotContain("secret-key", key.Global!.ToJsonString());
    }

    [Fact]
    public void AUserWhoNeverSyncedGetsEverythingFromTheDefaults()
    {
        var defaults = new MoonfinSettingsProfile { RadarrCalendarShowDate = false };

        var explanation = MoonfinSettingsService.ExplainProfile(null, "mobile", defaults);

        Assert.False(explanation.HasSettingsFile);
        Assert.Equal("default", Field(explanation, "radarrCalendarShowDate").Source);
    }
}
