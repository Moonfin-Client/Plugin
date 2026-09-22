using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public class SeerrHomeSectionMigrationTests
{
    [Fact]
    public void MigrateSeerrHomeSections_RewritesStoredSeerrTypesToSliders()
    {
        var profile = new MoonfinSettingsProfile
        {
            HomeSections = new List<MoonfinHomeSectionConfig>
            {
                new() { Type = "resume", Enabled = true, Order = 0 },
                new() { Type = "seerr_trending", Enabled = true, Order = 1 },
                new() { Type = "seerr_shortcuts", Enabled = false, Order = 2 },
            },
        };

        Assert.True(MoonfinSettingsService.MigrateSeerrHomeSections(profile));

        var trending = profile.HomeSections![1];
        Assert.Equal("seerrSlider", trending.Kind);
        Assert.Equal("seerr_slider", trending.Type);
        Assert.Equal(4, trending.SliderType);
        Assert.Null(trending.SliderId);

        var shortcuts = profile.HomeSections[2];
        Assert.Equal("seerrSlider", shortcuts.Kind);
        Assert.Equal("shortcuts", shortcuts.SliderId);
        Assert.Null(shortcuts.SliderType);
        Assert.False(shortcuts.Enabled);

        // The migration runs on every load and every save, so a second pass over an
        // already migrated profile has to report nothing changed.
        Assert.False(MoonfinSettingsService.MigrateSeerrHomeSections(profile));
    }

    [Fact]
    public void MigrateSeerrHomeSections_KeepsBuiltinRowsWhenOnlyHomeRowOrderIsStored()
    {
        var profile = new MoonfinSettingsProfile
        {
            HomeRowOrder = new List<string> { "resume", "seerr_trending", "nextUp" },
        };

        Assert.True(MoonfinSettingsService.MigrateSeerrHomeSections(profile));

        // Clients prefer homeSections over homeRowOrder, so a list holding nothing but
        // the migrated slider would silently turn every other row off.
        Assert.Equal(new[] { "resume", "nextUp" }, profile.HomeRowOrder);
        Assert.NotNull(profile.HomeSections);
        Assert.Collection(
            profile.HomeSections!,
            section =>
            {
                Assert.Equal("builtin", section.Kind);
                Assert.Equal("resume", section.Type);
                Assert.True(section.Enabled);
            },
            section =>
            {
                Assert.Equal("seerrSlider", section.Kind);
                Assert.Equal(4, section.SliderType);
            },
            section =>
            {
                Assert.Equal("builtin", section.Kind);
                Assert.Equal("nextUp", section.Type);
                Assert.True(section.Enabled);
            });

        Assert.False(MoonfinSettingsService.MigrateSeerrHomeSections(profile));
    }

    [Fact]
    public void MigrateSeerrHomeSections_DoesNotMaterializeOrderWithoutSeerrRows()
    {
        var profile = new MoonfinSettingsProfile
        {
            HomeRowOrder = new List<string> { "resume", "nextUp" },
        };

        Assert.False(MoonfinSettingsService.MigrateSeerrHomeSections(profile));
        Assert.Null(profile.HomeSections);
        Assert.Equal(new[] { "resume", "nextUp" }, profile.HomeRowOrder);
    }

    [Fact]
    public void MigrateSeerrHomeSections_UnbindsLegacySliderIds()
    {
        var profile = new MoonfinSettingsProfile
        {
            HomeSections = new List<MoonfinHomeSectionConfig>
            {
                new()
                {
                    Kind = "seerrSlider",
                    Type = "seerr_slider",
                    SliderId = "legacy:9",
                    Enabled = true,
                    Order = 0,
                },
            },
        };

        Assert.True(MoonfinSettingsService.MigrateSeerrHomeSections(profile));
        Assert.Null(profile.HomeSections![0].SliderId);
        Assert.Equal(9, profile.HomeSections[0].SliderType);
    }

    [Fact]
    public void MigrateSeerrHomeSections_SkipsRowOrderEntryAlreadyPresentInSections()
    {
        var profile = new MoonfinSettingsProfile
        {
            HomeSections = new List<MoonfinHomeSectionConfig>
            {
                new() { Kind = "builtin", Type = "resume", Enabled = true, Order = 0 },
                new()
                {
                    Kind = "seerrSlider",
                    Type = "seerr_slider",
                    SliderId = "7",
                    SliderType = 4,
                    Enabled = true,
                    Order = 1,
                },
            },
            HomeRowOrder = new List<string> { "resume", "seerr_trending" },
        };

        Assert.True(MoonfinSettingsService.MigrateSeerrHomeSections(profile));
        Assert.Equal(2, profile.HomeSections!.Count);
        Assert.Equal(new[] { "resume" }, profile.HomeRowOrder);
    }
}
