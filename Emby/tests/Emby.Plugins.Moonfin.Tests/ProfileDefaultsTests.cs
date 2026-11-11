using System.Xml.Serialization;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Admin defaults kept per profile (global, tv, desktop, mobile), pushing only some settings,
/// and copying one user's settings onto another.
/// </summary>
public sealed class ProfileDefaultsTests : IDisposable
{
    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public ProfileDefaultsTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-profile-defaults-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private async Task<Guid> SeedAsync(string profileName, MoonfinSettingsProfile profile, Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        await _service.SaveProfileAsync(id, profileName, profile, "test-seed", notifySettingsChanged: false);
        return id;
    }

    private MoonfinUserSettings Read(Guid userId) => _service.GetUserSettingsAsync(userId).GetAwaiter().GetResult()!;

    private static PluginConfiguration Config() => new()
    {
        DefaultUserSettings = new MoonfinSettingsProfile
        {
            ClockBehavior = "always",
            Use24HourClock = true,
            CinemaModeEnabled = false,
            MediaBarLibraryIds = ["lib-global"],
        },
        DefaultDeviceSettings = new MoonfinDeviceDefaults
        {
            Tv = new MoonfinSettingsProfile
            {
                ClockBehavior = "never",
                KeyboardStyle = "docked",
                // What XmlSerializer hands back for a list the TV defaults never set.
                MediaBarLibraryIds = [],
            },
        },
    };

    [Fact]
    public void DeviceDefaultsWinOverGlobalDefaultsOnlyOnThatDevice()
    {
        var config = Config();
        var settings = new MoonfinUserSettings();

        var tv = _service.ResolveProfile(settings, "tv", config);
        Assert.Equal("never", tv.ClockBehavior);
        Assert.Equal("docked", tv.KeyboardStyle);
        Assert.True(tv.Use24HourClock);
        Assert.Equal(["lib-global"], tv.MediaBarLibraryIds!);

        var desktop = _service.ResolveProfile(settings, "desktop", config);
        Assert.Equal("always", desktop.ClockBehavior);
        Assert.Null(desktop.KeyboardStyle);

        Assert.Equal("always", _service.ResolveProfile(settings, "global", config).ClockBehavior);
    }

    [Fact]
    public void AUsersOwnGlobalValueStillBeatsTheDeviceDefault()
    {
        var settings = new MoonfinUserSettings { Global = new MoonfinSettingsProfile { ClockBehavior = "inMenus" } };

        Assert.Equal("inMenus", _service.ResolveProfile(settings, "tv", Config()).ClockBehavior);
    }

    [Fact]
    public void AdminDefaultsForADeviceLayerItsOwnOverGlobal()
    {
        var config = Config();
        var merged = MoonfinSettingsService.AdminDefaultsFor("tv", config)!;

        Assert.Equal("never", merged.ClockBehavior);
        Assert.True(merged.Use24HourClock);
        Assert.Equal(["lib-global"], merged.MediaBarLibraryIds!);
        Assert.Same(config.DefaultUserSettings, MoonfinSettingsService.AdminDefaultsFor("global", config));
        Assert.Same(config.DefaultUserSettings, MoonfinSettingsService.AdminDefaultsFor(null, config));
        Assert.Same(config.DefaultUserSettings, MoonfinSettingsService.AdminDefaultsFor("mobile", config));
    }

    [Fact]
    public void DeviceDefaultsSurviveTheXmlConfigRoundTrip()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream();
        serializer.Serialize(stream, Config());
        stream.Position = 0;

        var back = (PluginConfiguration)serializer.Deserialize(stream)!;

        Assert.Equal("never", back.DefaultDeviceSettings!.Tv!.ClockBehavior);
        Assert.Null(back.DefaultDeviceSettings.Mobile);
        Assert.Equal("never", _service.ResolveProfile(new MoonfinUserSettings(), "tv", back).ClockBehavior);
    }

    [Fact]
    public async Task GlobalDefaultsSkipWhatTheDeviceDefaultsSetInThatDevicesProfile()
    {
        var alice = await SeedAsync("tv", new MoonfinSettingsProfile { ClockBehavior = "inMenus", Use24HourClock = false });

        await _service.MergeDefaultsAsync(Config(), ["global"], null, null);

        var settings = Read(alice);
        Assert.Equal("always", settings.Global!.ClockBehavior);
        // The admin wants TV to differ, so Global's clock stays out of the TV profile.
        Assert.Equal("inMenus", settings.Tv!.ClockBehavior);
        Assert.True(settings.Tv.Use24HourClock);
    }

    [Fact]
    public async Task DeviceDefaultsCreateThatProfileAndGlobalAloneCreatesNone()
    {
        var alice = await SeedAsync("global", new MoonfinSettingsProfile { CinemaModeEnabled = true });
        var bob = await SeedAsync("global", new MoonfinSettingsProfile { CinemaModeEnabled = true });

        await _service.MergeDefaultsAsync(Config(), ["global"], null, [alice]);
        Assert.Null(Read(alice).Tv);

        await _service.MergeDefaultsAsync(Config(), ["tv"], null, [bob]);
        var settings = Read(bob);
        Assert.Equal("never", settings.Tv!.ClockBehavior);
        Assert.Equal("docked", settings.Tv.KeyboardStyle);
        Assert.Null(settings.Tv.MediaBarLibraryIds);
        Assert.True(settings.Global!.CinemaModeEnabled);
        Assert.Null(settings.Global.ClockBehavior);
    }

    [Fact]
    public async Task PushingSomeKeysWritesOnlyThose()
    {
        var alice = await SeedAsync("global", new MoonfinSettingsProfile { ClockBehavior = "never", Use24HourClock = false });

        var updated = await _service.MergeDefaultsAsync(Config(), ["global"], ["use24HourClock"], null);

        Assert.Equal(1, updated);
        var global = Read(alice).Global!;
        Assert.True(global.Use24HourClock);
        Assert.Equal("never", global.ClockBehavior);
        Assert.Null(global.CinemaModeEnabled);
    }

    [Fact]
    public async Task PushingOnlyUnknownKeysChangesNothing()
    {
        await SeedAsync("global", new MoonfinSettingsProfile());

        Assert.Equal(0, await _service.MergeDefaultsAsync(Config(), ["global", "tv"], ["notASetting"], null));
    }

    [Fact]
    public async Task ResetWritesEachChosenProfilesDefaultsAndDropsTheRest()
    {
        var alice = await SeedAsync("mobile", new MoonfinSettingsProfile { CinemaModeEnabled = true });
        var orphan = await SeedAsync("global", new MoonfinSettingsProfile());

        var (reset, orphans) = await _service.ResetUsersToDefaultsAsync(Config(), ["global", "tv"], [alice], deleteOrphans: false);

        Assert.Equal(1, reset);
        Assert.Equal(0, orphans);
        var settings = Read(alice);
        Assert.Equal("always", settings.Global!.ClockBehavior);
        Assert.Equal("never", settings.Tv!.ClockBehavior);
        Assert.Null(settings.Mobile);
        Assert.Null(settings.Desktop);
        Assert.True(File.Exists(Path.Combine(_dataPath, orphan + ".json")));

        (_, orphans) = await _service.ResetUsersToDefaultsAsync(Config(), ["global"], [alice], deleteOrphans: true);
        Assert.Equal(1, orphans);
        Assert.False(File.Exists(Path.Combine(_dataPath, orphan + ".json")));
    }

    [Fact]
    public async Task CopyMergesPreferencesButLeavesPersonalDataBehind()
    {
        var alice = await SeedAsync("global", new MoonfinSettingsProfile
        {
            ClockBehavior = "never",
            TmdbApiKey = "alice-key",
            HiddenNextUpSeries = "{\"alice-series\":\"1\"}",
        });
        await SeedAsync("tv", new MoonfinSettingsProfile { KeyboardStyle = "glass" }, alice);
        var bob = await SeedAsync("global", new MoonfinSettingsProfile { Use24HourClock = true, TmdbApiKey = "bob-key" });

        var updated = await _service.CopySettingsAsync(alice, [bob, alice], ["global", "tv", "desktop"], null, replace: false);

        Assert.Equal(1, updated);
        var settings = Read(bob);
        Assert.Equal("never", settings.Global!.ClockBehavior);
        Assert.True(settings.Global.Use24HourClock);
        Assert.Equal("bob-key", settings.Global.TmdbApiKey);
        Assert.DoesNotContain("alice-series", settings.Global.HiddenNextUpSeries ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("glass", settings.Tv!.KeyboardStyle);
        Assert.Null(settings.Desktop);
        Assert.Equal("alice-key", Read(alice).Global!.TmdbApiKey);
    }

    [Fact]
    public async Task CopyReplaceMakesAnExactCopyButKeepsTheTargetsPersonalData()
    {
        var alice = await SeedAsync("global", new MoonfinSettingsProfile { ClockBehavior = "never" });
        var bob = await SeedAsync("global", new MoonfinSettingsProfile
        {
            Use24HourClock = true,
            TmdbApiKey = "bob-key",
            HiddenContinueWatchingItems = "{\"bob-item\":\"1\"}",
        });

        await _service.CopySettingsAsync(alice, [bob], ["global"], null, replace: true);

        var global = Read(bob).Global!;
        Assert.Equal("never", global.ClockBehavior);
        Assert.Null(global.Use24HourClock);
        Assert.Equal("bob-key", global.TmdbApiKey);
        Assert.Contains("bob-item", global.HiddenContinueWatchingItems ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyingSomeKeysCopiesOnlyThose()
    {
        var alice = await SeedAsync("global", new MoonfinSettingsProfile { ClockBehavior = "never", KeyboardStyle = "glass" });
        var bob = Guid.NewGuid();

        await _service.CopySettingsAsync(alice, [bob], ["global"], ["keyboardStyle"], replace: false);

        var global = Read(bob).Global!;
        Assert.Equal("glass", global.KeyboardStyle);
        Assert.Null(global.ClockBehavior);
    }

    [Fact]
    public async Task CopyFromAUserWithNoSettingsDoesNothing()
    {
        var bob = await SeedAsync("global", new MoonfinSettingsProfile { ClockBehavior = "never" });

        Assert.Equal(0, await _service.CopySettingsAsync(Guid.NewGuid(), [bob], ["global"], null, replace: true));
        Assert.Equal("never", Read(bob).Global!.ClockBehavior);
    }

    [Fact]
    public void ResolveHideWatchedWithDefaultsHandlesDeviceOnlyAndConflictingDefaults()
    {
        var tvDefaults = new MoonfinSettingsProfile();
        var desktopDefaults = new MoonfinSettingsProfile();
        var mobileDefaults = new MoonfinSettingsProfile();
        var globalDefaults = new MoonfinSettingsProfile();
        _service.AdminDefaultsOverrideForTesting = profile => profile.ToLowerInvariant() switch
        {
            "tv" => tvDefaults,
            "desktop" => desktopDefaults,
            "mobile" => mobileDefaults,
            _ => globalDefaults,
        };

        var unsetUser = new MoonfinUserSettings();

        tvDefaults.HideWatchedInRecentRows = true;
        Assert.True(_service.ResolveHideWatchedWithDefaults(unsetUser, "global"));

        desktopDefaults.HideWatchedInRecentRows = false;
        Assert.True(_service.ResolveHideWatchedWithDefaults(unsetUser, "global"));

        tvDefaults.HideWatchedInRecentRows = null;
        Assert.False(_service.ResolveHideWatchedWithDefaults(unsetUser, "global"));
    }

    [Fact]
    public async Task StaleBackupWithPrimaryAccessErrorDoesNotCacheFalseAndRetriesToRecover()
    {
        _service.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(30);
        var userId = Guid.NewGuid();
        var primaryPath = Path.Combine(_dataPath, $"{userId}.json");
        var backupPath = primaryPath + ".bak";

        File.WriteAllText(backupPath, System.Text.Json.JsonSerializer.Serialize(new MoonfinUserSettings
        {
            SchemaVersion = 2,
            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
        }));
        File.WriteAllText(primaryPath, System.Text.Json.JsonSerializer.Serialize(new MoonfinUserSettings
        {
            SchemaVersion = 2,
            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
        }));

        var recoveredTrue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.UpgradePreferenceChanged = (enabled, _) =>
        {
            if (enabled)
            {
                recoveredTrue.TrySetResult();
            }

            return Task.CompletedTask;
        };

        using (var lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var initial = _service.IsAnyHideUpgradesEnabled(out var incomplete);
            Assert.False(initial);
            Assert.True(incomplete);

            await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
        }

        await recoveredTrue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(_service.IsAnyHideUpgradesEnabled(out var incompleteAfterRecovery));
        Assert.False(incompleteAfterRecovery);
    }
}
