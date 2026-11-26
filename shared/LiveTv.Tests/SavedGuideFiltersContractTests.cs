using Moonfin.LiveTv;
using Xunit;
#if MOONFIN_EMBY_TESTS
using SettingsReadError = MediaBrowser.Common.Extensions.ServiceUnavailableException;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
namespace Emby.Plugins.Moonfin.Tests;
#else
using SettingsReadError = Moonfin.Server.Services.UnreadableSettingsException;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
namespace Moonfin.Server.Tests;
#endif

// One contract, compiled against Jellyfin and Emby's independent settings services.
public sealed class SavedGuideFiltersContractTests : IDisposable
{
    private readonly string _path = Directory.CreateTempSubdirectory("guide-pins-").FullName;
    private readonly MoonfinSettingsService _service;
    private readonly Guid _user = Guid.NewGuid();
    public SavedGuideFiltersContractTests()
    {
#if MOONFIN_EMBY_TESTS
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _path);
#else
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _path);
#endif
    }
    public void Dispose() => Directory.Delete(_path, true);
    private Task<MoonfinUserSettings?> Read() => _service.GetUserSettingsAsync(_user);
    private Task<List<string>?> Edit(string profile, string action, string? pin = null, params string[] filters) =>
        _service.SaveProfileAsync(_user, profile, new MoonfinSettingsProfile(), notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = action, Filter = pin,
                Filters = action == "initializeIfAbsent" ? new List<string>(filters) : null });
    private static PluginConfiguration Defaults(params string[] pins) => new() {
        DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>(pins) } };

    private static LiveTvSavedFilterChange Change(string action, string pin) => new() { Action = action, Filter = pin };
    private List<string>? Pins(MoonfinUserSettings user, PluginConfiguration config) =>
        _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters;

    [Fact]
    public async Task PinSaveReturnsCommittedPinsWhenTheFileCannotBeReadAfterWriting()
    {
#if MOONFIN_EMBY_TESTS
        var service = _service;
#else
        var provider = RecordingHostProxy.Create<IServiceProvider>((_, _) => null);
        var service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _path, provider);
#endif
        FileStream? lockedFile = null;
        // Native preference sync runs after the file has been committed, before the save returns.
        service.BeforeNativeSyncWaitForTesting = id =>
            lockedFile = File.Open(Path.Combine(_path, id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            var pins = await service.SaveProfileAsync(_user, "tv", new() { HideWatchedInRecentRows = true },
                savedFilterChange: Change("add", "group:Sports"));
            Assert.NotNull(lockedFile);
            await Assert.ThrowsAsync<SettingsReadError>(() => service.GetResolvedProfileAsync(_user, "global", throwIfUnreadable: true));
            Assert.Equal(["group:Sports"], pins);
        }
        finally
        {
            lockedFile?.Dispose();
        }
        Assert.Equal(["group:Sports"], (await Read())!.Global!.LiveTvSavedFilters);
    }

    [Theory]
    [InlineData("primary locked")]
    [InlineData("backup locked")]
    [InlineData("both corrupt")]
    public async Task FailedSettingsReadsPreserveBothCopiesAndCanBeRetried(string failure)
    {
        Assert.Null(await _service.GetResolvedProfileAsync(_user, "tv"));
        Assert.Null(await _service.GetProfileForPullAsync(_user, "tv", includeLiveTvSavedFilters: true));
        await Edit("tv", "add", "group:Sports");
        var primary = Path.Combine(_path, _user + ".json");
        var backup = AtomicFile.BackupPath(primary);
        var good = File.ReadAllText(primary);
        if (failure == "backup locked") File.Move(primary, backup);
        if (failure == "both corrupt") {
            File.WriteAllText(primary, "{primary");
            File.WriteAllText(backup, "{backup");
        }
        using (failure == "both corrupt" ? null : File.Open(failure == "backup locked" ? backup : primary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            Assert.Null(await _service.GetResolvedProfileAsync(_user, "tv"));
            Assert.Null(await _service.GetProfileForPullAsync(_user, "tv", includeLiveTvSavedFilters: false));
            var error = await Assert.ThrowsAsync<SettingsReadError>(() =>
                _service.GetProfileForPullAsync(_user, "tv", includeLiveTvSavedFilters: true));
            await Assert.ThrowsAsync<SettingsReadError>(() =>
                _service.GetResolvedProfileAsync(_user, "global", throwIfUnreadable: true));
            Assert.DoesNotContain(primary, error.Message);
            await Assert.ThrowsAsync<SettingsReadError>(() => Edit("tv", "add", "group:News"));
            await Assert.ThrowsAsync<SettingsReadError>(() => _service.SaveUserSettingsAsync(_user, new()));
            await Assert.ThrowsAsync<SettingsReadError>(() => _service.DeleteProfileAsync(_user, "tv"));
        }
        if (failure == "both corrupt") {
            Assert.Equal("{primary", File.ReadAllText(primary));
            Assert.Equal("{backup", File.ReadAllText(backup));
            File.WriteAllText(primary, good);
        }
        await Edit("tv", "add", "group:News");
        Assert.Equal(["group:Sports", "group:News"], (await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public void PinValidationRejectsMalformedImportsAndIgnoresOversizedStoredPins()
    {
        Assert.False(Change("remove", "group:").IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
            Filters = Enumerable.Repeat("group:News", 1001).ToList() }.IsValid());
        var atLimit = "group:" + new string('x', 1018);
        var tooLong = atLimit + "x";
        Assert.False(Change("add", tooLong).IsValid());
        Assert.False(Change("remove", tooLong).IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "initializeIfAbsent", Filters = [tooLong] }.IsValid());
        var saved = new MoonfinUserSettings { Global = new MoonfinSettingsProfile { LiveTvSavedFilters = [tooLong] } };
        Assert.Equal([atLimit], Pins(saved, Defaults(atLimit, tooLong)));
    }

    [Fact]
    public async Task PinLimitCountsPersonalPinsAndExclusionsAndRejectsTheWholeSave()
    {
        var pins = Enumerable.Range(0, 1000).Select(i => $"group:{i}").ToArray();
        await Edit("tv", "initializeIfAbsent", null, pins);
        await Assert.ThrowsAsync<GuidePinLimitException>(() => _service.SaveProfileAsync(_user, "mobile",
            new MoonfinSettingsProfile { LiveTvCategoryDisplay = "groups" }, savedFilterChange:
            Change("add", "group:extra")));
        var stored = (await Read())!;
        Assert.Null(stored.Mobile?.LiveTvCategoryDisplay);
        Assert.Equal(pins, stored.Global!.LiveTvSavedFilters);
        var exclude = Change("remove", "group:managed");
        Assert.Throws<GuidePinLimitException>(() => MoonfinSettingsService.ApplySavedFilterChange(stored, exclude, ["group:managed"]));
        Assert.Empty(stored.LiveTvSavedFilterExclusions!);
        await Edit("tv", "remove", pins[0]);
        await Edit("tv", "add", "group:extra");
        Assert.Equal(1000, (await Read())!.Global!.LiveTvSavedFilters!.Count);
    }

    [Fact]
    public async Task PinAwarePullDistinguishesUnsetFromClearedAfterAnAccountReset()
    {
        var viewer = new MoonfinUserSettings();
        foreach (var config in new[] { new PluginConfiguration(), Defaults() }) {
            var expected = config.DefaultUserSettings?.LiveTvSavedFilters;
            Assert.Equal(expected, MoonfinSettingsService.AdminDefaultsFor("tv", config)?.LiveTvSavedFilters);
            Assert.Equal(expected, Pins(viewer, config));
        }
        viewer.Global = new() { LiveTvSavedFilters = [] };
        Assert.Empty(Pins(viewer, new PluginConfiguration())!);
        await Edit("tv", "add", "group:Sports");
        Task<MoonfinSettingsProfile?> Pull(bool pins) => _service.GetProfileForPullAsync(_user, "tv", pins);
        Assert.Equal(["group:Sports"], (await Pull(true))!.LiveTvSavedFilters);
        await _service.DeleteUserSettingsAsync(_user);
        Assert.Null(await Pull(true));
        await _service.SaveProfileAsync(_user, "global", new() { LiveTvCategoryDisplay = "groups" });
        var fresh = await Pull(true);
        Assert.Equal("groups", fresh!.LiveTvCategoryDisplay);
        Assert.Empty(fresh.LiveTvSavedFilters!);
        Assert.Null((await Pull(false))!.LiveTvSavedFilters);
        Assert.Null((await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task AdminDefaultsAndStaleDeviceSavesRespectPersonalPinEdits()
    {
        const string local = "group:US | SPORTS, Canada";
        var config = Defaults("group:Sports", "group:News");
        config.DefaultDeviceSettings = new() { Tv = new() { LiveTvSavedFilters = ["group:TV"], LiveTvCategoryDisplay = "programmes" } };
        config.DefaultUserSettings!.LiveTvCategoryDisplay = "groups";
        var imported = new MoonfinUserSettings();
        MoonfinSettingsService.ApplySavedFilterChange(imported, new() { Action = "initializeIfAbsent", Filters = [local] }, ["group:Sports"]);
        File.WriteAllText(Path.Combine(_path, _user + ".json"), System.Text.Json.JsonSerializer.Serialize(imported));
        await Edit("mobile", "add", "group:Kids");
        await _service.SaveProfileAsync(_user, "mobile", new() { LiveTvSavedFilters = [] });
        await _service.SaveUserSettingsAsync(_user, new() {
            Global = new() { LiveTvSavedFilters = [] }, LiveTvSavedFilterExclusions = ["group:Kids"] }, mergeMode: "replace");
        var stored = (await Read())!;
        Assert.Equal([local, "group:Kids"], stored.Global!.LiveTvSavedFilters);
        Assert.Equal(["group:Sports"], stored.LiveTvSavedFilterExclusions);
        await Edit("mobile", "remove", "group:Kids");
        await Edit("tv", "initializeIfAbsent", null, "group:Ignored");
        stored = (await Read())!;
        Assert.Equal([local], stored.Global!.LiveTvSavedFilters);
        Assert.Equal("programmes", _service.ResolveProfile(stored, "tv", config).LiveTvCategoryDisplay);
        Assert.Equal("groups", _service.ResolveProfile(stored, "mobile", config).LiveTvCategoryDisplay);
        var later = Defaults("group:Kids", local);
        Assert.Equal(["group:Kids", local], Pins(stored, later));
        var managedPins = later.DefaultUserSettings!.LiveTvSavedFilters;
        Assert.Equal(["group:Kids"], MoonfinSettingsService.ApplySavedFilterChange(stored, Change("remove", local), managedPins));
        Assert.Equal(["group:Kids"], MoonfinSettingsService.ApplySavedFilterChange(stored,
            new() { Action = "initializeIfAbsent", Filters = ["group:Ignored"] }, managedPins));
        Assert.Equal(["group:Kids"], Pins(stored, later));
        async Task Check(params string[] pins) => Assert.Equal(pins, Pins((await Read())!, config));
        await Check("group:News", local);
        await _service.MergeDefaultsAsync(config, ["tv"], ["liveTvSavedFilters"], [_user]);
        await Check("group:News", local);
        await _service.MergeDefaultsAsync(config, ["global"], ["liveTvSavedFilters"], [_user]);
        await Check("group:Sports", "group:News", local);
        await _service.ResetUsersToDefaultsAsync(config, ["global"], [_user], deleteOrphans: false);
        Assert.Empty((await Read())!.Global!.LiveTvSavedFilters!);
        await Check("group:Sports", "group:News");
    }

    [Fact]
    public async Task ReplacingGlobalSettingsWithoutPinsPreventsReimport()
    {
        const string pin = "group:Sports";
        var source = Guid.NewGuid();
        Assert.Null(await _service.SaveProfileAsync(source, "global", new() { ClockBehavior = "never" }));
        Assert.Equal([pin], await Edit("tv", "initializeIfAbsent", null, pin));
        Assert.Equal([pin], (await Read())!.Global!.LiveTvSavedFilters);

        Assert.Equal(1, await _service.CopySettingsAsync(source, [_user], ["global"], keys: null, replace: true));
        Assert.Empty((await Read())!.Global!.LiveTvSavedFilters!);
        Assert.Empty((await Edit("tv", "initializeIfAbsent", null, pin))!);
        Assert.Empty((await Read())!.Global!.LiveTvSavedFilters!);

        await Edit("tv", "add", pin);
        Assert.Equal([pin], (await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task AdminCopyAndProfileDeletionPreserveSavedPins()
    {
        var to = Guid.NewGuid();
        await Edit("tv", "add", "group:News");
        await _service.SaveProfileAsync(to, "tv", new() { ClockBehavior = "never" }, savedFilterChange: Change("add", "group:Old"));

        Assert.Equal(1, await _service.CopySettingsAsync(_user, [to], ["global"], ["liveTvSavedFilters"], replace: false));
        Assert.Equal("never", (await _service.GetUserSettingsAsync(to))!.Tv!.ClockBehavior);

        await _service.DeleteProfileAsync(to, "tv");
        var settings = (await _service.GetUserSettingsAsync(to))!;
        Assert.Null(settings.Tv);
        Assert.Equal(["group:News"], settings.Global!.LiveTvSavedFilters);
    }
}
