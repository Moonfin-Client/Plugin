using System.Reflection;
using System.Text.Json;
using System.Xml.Serialization;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Moonfin.LiveTv;
using Moonfin.Upgrades;
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
        _service.Dispose();
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

    private sealed class MemoryUpgradeStateFile : IStateFile
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public T? Read<T>(string path, Func<string, T?> parse) where T : class =>
            _files.TryGetValue(path, out var text) ? parse(text) : null;

        public void Write(string path, string content) => _files[path] = content;
    }

    private class EmbyDelegateProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?>? _handler;

        public static T Create<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
        {
            var proxy = Create<T, EmbyDelegateProxy>();
            ((EmbyDelegateProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            _handler != null ? _handler(method!, args) : null;
    }

    private sealed class VisibleEmbyMovie : MediaBrowser.Controller.Entities.Movies.Movie
    {
        public override bool IsVisible(User user) => true;
    }

    [Fact]
    public async Task StaleTrueBackupWithPrimaryAccessErrorDoesNotCacheTrueAndRetriesPastFiveAttempts()
    {
        _service.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(15);
        var userId = Guid.NewGuid();
        var primaryPath = Path.Combine(_dataPath, $"{userId}.json");
        var backupPath = primaryPath + ".bak";

        File.WriteAllText(backupPath, JsonSerializer.Serialize(new MoonfinUserSettings
        {
            SchemaVersion = 2,
            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
        }));
        File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
        {
            SchemaVersion = 2,
            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
        }));

        var scanAttempts = 0;
        var reachedSevenAttempts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredFalse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _service.BeforePublishHideUpgradesScanForTesting = () =>
        {
            if (Interlocked.Increment(ref scanAttempts) >= 7)
            {
                reachedSevenAttempts.TrySetResult();
            }
        };

        _service.UpgradePreferenceChanged = (enabled, _) =>
        {
            if (!enabled && Volatile.Read(ref scanAttempts) >= 7)
            {
                recoveredFalse.TrySetResult();
            }

            return Task.CompletedTask;
        };

        FileStream? lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var initial = _service.IsAnyHideUpgradesEnabled(out var incomplete);
            Assert.True(initial);
            Assert.True(incomplete);

            await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
            await reachedSevenAttempts.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            lockStream.Dispose();
        }

        await recoveredFalse.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_service.IsAnyHideUpgradesEnabled(out var incompleteAfterRecovery));
        Assert.False(incompleteAfterRecovery);
    }

    [Fact]
    public async Task PendingSyncDuringIncompleteScanWithAppliedStateStillDrainsAndClearsWorkerFlag()
    {
        _service.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(30);
        var userId = Guid.NewGuid();
        var primaryPath = Path.Combine(_dataPath, $"{userId}.json");
        File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
        {
            SchemaVersion = 2,
            Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }
        }));

        var appliedTrue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.UpgradePreferenceChanged = (enabled, _) =>
        {
            if (enabled)
            {
                appliedTrue.TrySetResult();
            }

            return Task.CompletedTask;
        };

        await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
        _service.InvalidateHideUpgradesCache();

        var injectedPending = 0;
        FileStream? lockStream = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _service.BeforePublishHideUpgradesScanForTesting = () =>
            {
                if (Interlocked.CompareExchange(ref injectedPending, 1, 0) == 0)
                {
                    lockStream?.Dispose();
                    lockStream = null;
                    File.WriteAllText(primaryPath, JsonSerializer.Serialize(new MoonfinUserSettings
                    {
                        SchemaVersion = 2,
                        Global = new MoonfinSettingsProfile { HideUpgradesInRecentRows = true }
                    }));
                    _service.InvalidateHideUpgradesCache();
                    _ = _service.ScheduleUpgradePreferenceSyncIfNeeded(userId);
                }
            };

            await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lockStream?.Dispose();
        }

        await appliedTrue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_service.IsUpgradeWorkerRunningForTesting);

        _service.UpgradePreferenceChanged = (_, _) => Task.CompletedTask;
        await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId, force: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_service.IsUpgradeWorkerRunningForTesting);
    }

    [Fact]
    public async Task ArrivalPausedBeforeObserveConvergesWhenHideUpgradesTurnsOnBeforeObserve()
    {
        var ledger = new MediaUpgradeLedger("emby-ledger.json", new MemoryUpgradeStateFile());
        var movieId = Guid.NewGuid();
        var originalDate = new DateTime(2023, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var upgradeDate = new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc);

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = movieId,
            Name = "Emby Pre-Observe Race Movie",
            ProductionYear = 2023,
            DateCreated = new DateTimeOffset(upgradeDate),
        };
        movie.ProviderIds["Tmdb"] = "445566";

        ledger.RecordRemoved(new MediaUpgradeCandidate
        {
            ItemId = Guid.NewGuid(),
            IsEpisode = false,
            TmdbId = "445566",
            DateCreatedUtc = originalDate,
        });

        var fakeLibrary = EmbyDelegateProxy.Create<ILibraryManager>((method, args) =>
            method.Name == nameof(ILibraryManager.GetItemById) && args is { Length: 1 } && args[0] is Guid id && id == movieId
                ? movie
                : null);

        using var upgradeService = new MediaUpgradeService(
            fakeLibrary,
            _service,
            ledger,
            AdminDefaultsMergeTests.NoOpLogger.Create());

        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false });
        await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId).WaitAsync(TimeSpan.FromSeconds(5));

        using var enteredBeforeObserve = new ManualResetEventSlim(false);
        using var allowObserveContinue = new ManualResetEventSlim(false);
        upgradeService.PersistItemDateForTesting = (_, _) => { };
        upgradeService.BeforeObserveArrivalForTesting = () =>
        {
            enteredBeforeObserve.Set();
            allowObserveContinue.Wait(TimeSpan.FromSeconds(5));
        };

        var arrivalTask = Task.Run(() => upgradeService.HandleItemChangedForTesting(movie));
        Assert.True(enteredBeforeObserve.Wait(TimeSpan.FromSeconds(5)));

        upgradeService.ApplyUpgradeDates(hideUpgradesEnabled: true);

        allowObserveContinue.Set();
        await arrivalTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(originalDate, movie.DateCreated.UtcDateTime);
    }

    [Fact]
    public async Task TwoUserMediaBarEndpointOrdersUpgradesIndependently()
    {
        var prevLibrary = PluginServices.LibraryManager;
        var prevUpgrades = PluginServices.MediaUpgrades;
        try
        {
            var ledger = new MediaUpgradeLedger("emby-twouser.json", new MemoryUpgradeStateFile());
            var userHide = new User { Id = Guid.NewGuid(), Name = "emby-hide-user" };
            var userShow = new User { Id = Guid.NewGuid(), Name = "emby-show-user" };

            var upgradedMovieId = Guid.NewGuid();
            var newerMovieId = Guid.NewGuid();
            var originalDate = new DateTime(2023, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            var middleDate = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var upgradeDate = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

            var upgradedMovie = new VisibleEmbyMovie
            {
                Id = upgradedMovieId,
                Name = "Emby Upgraded Movie",
                ProductionYear = 2023,
                DateCreated = new DateTimeOffset(originalDate),
                ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Backdrop, Path = "/tmp/upgraded-backdrop.jpg" } },
            };
            upgradedMovie.ProviderIds["Tmdb"] = "778899";

            var newerMovie = new VisibleEmbyMovie
            {
                Id = newerMovieId,
                Name = "Emby Newer Standard Movie",
                ProductionYear = 2025,
                DateCreated = new DateTimeOffset(middleDate),
                ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Backdrop, Path = "/tmp/newer-backdrop.jpg" } },
            };

            ledger.RecordRemoved(new MediaUpgradeCandidate
            {
                ItemId = Guid.NewGuid(),
                TmdbId = "778899",
                DateCreatedUtc = originalDate,
            });
            ledger.ObserveArrival(
                new MediaUpgradeCandidate
                {
                    ItemId = upgradedMovieId,
                    TmdbId = "778899",
                    DateCreatedUtc = upgradeDate,
                },
                hideUpgradesEnabled: true,
                out _);

            var items = new BaseItem[] { upgradedMovie, newerMovie };
            var fakeLibrary = EmbyDelegateProxy.Create<ILibraryManager>((method, args) =>
            {
                if (method.Name == nameof(ILibraryManager.GetItemById) && args is { Length: 1 } && args[0] is Guid id)
                {
                    return items.FirstOrDefault(i => i.Id == id);
                }

                if (method.Name == nameof(ILibraryManager.GetItemsResult) && args is { Length: 1 } && args[0] is InternalItemsQuery q)
                {
                    IEnumerable<BaseItem> seq = items;
                    if (q.OrderBy is { Length: > 0 })
                    {
                        seq = seq.OrderByDescending(i => i.DateCreated);
                    }

                    if (q.Limit.HasValue)
                    {
                        seq = seq.Take(q.Limit.Value);
                    }

                    return new QueryResult<BaseItem> { Items = seq.ToArray() };
                }

                return null;
            });

            using var upgradeService = new MediaUpgradeService(
                fakeLibrary,
                _service,
                ledger,
                AdminDefaultsMergeTests.NoOpLogger.Create());

            PluginServices.LibraryManager = fakeLibrary;
            PluginServices.MediaUpgrades = upgradeService;

            await SeedAsync("global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 10,
                HideUpgradesInRecentRows = true,
            }, userHide.Id);

            await SeedAsync("global", new MoonfinSettingsProfile
            {
                MediaBarSourceType = "recentlyAdded",
                MediaBarItemCount = 10,
                HideUpgradesInRecentRows = false,
            }, userShow.Id);

            var currentUser = userHide;
            var authContext = EmbyDelegateProxy.Create<IAuthorizationContext>((method, _) =>
                method.Name == nameof(IAuthorizationContext.GetAuthorizationInfo)
                    ? new AuthorizationInfo { User = currentUser }
                    : null);
            var logManager = EmbyDelegateProxy.Create<ILogManager>((_, _) => AdminDefaultsMergeTests.NoOpLogger.Create());
            var resultFactory = EmbyDelegateProxy.Create<IHttpResultFactory>((method, args) =>
            {
                if (method.Name == nameof(IHttpResultFactory.GetResult) && args is { Length: >= 2 } && args[1] is Stream s)
                {
                    using var doc = JsonDocument.Parse(s);
                    return doc.RootElement.GetProperty("Items")
                        .EnumerateArray()
                        .Select(e => e.GetProperty("Id").GetGuid())
                        .ToArray();
                }

                return null;
            });
            var appHost = EmbyDelegateProxy.Create<IApplicationHost>((method, _) =>
            {
                if (method.Name == nameof(IApplicationHost.Resolve))
                {
                    var target = method.GetGenericArguments()[0];
                    if (target == typeof(IAuthorizationContext)) return authContext;
                    if (target == typeof(IHttpResultFactory)) return resultFactory;
                    if (target == typeof(ILogManager)) return logManager;
                }

                return null;
            });

            var settingsApi = new SettingsService(appHost)
            {
                Request = EmbyDelegateProxy.Create<IRequest>(),
                SettingsOverrideForTesting = _service,
            };

            currentUser = userHide;
            var hideResultIds = Assert.IsType<Guid[]>(await settingsApi.Get(new GetMediaBarRequest { Profile = "global" }));

            currentUser = userShow;
            var showResultIds = Assert.IsType<Guid[]>(await settingsApi.Get(new GetMediaBarRequest { Profile = "global" }));

            Assert.Equal(new[] { newerMovieId, upgradedMovieId }, hideResultIds);
            Assert.Equal(new[] { upgradedMovieId, newerMovieId }, showResultIds);
        }
        finally
        {
            PluginServices.LibraryManager = prevLibrary;
            PluginServices.MediaUpgrades = prevUpgrades;
        }
    }

    [Fact]
    public async Task FailedPersistenceRestoresDateCreatedAndRetriesUpgradeBatch()
    {
        var ledger = new MediaUpgradeLedger("emby-persist-retry.json", new MemoryUpgradeStateFile());
        var movieId = Guid.NewGuid();
        var originalDate = new DateTime(2023, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var upgradeDate = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = movieId,
            Name = "Emby Retry Persist Movie",
            ProductionYear = 2023,
            DateCreated = new DateTimeOffset(originalDate),
        };
        movie.ProviderIds["Tmdb"] = "112299";
        ledger.RecordRemoved(new MediaUpgradeCandidate { ItemId = Guid.NewGuid(), TmdbId = "112299", DateCreatedUtc = originalDate });
        ledger.ObserveArrival(new MediaUpgradeCandidate { ItemId = movieId, TmdbId = "112299", DateCreatedUtc = upgradeDate }, true, out _);

        var fakeLibrary = EmbyDelegateProxy.Create<ILibraryManager>((method, args) =>
            method.Name == nameof(ILibraryManager.GetItemById) && args is { Length: 1 } && args[0] is Guid id && id == movieId ? movie : null);

        _service.UpgradeScanRetryDelayForTesting = TimeSpan.FromMilliseconds(20);
        using var upgradeService = new MediaUpgradeService(
            fakeLibrary,
            _service,
            ledger,
            AdminDefaultsMergeTests.NoOpLogger.Create());

        var failWrites = 1;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        upgradeService.PersistItemDateForTesting = (_, _) =>
        {
            if (Interlocked.Exchange(ref failWrites, 0) == 1)
            {
                throw new IOException("Transient Emby DB write failure");
            }

            recovered.TrySetResult();
        };

        Assert.Throws<InvalidOperationException>(() => upgradeService.ApplyUpgradeDates(hideUpgradesEnabled: false));
        Assert.Equal(originalDate, movie.DateCreated.UtcDateTime);

        Volatile.Write(ref failWrites, 1);
        var userId = Guid.NewGuid();
        await SeedAsync("global", new MoonfinSettingsProfile { HideUpgradesInRecentRows = false }, userId);
        await _service.ScheduleUpgradePreferenceSyncIfNeeded(userId, force: true).WaitAsync(TimeSpan.FromSeconds(5));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(upgradeDate, movie.DateCreated.UtcDateTime);
    }

    [Fact]
    public async Task SyncHideWatchedForAffectedUsersValidatesBaselineGenerationZero()
    {
        var prevUserManager = PluginServices.UserManager;
        try
        {
            var user = new User { Id = Guid.NewGuid(), Name = "emby-unsaved-user" };
            var config = new MediaBrowser.Model.Configuration.UserConfiguration { HidePlayedInLatest = true };
            var userManager = EmbyDelegateProxy.Create<IUserManager>((method, args) =>
            {
                if (method.Name == nameof(IUserManager.GetUserList))
                {
                    return new[] { user };
                }

                if (method.Name == nameof(IUserManager.GetUserById))
                {
                    return user;
                }

                if (method.Name == nameof(IUserManager.GetUserConfiguration))
                {
                    return config;
                }

                if (method.Name == nameof(IUserManager.UpdateConfiguration) && args is { Length: 2 } && args[1] is MediaBrowser.Model.Configuration.UserConfiguration updated)
                {
                    config.HidePlayedInLatest = updated.HidePlayedInLatest;
                    return null;
                }

                return null;
            });

            PluginServices.UserManager = userManager;
            _service.AdminDefaultsOverrideForTesting = profile =>
                profile == "global" ? new MoonfinSettingsProfile { HideWatchedInRecentRows = true } : null;

            var concurrentSaveTriggered = 0;
            _service.BeforeNativeSyncWaitForTesting = id =>
            {
                if (id == user.Id && Interlocked.CompareExchange(ref concurrentSaveTriggered, 1, 0) == 0)
                {
                    SeedAsync("global", new MoonfinSettingsProfile { HideWatchedInRecentRows = false }, user.Id)
                        .GetAwaiter().GetResult();
                }
            };

            await _service.SyncHideWatchedForAffectedUsersAsync();

            Assert.False(config.HidePlayedInLatest);
        }
        finally
        {
            PluginServices.UserManager = prevUserManager;
        }
    }
}
