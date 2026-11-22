using System.Linq;
using Moonfin.LiveTv;
using Xunit;
#if MOONFIN_EMBY_TESTS
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
namespace Emby.Plugins.Moonfin.Tests;
#else
using Moonfin.Server.Models;
using Moonfin.Server.Services;
namespace Moonfin.Server.Tests;
#endif

// One contract, compiled against Jellyfin and Emby's independent settings services.
public sealed class SavedGuideFiltersContractTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "guide-pins-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;
    public SavedGuideFiltersContractTests()
    {
#if MOONFIN_EMBY_TESTS
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _path);
#else
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _path);
#endif
    }
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    private Task<MoonfinUserSettings?> Read(Guid id) => _service.GetUserSettingsAsync(id);
    private Task Edit(Guid id, string profile, string action, string? pin = null, params string[] filters) =>
        _service.SaveProfileAsync(id, profile, new MoonfinSettingsProfile(), notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = action, Filter = pin,
                Filters = action == "initializeIfAbsent" ? new List<string>(filters) : null });
    private static PluginConfiguration Defaults(params string[] pins) => new() {
        DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>(pins) } };

    [Fact]
    public void ValidationAndLocalImportRespectCurrentButNotFutureDefaults()
    {
        Assert.True(new LiveTvSavedFilterChange { Action = "add", Filter = "group:News" }.IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "remove", Filter = "group:" }.IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
            Filters = Enumerable.Repeat("group:News", 1001).ToList() }.IsValid());
        var config = Defaults("group:Sports");
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:TV"] } };
        var user = new MoonfinUserSettings();
        MoonfinSettingsService.ApplySavedFilterChange(user, new LiveTvSavedFilterChange {
            Action = "initializeIfAbsent", Filters = ["group:Local"] }, config.DefaultUserSettings!.LiveTvSavedFilters);
        Assert.Equal(["group:Local"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:News"];
        Assert.Equal(["group:News", "group:Local"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public async Task AtomicEditsOutliveStaleFullSettingsAndImportWrites()
    {
        var id = Guid.NewGuid();
        await Edit(id, "tv", "initializeIfAbsent", null, "group:US | SPORTS, Canada");
        await Edit(id, "mobile", "add", "group:News");
        await Edit(id, "tv", "remove", "group:Sports");
        await _service.SaveProfileAsync(id, "mobile", new MoonfinSettingsProfile { LiveTvSavedFilters = [] }, notifySettingsChanged: false);
        await _service.SaveUserSettingsAsync(id, new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { LiveTvSavedFilters = [] },
            LiveTvSavedFilterExclusions = ["group:News"] }, mergeMode: "replace");
        Assert.Equal(["group:US | SPORTS, Canada", "group:News"], (await Read(id))!.Global!.LiveTvSavedFilters);
        Assert.Equal(["group:Sports"], (await Read(id))!.LiveTvSavedFilterExclusions);
        await Edit(id, "mobile", "remove", "group:News");
        await Edit(id, "tv", "initializeIfAbsent", null, "group:Ignored");
        Assert.Equal(["group:US | SPORTS, Canada"], (await Read(id))!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task GlobalAdminPinsMergeAndResetWithoutDeviceOnlyPins()
    {
        var config = Defaults("group:Sports", "group:News");
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:TV"] } };
        var id = Guid.NewGuid();
        await Edit(id, "tv", "add", "group:Local");
        await Edit(id, "tv", "remove", "group:Sports");
        await _service.MergeDefaultsAsync(config, ["tv"], ["liveTvSavedFilters"], [id]);
        Assert.Equal(["group:News", "group:Local"], _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
        await _service.MergeDefaultsAsync(config, ["global"], ["liveTvSavedFilters"], [id]);
        Assert.Equal(["group:Sports", "group:News", "group:Local"], _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
        await _service.ResetUsersToDefaultsAsync(config, ["global"], [id], deleteOrphans: false);
        Assert.Empty((await Read(id))!.Global!.LiveTvSavedFilters!);
        Assert.Equal(["group:Sports", "group:News"], _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public async Task AdminCopyAndProfileDeletionPreserveSavedPins()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        await Edit(from, "tv", "add", "group:News");
        await Edit(to, "tv", "add", "group:Old");
        await _service.SaveProfileAsync(to, "tv", new MoonfinSettingsProfile { ClockBehavior = "never" }, notifySettingsChanged: false);

        Assert.Equal(1, await _service.CopySettingsAsync(from, [to], ["global"], ["liveTvSavedFilters"], replace: false));
        Assert.Equal(["group:News"], (await Read(to))!.Global!.LiveTvSavedFilters);
        Assert.Equal("never", (await Read(to))!.Tv!.ClockBehavior);

        await _service.DeleteProfileAsync(to, "tv");
        Assert.Equal(["group:News"], (await Read(to))!.Global!.LiveTvSavedFilters);
        await _service.SaveProfileAsync(to, "mobile", new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:Stale"] }, notifySettingsChanged: false);
        Assert.Equal(["group:News"], (await Read(to))!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public void ProgramCategoriesRetainDeviceOverrides()
    {
        var config = Defaults();
        config.DefaultUserSettings!.LiveTvCategoryDisplay = "groups";
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvCategoryDisplay = "programmes" } };
        Assert.Equal("programmes", _service.ResolveProfile(new MoonfinUserSettings(), "tv", config).LiveTvCategoryDisplay);
        Assert.Equal("groups", _service.ResolveProfile(new MoonfinUserSettings(), "mobile", config).LiveTvCategoryDisplay);
    }
}
