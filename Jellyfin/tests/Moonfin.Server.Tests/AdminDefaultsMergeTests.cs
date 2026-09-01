using System.Text.Json;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// A defaults push hands the live DefaultUserSettings object to the settings service, and
/// SaveProfileAsync mutates whatever profile it is given. Without a per-user clone that leaves one
/// user's choices sitting in the admin defaults for everyone else to inherit.
/// </summary>
public sealed class AdminDefaultsMergeTests : IDisposable
{
    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public AdminDefaultsMergeTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-merge-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private async Task<Guid> SeedUserAsync(MoonfinSettingsProfile global)
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(userId, "global", global, "test-seed", notifySettingsChanged: false);
        return userId;
    }

    /// <summary>
    /// A settings file with no global profile, only a device one, which is what a user who has
    /// only ever synced from a TV looks like. SaveProfileAsync handles that with SetProfile, so
    /// the profile it was handed becomes the user's own and later steps mutate the caller's copy.
    /// </summary>
    private Guid SeedDeviceOnlyUser(string hiddenContinueWatching)
    {
        var userId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(
            new MoonfinUserSettings
            {
                SchemaVersion = 2,
                SyncEnabled = true,
                Tv = new MoonfinSettingsProfile { HiddenContinueWatchingItems = hiddenContinueWatching },
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastUpdatedBy = "test-seed",
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, userId + ".json"), json);
        return userId;
    }

    private MoonfinUserSettings Read(Guid userId)
    {
        var json = File.ReadAllText(Path.Combine(_dataPath, userId + ".json"));
        return JsonSerializer.Deserialize<MoonfinUserSettings>(
            json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    }

    [Fact]
    public async Task MergingDefaultsToAllUsersLeavesTheDefaultsObjectUntouched()
    {
        SeedDeviceOnlyUser("{\"alice-item\":\"1\"}");

        var defaults = new MoonfinSettingsProfile { CinemaModeEnabled = true };

        await _service.MergeDefaultsToAllUsersAsync(defaults);

        // The admin never set this, so the push must not have collected it from a user.
        Assert.Null(defaults.HiddenContinueWatchingItems);
        Assert.True(defaults.CinemaModeEnabled);
    }

    /// <summary>
    /// A guard rather than a demonstration: MoveContentHidingToGlobal assigns each user's own
    /// device union last, so this holds today even without the clone.
    /// </summary>
    [Fact]
    public async Task MergingDefaultsDoesNotLeakOneUsersHiddenItemsOntoAnother()
    {
        var alice = SeedDeviceOnlyUser("{\"alice-item\":\"1\"}");
        var bob = SeedDeviceOnlyUser("{\"bob-item\":\"1\"}");

        await _service.MergeDefaultsToAllUsersAsync(new MoonfinSettingsProfile { CinemaModeEnabled = true });

        var aliceHidden = Read(alice).Global?.HiddenContinueWatchingItems ?? string.Empty;
        var bobHidden = Read(bob).Global?.HiddenContinueWatchingItems ?? string.Empty;

        Assert.Contains("alice-item", aliceHidden, StringComparison.Ordinal);
        Assert.DoesNotContain("bob-item", aliceHidden, StringComparison.Ordinal);
        Assert.Contains("bob-item", bobHidden, StringComparison.Ordinal);
        Assert.DoesNotContain("alice-item", bobHidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergingDefaultsLeavesTheAdminsHomeLayoutListAsTheAdminWroteIt()
    {
        await SeedUserAsync(new MoonfinSettingsProfile());
        await SeedUserAsync(new MoonfinSettingsProfile());

        var section = new MoonfinHomeSectionConfig { Kind = "custom", Type = "custom", Enabled = true, Order = 0 };
        var defaults = new MoonfinSettingsProfile
        {
            HomeSections = new List<MoonfinHomeSectionConfig> { section },
        };

        await _service.MergeDefaultsToAllUsersAsync(defaults);

        // MergeProfile assigns references, and PropagateCustomHomeSectionsAcrossProfiles adds
        // to and removes from whatever list it lands on.
        Assert.Single(defaults.HomeSections);
        Assert.Same(section, defaults.HomeSections[0]);
    }

    [Fact]
    public async Task MergingDefaultsStillAppliesWhatTheAdminDidSet()
    {
        var alice = await SeedUserAsync(
            new MoonfinSettingsProfile { HiddenContinueWatchingItems = "{\"alice-item\":\"1\"}" });

        await _service.MergeDefaultsToAllUsersAsync(new MoonfinSettingsProfile { CinemaModeEnabled = true });

        var settings = Read(alice);
        Assert.True(settings.Global?.CinemaModeEnabled);
        Assert.Contains("alice-item", settings.Global?.HiddenContinueWatchingItems ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergingDefaultsToASingleUserLeavesTheDefaultsObjectUntouched()
    {
        var alice = SeedDeviceOnlyUser("{\"alice-item\":\"1\"}");
        var defaults = new MoonfinSettingsProfile { CinemaModeEnabled = true };

        await _service.MergeDefaultsToUserAsync(alice, defaults);

        Assert.Null(defaults.HiddenContinueWatchingItems);
    }

    [Fact]
    public async Task SuccessiveDeviceProfileSavesPreserveGlobalHiddenItems()
    {
        var userId = Guid.NewGuid();

        // 1. TV saves an item to hide
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { HiddenContinueWatchingItems = "{\"item-tv\":\"2026-09-01T01:00:00Z\"}" },
            "tv-client",
            notifySettingsChanged: false);

        var afterTv = Read(userId);
        Assert.Contains("item-tv", afterTv.Global?.HiddenContinueWatchingItems ?? string.Empty, StringComparison.Ordinal);

        // 2. Mobile saves a different item to hide (or pushes without hidden items)
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { HiddenContinueWatchingItems = "{\"item-mobile\":\"2026-09-01T02:00:00Z\"}" },
            "mobile-client",
            notifySettingsChanged: false);

        var afterMobile = Read(userId);
        var globalHidden = afterMobile.Global?.HiddenContinueWatchingItems ?? string.Empty;
        Assert.Contains("item-tv", globalHidden, StringComparison.Ordinal);
        Assert.Contains("item-mobile", globalHidden, StringComparison.Ordinal);

        // 3. TV saves an unrelated setting with null/empty hidden items
        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { CinemaModeEnabled = true },
            "tv-client",
            notifySettingsChanged: false);

        var afterTvUpdate = Read(userId);
        var finalGlobalHidden = afterTvUpdate.Global?.HiddenContinueWatchingItems ?? string.Empty;
        Assert.Contains("item-tv", finalGlobalHidden, StringComparison.Ordinal);
        Assert.Contains("item-mobile", finalGlobalHidden, StringComparison.Ordinal);

        // 4. Resolving TV profile retrieves both items from Global
        var resolvedTv = _service.ResolveProfile(afterTvUpdate, "tv");
        Assert.Contains("item-tv", resolvedTv.HiddenContinueWatchingItems ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("item-mobile", resolvedTv.HiddenContinueWatchingItems ?? string.Empty, StringComparison.Ordinal);
    }
}

