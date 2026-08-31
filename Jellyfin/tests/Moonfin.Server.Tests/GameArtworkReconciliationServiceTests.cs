using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Data.Events;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class GameArtworkReconciliationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-artwork-reconciliation-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetSystemsAsync_DoesNotHideGamesWhileArtworkCatalogIsEmpty()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "Chrono Trigger.sfc"), [1, 2, 3]);

        var libraryId = Guid.NewGuid().ToString();
        var libraryManager = new FakeLibraryManager(
        [
            new VirtualFolderInfo
            {
                Name = "Games",
                ItemId = libraryId,
                Locations = [libraryRoot],
            },
        ]);
        var games = new GamesService(libraryManager);
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        var service = new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance);

        var systems = await service.GetSystemsAsync(libraryId);

        var system = Assert.Single(systems);
        Assert.Equal("SNES", system.Id);
        Assert.Equal(1, system.GameCount);
    }

    [Fact]
    public async Task GameLibraryFileChange_TriggersReconciliationForNewRoms()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "First.sfc"), [1, 2, 3]);

        var libraryId = Guid.NewGuid().ToString();
        var libraryManager = new FakeLibraryManager(
        [
            new VirtualFolderInfo
            {
                Name = "Games",
                ItemId = libraryId,
                Locations = [libraryRoot],
            },
        ]);
        var games = new GamesService(libraryManager);
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        var service = new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForDistinctGameCountAsync(catalog, libraryId, "snes", 1);

            // Per-library scans bypass TaskCompleted, so the ROM-root watcher must find this file.
            await File.WriteAllBytesAsync(Path.Combine(systemRoot, "Added.sfc"), [4, 5, 6]);
            await WaitForDistinctGameCountAsync(catalog, libraryId, "snes", 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // Watchers used to be built once, at StartAsync, from a snapshot of the configured libraries.
    // Selecting a new game library on the configuration page therefore left its root unwatched and
    // its ROMs uncataloged until a global scheduled scan or a server restart -- exactly what the
    // per-library-scan support this class's watcher exists for is meant to avoid.
    [Fact]
    public async Task OnGameLibrariesChanged_WatchesAndScansANewlySelectedRootWithoutRestart()
    {
        var firstRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(firstRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(firstRoot, "SNES", "First.sfc"), [1, 2, 3]);

        var secondRoot = Path.Combine(_root, "MoreGames");
        Directory.CreateDirectory(Path.Combine(secondRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(secondRoot, "SNES", "Second.sfc"), [4, 5, 6]);

        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = firstId, Locations = [firstRoot] },
        };
        var service = CreateWatchingService(folders, out var catalog);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForDistinctGameCountAsync(catalog, firstId, "snes", 1);
            Assert.Equal([firstRoot], service.WatchedLibraryRootsForTests);

            // The configuration page selects a second library. Its ROM is already on disk, so it
            // raises no filesystem event: only the queued reconciliation can discover it.
            folders.Add(new VirtualFolderInfo { Name = "MoreGames", ItemId = secondId, Locations = [secondRoot] });
            service.OnGameLibrariesChanged();

            await WaitForDistinctGameCountAsync(catalog, secondId, "snes", 1);
            await WaitForWatchedRootsAsync(service, [firstRoot, secondRoot]);

            // And the new root is genuinely watched from here on, not merely scanned once.
            await File.WriteAllBytesAsync(Path.Combine(secondRoot, "SNES", "Third.sfc"), [7, 8, 9]);
            await WaitForDistinctGameCountAsync(catalog, secondId, "snes", 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // Deselecting a library must also drop its watcher. This asserts the watcher set directly
    // rather than asserting that the removed root's ROMs stop being cataloged: reconciliation only
    // ever scans currently-configured roots, so the catalog assertion passes even when a stale
    // watcher is still running (it would just trigger pointless reconciliation passes forever).
    [Fact]
    public async Task OnGameLibrariesChanged_StopsWatchingADeselectedRoot()
    {
        var firstRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(firstRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(firstRoot, "SNES", "First.sfc"), [1, 2, 3]);

        var secondRoot = Path.Combine(_root, "MoreGames");
        Directory.CreateDirectory(Path.Combine(secondRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(secondRoot, "SNES", "Second.sfc"), [4, 5, 6]);

        var firstId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = firstId, Locations = [firstRoot] },
            new() { Name = "MoreGames", ItemId = Guid.NewGuid().ToString(), Locations = [secondRoot] },
        };
        var service = CreateWatchingService(folders, out var catalog);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForWatchedRootsAsync(service, [firstRoot, secondRoot]);

            folders.RemoveAt(1);
            service.OnGameLibrariesChanged();

            await WaitForWatchedRootsAsync(service, [firstRoot]);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_DoesNotAllowAnInProgressWatcherSyncToRecreateWatchers()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "SNES", "First.sfc"), [1, 2, 3]);

        var libraryId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = libraryId, Locations = [libraryRoot] },
        };
        var blockNextResolution = 0;
        var resolutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseResolution = new ManualResetEventSlim();
        var libraryManager = new FakeLibraryManager(
            folders,
            () =>
            {
                if (Interlocked.Exchange(ref blockNextResolution, 0) == 0)
                {
                    return;
                }

                resolutionStarted.TrySetResult();
                Assert.True(releaseResolution.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting to release library-root resolution");
            });
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog-racing-stop"));
        var service = new GameArtworkReconciliationService(
            new GamesService(libraryManager),
            (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService)),
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null);

        await service.StartAsync(CancellationToken.None);
        Task? stopTask = null;
        try
        {
            await WaitForDistinctGameCountAsync(catalog, libraryId, "snes", 1);
            await WaitForWatchedRootsAsync(service, [libraryRoot]);

            Interlocked.Exchange(ref blockNextResolution, 1);
            service.OnGameLibrariesChanged();
            await resolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            stopTask = service.StopAsync(CancellationToken.None);
            await WaitForWatchedRootsAsync(service, []);
            releaseResolution.Set();
            await stopTask;

            Assert.Empty(service.WatchedLibraryRootsForTests);
        }
        finally
        {
            releaseResolution.Set();
            if (stopTask == null)
            {
                await service.StopAsync(CancellationToken.None);
            }
            else
            {
                await stopTask;
            }
        }
    }

    [Fact]
    public void WatcherEventAfterTheStopSourceIsDisposedDoesNotThrow()
    {
        // A watcher callback reads the stop source, then can be descheduled for the whole of
        // StopAsync and resume once that source has been disposed. Callbacks run on their own
        // threads, so anything thrown there is unhandled and ends the process. The window is too
        // narrow to hit by racing threads, so this drives the same field state directly.
        var libraryManager = new FakeLibraryManager(new List<VirtualFolderInfo>());
        var service = new GameArtworkReconciliationService(
            new GamesService(libraryManager),
            (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService)),
            new GameArtworkCatalog(Path.Combine(_root, "catalog-disposed-stop")),
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null);

        var stopField = typeof(GameArtworkReconciliationService)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        stopField.SetValue(service, disposed);

        var debounce = typeof(GameArtworkReconciliationService)
            .GetMethod("DebounceLibraryFileSystemReconciliation", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var fault = Record.Exception(() => debounce.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: null,
            culture: null));

        Assert.Null(fault);
    }

    [Fact]
    public void Scheduler_ForcesBulkAfterFourInteractiveSelections()
    {
        var scheduler = new ArtworkWorkScheduler();
        scheduler.Enqueue(Work("bulk", ArtworkLane.Bulk));
        for (var i = 0; i < 5; i++)
        {
            scheduler.Enqueue(Work("interactive-" + i, ArtworkLane.Interactive));
        }

        var lanes = Enumerable.Range(0, 5).Select(_ =>
        {
            Assert.True(scheduler.TryDequeue(out var work));
            return work.Lane;
        }).ToArray();

        Assert.Equal(
            new[] { ArtworkLane.Interactive, ArtworkLane.Interactive, ArtworkLane.Interactive, ArtworkLane.Interactive, ArtworkLane.Bulk },
            lanes);
    }

    [Fact]
    public void Scheduler_RotatesPreviewSystems()
    {
        var scheduler = new ArtworkWorkScheduler();
        scheduler.Enqueue(Work("snes-a", ArtworkLane.Preview, "snes"));
        scheduler.Enqueue(Work("nes-a", ArtworkLane.Preview, "nes"));
        scheduler.Enqueue(Work("snes-b", ArtworkLane.Preview, "snes"));

        var games = Enumerable.Range(0, 3).Select(_ =>
        {
            Assert.True(scheduler.TryDequeue(out var work));
            return work.Candidate.GameId;
        }).ToArray();

        Assert.Equal(new[] { "snes-a", "nes-a", "snes-b" }, games);
    }

    [Fact]
    public void RemoteQueue_AtCapacityGuaranteesPreviewAdmissionWithoutLettingInteractiveEvictIt()
    {
        const int bestEffortCapacity = 20_000;
        var service = BuildService(_root);
        for (var index = 0; index < bestEffortCapacity; index++)
        {
            Assert.True(service.EnqueueRemoteForTest(Candidate("library", $"bulk-{index}"), ArtworkLane.Bulk));
        }

        var preview = Candidate("library", "preview");
        Assert.True(service.EnqueueRemoteForTest(preview, ArtworkLane.Preview));
        Assert.Contains("remote:" + preview.Key.StorageKey, QueuedRemoteIdentities(service));

        Assert.True(service.EnqueueRemoteForTest(Candidate("library", "interactive"), ArtworkLane.Interactive));
        Assert.Contains("remote:" + preview.Key.StorageKey, QueuedRemoteIdentities(service));
        Assert.Equal(bestEffortCapacity + 1, QueuedRemoteIdentities(service).Count);

        Assert.False(service.EnqueueRemoteForTest(Candidate("library", "rejected-bulk"), ArtworkLane.Bulk));
        Assert.Equal(bestEffortCapacity + 1, QueuedRemoteIdentities(service).Count);
    }

    private static ICollection<string> QueuedRemoteIdentities(GameArtworkReconciliationService service)
    {
        var field = typeof(GameArtworkReconciliationService)
            .GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queued = (System.Collections.IDictionary)field.GetValue(service)!;
        return queued.Keys.Cast<string>().ToArray();
    }

    [Fact]
    public async Task SaturatedQueue_AdvancesToTheNextUnresolvedPreviewCandidateAfterAMiss()
    {
        const int bestEffortCapacity = 20_000;
        var libraryRoot = Path.Combine(_root, "Games");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "First.sfc"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "Second.sfc"), [2]);

        var libraryId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = libraryId, Locations = [libraryRoot] },
        };
        var service = CreateWatchingService(folders, out var catalog);
        var gamesField = typeof(GameArtworkReconciliationService)
            .GetField("_games", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var games = (GamesService)gamesField.GetValue(service)!;
        var candidates = games.GetGames(libraryId, null)
            .Select(game => ResolveCandidate(service, libraryId, game.Id))
            .ToDictionary(candidate => candidate.GameId, StringComparer.Ordinal);
        foreach (var candidate in candidates.Values)
        {
            await catalog.GetOrCreateAsync(candidate.Key, candidate.SystemId, "libretro-thumbnails-v3", candidate.GameId);
        }

        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync(
            libraryId,
            "snes",
            "inventory-a",
            candidates.Keys);
        var first = candidates[discovery.PreviewCandidateGameIds[0]];
        var second = candidates[discovery.PreviewCandidateGameIds[1]];
        for (var index = 0; index < bestEffortCapacity; index++)
        {
            Assert.True(service.EnqueueRemoteForTest(Candidate("bulk-library", $"bulk-{index}"), ArtworkLane.Bulk));
        }

        Assert.True(service.EnqueueRemoteForTest(first, ArtworkLane.Preview));
        Assert.False(service.EnqueueRemoteForTest(second, ArtworkLane.Bulk));

        await catalog.MarkMissingAsync(first.Key, first.SystemId, "libretro-thumbnails-v3");
        await RefreshPreviewSelectionAsync(service, first);

        Assert.Contains("remote:" + second.Key.StorageKey, QueuedRemoteIdentities(service));
    }

    [Fact]
    public async Task RequestRefresh_DoesNotItselfRepublishThePreviewSelection()
    {
        var (service, catalog, libraryId, candidates) = await CreatePreviewFixtureAsync(4);
        foreach (var candidate in candidates.Values)
        {
            await catalog.MarkOriginalReadyAsync(candidate.Key, candidate.SystemId, $"C:/catalog/{candidate.GameId}.png");
        }

        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync(
            libraryId,
            "snes",
            "inventory-a",
            candidates.Keys);
        var completed = await catalog.RecomputePreviewSelectionAsync(libraryId, "snes", "inventory-a");
        var refreshed = candidates[discovery.PreviewCandidateGameIds[0]];

        Assert.True(await service.RequestRefreshAsync(libraryId, refreshed.GameId, "boxart"));

        var after = await catalog.GetSystemAsync(libraryId, "snes");
        Assert.Equal(completed.PreviewGameIds, after.PreviewGameIds);
        Assert.Equal(completed.PreviewSelectionGeneration, after.PreviewSelectionGeneration);
        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.GetAsync(refreshed.Key))?.State);
    }

    [Fact]
    public async Task ReopeningMetadataDeferred_DoesNotItselfRepublishThePreviewSelection()
    {
        var (service, catalog, libraryId, candidates) = await CreatePreviewFixtureAsync(5);
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync(
            libraryId,
            "snes",
            "inventory-a",
            candidates.Keys);
        var reopened = candidates[discovery.PreviewCandidateGameIds[0]];
        var started = DateTimeOffset.UtcNow.AddHours(-25);
        await catalog.MarkMetadataPendingAsync(
            reopened.Key,
            reopened.SystemId,
            "libretro-thumbnails-v3",
            started,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(24));
        await catalog.MarkMetadataPendingAsync(
            reopened.Key,
            reopened.SystemId,
            "libretro-thumbnails-v3",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(3),
            TimeSpan.FromHours(24));
        foreach (var candidate in discovery.PreviewCandidateGameIds.Skip(1).Select(gameId => candidates[gameId]))
        {
            await catalog.MarkOriginalReadyAsync(candidate.Key, candidate.SystemId, $"C:/catalog/{candidate.GameId}.png");
        }

        var completed = await catalog.RecomputePreviewSelectionAsync(libraryId, "snes", "inventory-a");

        Assert.True(await QueueCandidateAsync(service, reopened, reopenMetadataDeferred: true));

        var after = await catalog.GetSystemAsync(libraryId, "snes");
        Assert.Equal(completed.PreviewGameIds, after.PreviewGameIds);
        Assert.Equal(completed.PreviewSelectionGeneration, after.PreviewSelectionGeneration);
        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.GetAsync(reopened.Key))?.State);
    }

    private async Task<(GameArtworkReconciliationService Service, GameArtworkCatalog Catalog, string LibraryId,
        Dictionary<string, GameArtworkReconciliationService.ArtworkCandidate> Candidates)> CreatePreviewFixtureAsync(int gameCount)
    {
        var libraryRoot = Path.Combine(_root, $"Games-{gameCount}");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        for (var index = 0; index < gameCount; index++)
        {
            await File.WriteAllBytesAsync(Path.Combine(systemRoot, $"Game-{index}.sfc"), [(byte)index]);
        }

        var libraryId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = libraryId, Locations = [libraryRoot] },
        };
        var service = CreateWatchingService(folders, out var catalog);
        var gamesField = typeof(GameArtworkReconciliationService)
            .GetField("_games", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var games = (GamesService)gamesField.GetValue(service)!;
        var candidates = games.GetGames(libraryId, null)
            .Select(game => ResolveCandidate(service, libraryId, game.Id))
            .ToDictionary(candidate => candidate.GameId, StringComparer.Ordinal);
        foreach (var candidate in candidates.Values)
        {
            await catalog.GetOrCreateAsync(candidate.Key, candidate.SystemId, "libretro-thumbnails-v3", candidate.GameId);
        }

        return (service, catalog, libraryId, candidates);
    }

    private static async Task<bool> QueueCandidateAsync(
        GameArtworkReconciliationService service,
        GameArtworkReconciliationService.ArtworkCandidate candidate,
        bool reopenMetadataDeferred)
    {
        var method = typeof(GameArtworkReconciliationService)
            .GetMethod("QueueCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<bool>)method.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [candidate, ArtworkLane.Preview, CancellationToken.None, reopenMetadataDeferred],
            culture: null)!;
        return await task;
    }

    private static GameArtworkReconciliationService.ArtworkCandidate ResolveCandidate(
        GameArtworkReconciliationService service,
        string libraryId,
        string gameId)
    {
        var method = typeof(GameArtworkReconciliationService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "TryCreateCandidate" &&
                candidate.GetParameters().FirstOrDefault()?.ParameterType == typeof(string));
        return Assert.IsType<GameArtworkReconciliationService.ArtworkCandidate>(method.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [libraryId, gameId, GameThumbService.ThumbKind.Boxart, null],
            culture: null));
    }

    private static Task RefreshPreviewSelectionAsync(
        GameArtworkReconciliationService service,
        GameArtworkReconciliationService.ArtworkCandidate candidate)
    {
        var method = typeof(GameArtworkReconciliationService)
            .GetMethod("RefreshPreviewSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [candidate, CancellationToken.None],
            culture: null)!;
    }

    [Fact]
    public async Task StopAsync_CompletesCleanlyAndDisposesTheTokenSource()
    {
        var service = BuildService(_root);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None); // must be restartable
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_AllowsAnActiveButPartiallyInitializedLifetime()
    {
        var service = BuildService(_root);
        StopSourceField.SetValue(service, new CancellationTokenSource());

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_HonorsItsDeadlineAndAllowsRestartAfterGrace()
    {
        var libraryRoot = Path.Combine(_root, "Games-blocked-stop");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "SNES", "First.sfc"), [1, 2, 3]);

        var libraryId = Guid.NewGuid().ToString();
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = libraryId, Locations = [libraryRoot] },
        };
        var blockNextResolution = 0;
        var resolutionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseResolution = new ManualResetEventSlim();
        var libraryManager = new FakeLibraryManager(
            folders,
            () =>
            {
                if (Interlocked.Exchange(ref blockNextResolution, 0) != 0)
                {
                    resolutionStarted.TrySetResult();
                    releaseResolution.Wait();
                }
            });
        var service = new GameArtworkReconciliationService(
            new GamesService(libraryManager),
            (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService)),
            new GameArtworkCatalog(Path.Combine(_root, "catalog-blocked-stop")),
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null,
            watchLibraryFileSystem: true,
            workerStopGraceForTests: TimeSpan.FromMilliseconds(200));

        await service.StartAsync(CancellationToken.None);
        try
        {
            Interlocked.Exchange(ref blockNextResolution, 1);
            service.OnGameLibrariesChanged();
            await resolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await service.StopAsync(stopDeadline.Token).WaitAsync(TimeSpan.FromSeconds(2));

            // The blocked worker is parked in a non-cancelable call, so cleanup gives up on it
            // after the grace and restart must still succeed -- otherwise one wedged filesystem
            // call leaves the plugin permanently unstartable.
            await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            releaseResolution.Set();
        }
        finally
        {
            releaseResolution.Set();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WatcherSync_FromCanceledLifetimeDoesNotUseTheReplacementStopSource()
    {
        var libraryRoot = Path.Combine(_root, "Games-canceled-lifetime");
        Directory.CreateDirectory(libraryRoot);
        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = Guid.NewGuid().ToString(), Locations = [libraryRoot] },
        };
        var service = CreateWatchingService(folders, out _);
        using var replacementStop = new CancellationTokenSource();
        using var canceledLifetime = new CancellationTokenSource();
        canceledLifetime.Cancel();
        StopSourceField.SetValue(service, replacementStop);

        var sync = typeof(GameArtworkReconciliationService)
            .GetMethod("SyncLibraryWatchers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sync.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [canceledLifetime.Token],
            culture: null);

        Assert.Empty(service.WatchedLibraryRootsForTests);
    }

    [Fact]
    public async Task Restart_DoesNotLetCanceledDelayedPreviewWorkRemoveItsReplacement()
    {
        var service = BuildService(_root);
        var candidate = Candidate("library", "delayed-preview");
        var thumbnailCandidate = candidate with { OriginalPath = "C:/catalog/delayed-preview.png" };
        var retryAfter = DateTimeOffset.UtcNow.AddHours(1);

        await service.StartAsync(CancellationToken.None);
        Assert.True(service.EnqueueRemoteForTest(candidate, ArtworkLane.Preview, retryAfter));
        QueueThumbnail(service, thumbnailCandidate, retryAfter);
        await service.StopAsync(CancellationToken.None);
        Assert.DoesNotContain("remote:" + candidate.Key.StorageKey, QueuedRemoteIdentities(service));
        Assert.DoesNotContain("thumbnail:" + candidate.Key.StorageKey, QueuedThumbnailIdentities(service));

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(service.EnqueueRemoteForTest(candidate, ArtworkLane.Preview, retryAfter));
            QueueThumbnail(service, thumbnailCandidate, retryAfter);
            await Task.Delay(100);
            Assert.Contains("remote:" + candidate.Key.StorageKey, QueuedRemoteIdentities(service));
            Assert.Contains("thumbnail:" + candidate.Key.StorageKey, QueuedThumbnailIdentities(service));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static void QueueThumbnail(
        GameArtworkReconciliationService service,
        GameArtworkReconciliationService.ArtworkCandidate candidate,
        DateTimeOffset notBefore)
    {
        var method = typeof(GameArtworkReconciliationService)
            .GetMethod("QueueThumbnail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [candidate, notBefore],
            culture: null);
    }

    private static ICollection<string> QueuedThumbnailIdentities(GameArtworkReconciliationService service)
    {
        var field = typeof(GameArtworkReconciliationService)
            .GetField("_thumbnailQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var queued = (IEnumerable<KeyValuePair<string, int>>)field.GetValue(service)!;
        return queued.Select(entry => entry.Key).ToArray();
    }

    [Fact]
    public async Task RemoteWorker_SurvivesAPersistFailureInsideItsErrorHandler()
    {
        var service = BuildService(_root, out var catalog);
        const string libraryId = "library";

        // AtomicFile.WriteAllText's first step is creating "<path>.tmp"; a directory sitting at
        // that exact path makes every catalog persist for this library throw
        // UnauthorizedAccessException -- exactly the kind of failure that must happen *inside*
        // RetryRemoteAsync's own catch handler for this test to be meaningful.
        var blockedTmpPath = catalog.GetLibraryPath(libraryId) + ".tmp";
        Directory.CreateDirectory(blockedTmpPath);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Two poisoned candidates: with exactly two remote workers, and each worker able to
            // consume at most one item before an unhandled persist failure ends its task (the
            // pre-fix bug), both workers are guaranteed dead after these two, regardless of
            // scheduling order.
            var poisonedA = Candidate(libraryId, "poisoned-a");
            var poisonedB = Candidate(libraryId, "poisoned-b");
            service.EnqueueRemoteForTest(poisonedA);
            service.EnqueueRemoteForTest(poisonedB);

            await WaitForStateAsync(catalog, poisonedA.Key, ArtworkCatalogState.Retryable);
            await WaitForStateAsync(catalog, poisonedB.Key, ArtworkCatalogState.Retryable);

            // Unblock catalog writes, then queue a third candidate. If both workers died silently
            // (the pre-fix bug), nothing will ever dequeue this and the wait below times out.
            Directory.Delete(blockedTmpPath, recursive: true);
            var recovered = Candidate(libraryId, "recovered");
            service.EnqueueRemoteForTest(recovered);

            await WaitForStateAsync(catalog, recovered.Key, ArtworkCatalogState.Retryable);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Best-effort file creation for the containment test. UNC and foreign-drive targets are
    /// expected to fail here; the local ones must succeed or the assertion they support is empty.
    /// </summary>
    private static void TryCreateFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Unreachable target (UNC / other drive). The guard is the only refusal path for these.
        }
    }

    [Fact]
    public async Task GetLocalArtworkAsync_RefusesEveryVariantOfAPathOutsideTheManagedArtworkRoot()
    {
        var service = BuildService(_root, out var catalog);
        const string libraryId = "library";
        var driveRoot = Path.GetPathRoot(_root) ?? "C:\\";
        var otherDrive = driveRoot.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? "Y:\\" : "Z:\\";

        // Managed root is Path.Combine(Directory.GetParent(catalog.RootPath), "game_thumbs") -- i.e. root/game_thumbs.
        var outsidePaths = new[]
        {
            Path.Combine(_root, "outside", "game.png"), // sibling directory of game_thumbs
            Path.Combine(_root, "game_thumbs", "..", "..", "escape.png"), // traversal above root
            "\\\\evil-server\\share\\game.png", // UNC
            Path.Combine(otherDrive, "other", "game.png"), // drive-qualified, different drive
        };

        for (var i = 0; i < outsidePaths.Length; i++)
        {
            // Create the file wherever that is possible locally. Without this the assertions
            // below pass on GetLocalArtworkAsync's File.Exists check and would keep passing
            // with the containment guard deleted -- which is the only thing under test here.
            // UNC and foreign-drive paths cannot be created, so they lean on the guard alone.
            TryCreateFile(outsidePaths[i]);

            var key = ArtworkCatalogKey.Create(libraryId, $"SNES/Outside-{i}.sfc", $"fingerprint-outside-{i}", "boxart");
            var gameId = "outside-" + i;
            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", gameId);
            var entry = await catalog.MarkOriginalReadyAsync(key, "snes", outsidePaths[i]);

            var asset = await service.GetLocalArtworkAsync(
                libraryId,
                gameId,
                "boxart",
                entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Assert.Null(asset);
        }
    }

    [Fact]
    public async Task GetLocalArtworkAsync_RefusesAStaleRevisionButServesTheCurrentOneFromInsideTheManagedRoot()
    {
        var service = BuildService(_root, out var catalog);
        const string libraryId = "library";
        var key = ArtworkCatalogKey.Create(libraryId, "SNES/Mario.sfc", "fingerprint-mario", "boxart");

        // catalog root is Path.Combine(root, "catalog"), so the managed artwork root is root/game_thumbs.
        var managedPath = Path.Combine(_root, "game_thumbs", "mario.png");
        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        await File.WriteAllBytesAsync(managedPath, new byte[] { 1, 2, 3 });

            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", "mario");
            var entry = await catalog.MarkOriginalReadyAsync(key, "snes", managedPath);

            Assert.Null(await service.GetLocalArtworkAsync(libraryId, "mario", "boxart", "not-the-real-revision"));

            var asset = await service.GetLocalArtworkAsync(
                libraryId,
                "mario",
                "boxart",
                entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotNull(asset);
        Assert.Equal(managedPath, asset!.Path);
    }

    [Fact]
    public async Task TaskManagerSubscription_TriggersReconciliationOnlyForRefreshLibraryWhileStartedAndUnsubscribesOnStop()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "Existing.sfc"), new byte[] { 1 });

        var libraryId = Guid.NewGuid().ToString();
        var libraryManager = new FakeLibraryManager(
        [
            new VirtualFolderInfo
            {
                Name = "Games",
                ItemId = libraryId,
                Locations = [libraryRoot],
            },
        ]);
        var games = new GamesService(libraryManager);
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        var taskManager = new FakeTaskManager();
        var service = new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager,
            watchLibraryFileSystem: false);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Startup reconciliation picks up the one pre-existing ROM.
            await WaitForDistinctGameCountAsync(catalog, libraryId, "snes", 1);

            // A completed task that isn't "RefreshLibrary" must not trigger another pass.
            await File.WriteAllBytesAsync(Path.Combine(systemRoot, "Ignored.sfc"), new byte[] { 2 });
            taskManager.Raise("SomeOtherTask");
            await Task.Delay(300);
            Assert.Equal(1, await DistinctGameCountAsync(catalog, libraryId, "snes"));

            // A completed "RefreshLibrary" must trigger reconciliation while started.
            taskManager.Raise("RefreshLibrary");
            await WaitForDistinctGameCountAsync(catalog, libraryId, "snes", 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // After StopAsync the handler must be unsubscribed: a new ROM plus the same event must
        // not change the catalog anymore.
        await File.WriteAllBytesAsync(Path.Combine(systemRoot, "AfterStop.sfc"), new byte[] { 3 });
        taskManager.Raise("RefreshLibrary");
        await Task.Delay(300);
        Assert.Equal(2, await DistinctGameCountAsync(catalog, libraryId, "snes"));
    }

    // Overflows arrive in bursts during a romset copy, and queueing a pass directly ran a full
    // library enumeration back to back for the length of the import. Debouncing still guarantees
    // the pass, since the overflow itself arms the timer.
    [Fact]
    public async Task WatcherOverflow_WaitsForTheQuietPeriodInsteadOfReconcilingImmediately()
    {
        var service = BuildService(_root);
        var stop = new CancellationTokenSource();
        StopSourceField.SetValue(service, stop);
        try
        {
            RaiseWatcherOverflow(service);

            Assert.Equal(0, PendingReconciliations(service));
            await WaitForPendingReconciliationAsync(service);
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static readonly FieldInfo StopSourceField = typeof(GameArtworkReconciliationService)
        .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void RaiseWatcherOverflow(GameArtworkReconciliationService service)
    {
        var handler = typeof(GameArtworkReconciliationService)
            .GetMethod("OnLibraryWatcherError", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [service, new ErrorEventArgs(new InternalBufferOverflowException())],
            culture: null);
    }

    // The service is never started here, so nothing drains the semaphore and its count is stable.
    private static int PendingReconciliations(GameArtworkReconciliationService service)
    {
        var field = typeof(GameArtworkReconciliationService)
            .GetField("_reconciliationAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((SemaphoreSlim)field.GetValue(service)!).CurrentCount;
    }

    private static async Task WaitForPendingReconciliationAsync(GameArtworkReconciliationService service)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (PendingReconciliations(service) > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the quiet period to queue the reconciliation pass");
    }

    // Only overflow leaves a working watcher. Any other error -- a deleted or disconnected root --
    // leaves one that never reports again, and SyncLibraryWatchers skips any root already in the
    // dictionary, so without recycling the root stays unwatched until a restart.
    [Fact]
    public async Task WatcherFailure_RecreatesTheWatcherSoTheRootStaysWatched()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "SNES"));
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "SNES", "First.sfc"), [1, 2, 3]);

        var folders = new List<VirtualFolderInfo>
        {
            new() { Name = "Games", ItemId = Guid.NewGuid().ToString(), Locations = [libraryRoot] },
        };
        var service = CreateWatchingService(folders, out _);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForWatchedRootsAsync(service, [libraryRoot]);
            var original = LibraryWatchers(service)[libraryRoot];

            RaiseWatcherError(service, original, new IOException("root disconnected"));

            await WaitUntilAsync(() => LibraryWatchers(service).TryGetValue(libraryRoot, out var current)
                && !ReferenceEquals(current, original));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WatcherOverflow_KeepsTheWatcherItAlreadyHas()
    {
        var service = BuildService(_root);
        var stop = new CancellationTokenSource();
        StopSourceField.SetValue(service, stop);
        try
        {
            Directory.CreateDirectory(_root);
            using var watcher = new FileSystemWatcher(_root) { EnableRaisingEvents = true };
            LibraryWatchers(service).Add(_root, watcher);

            RaiseWatcherError(service, watcher, new InternalBufferOverflowException());

            Assert.Same(watcher, LibraryWatchers(service)[_root]);
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static Dictionary<string, FileSystemWatcher> LibraryWatchers(GameArtworkReconciliationService service)
    {
        var field = typeof(GameArtworkReconciliationService)
            .GetField("_libraryWatchers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Dictionary<string, FileSystemWatcher>)field.GetValue(service)!;
    }

    private static void RaiseWatcherError(
        GameArtworkReconciliationService service,
        FileSystemWatcher sender,
        Exception error)
    {
        var handler = typeof(GameArtworkReconciliationService)
            .GetMethod("OnLibraryWatcherError", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [sender, new ErrorEventArgs(error)],
            culture: null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for the watcher to be recreated");
    }

    private static async Task<int> DistinctGameCountAsync(GameArtworkCatalog catalog, string libraryId, string systemId)
    {
        var projection = await catalog.GetSystemProjectionAsync(libraryId, systemId);
        return projection?.Entries.Select(entry => entry.GameId).Distinct().Count() ?? 0;
    }

    private static async Task WaitForDistinctGameCountAsync(GameArtworkCatalog catalog, string libraryId, string systemId, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await DistinctGameCountAsync(catalog, libraryId, systemId) == count)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {systemId} to reach {count} distinct games");
    }

    /// <summary>
    /// Minimal <see cref="ITaskManager"/> stand-in that only supports what
    /// <see cref="GameArtworkReconciliationService"/> actually uses: subscribing/unsubscribing
    /// <see cref="TaskCompleted"/> and raising it. Every other member is stubbed to throw.
    /// </summary>
    private sealed class FakeTaskManager : ITaskManager
    {
        public event EventHandler<TaskCompletionEventArgs>? TaskCompleted;

        event EventHandler<GenericEventArgs<IScheduledTaskWorker>>? ITaskManager.TaskExecuting
        {
            add { }
            remove { }
        }

        IReadOnlyList<IScheduledTaskWorker> ITaskManager.ScheduledTasks => Array.Empty<IScheduledTaskWorker>();

        public void Raise(string key) => TaskCompleted?.Invoke(this, new TaskCompletionEventArgs(null!, new TaskResult { Key = key }));

        void ITaskManager.CancelIfRunningAndQueue<T>(TaskOptions options) => throw new NotImplementedException();

        void ITaskManager.CancelIfRunningAndQueue<T>() => throw new NotImplementedException();

        void ITaskManager.CancelIfRunning<T>() => throw new NotImplementedException();

        void ITaskManager.QueueScheduledTask<T>(TaskOptions options) => throw new NotImplementedException();

        void ITaskManager.QueueScheduledTask<T>() => throw new NotImplementedException();

        void ITaskManager.QueueIfNotRunning<T>() => throw new NotImplementedException();

        void ITaskManager.QueueScheduledTask(IScheduledTask task, TaskOptions options) => throw new NotImplementedException();

        void ITaskManager.AddTasks(IEnumerable<IScheduledTask> tasks) => throw new NotImplementedException();

        void ITaskManager.Cancel(IScheduledTaskWorker task) => throw new NotImplementedException();

        Task ITaskManager.Execute(IScheduledTaskWorker task, TaskOptions options) => throw new NotImplementedException();

        void ITaskManager.Execute<T>() => throw new NotImplementedException();

        void IDisposable.Dispose()
        {
        }
    }

    private static GameArtworkReconciliationService.ArtworkWork Work(string gameId, ArtworkLane lane, string system = "snes")
    {
        var key = ArtworkCatalogKey.Create("library", gameId + ".sfc", "fingerprint", "boxart");
        var source = new GameThumbSource(new[] { "snes" }, gameId, "C:/roms/" + gameId + ".sfc", system, false);
        var candidate = new GameArtworkReconciliationService.ArtworkCandidate(gameId, system, GameThumbService.ThumbKind.Boxart, source, key, null);
        return GameArtworkReconciliationService.ArtworkWork.Remote(candidate, lane);
    }

    private static GameArtworkReconciliationService.ArtworkCandidate Candidate(string libraryId, string gameId, string system = "snes")
    {
        var key = ArtworkCatalogKey.Create(libraryId, gameId + ".sfc", "fingerprint", "boxart");
        var source = new GameThumbSource(new[] { "snes" }, gameId, "C:/roms/" + gameId + ".sfc", system, false);
        return new GameArtworkReconciliationService.ArtworkCandidate(gameId, system, GameThumbService.ThumbKind.Boxart, source, key, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static GameArtworkReconciliationService BuildService(string root) => BuildService(root, out _);

    private static GameArtworkReconciliationService BuildService(string root, out GameArtworkCatalog catalog)
    {
        var libraryManager = new FakeLibraryManager(new List<VirtualFolderInfo>());
        var games = new GamesService(libraryManager);
        catalog = new GameArtworkCatalog(Path.Combine(root, "catalog"));
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        return new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance);
    }

    private static async Task WaitForStateAsync(GameArtworkCatalog catalog, ArtworkCatalogKey key, ArtworkCatalogState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var entry = await catalog.GetAsync(key);
            if (entry != null && entry.State == state)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {key.StorageKey} to reach {state}");
    }

    // Real watchers (the production default), a real GamesService over a mutable folder list, and
    // a stubbed GameThumbService: reconciliation's catalog bookkeeping runs, artwork downloads do
    // not.
    private GameArtworkReconciliationService CreateWatchingService(
        List<VirtualFolderInfo> folders,
        out GameArtworkCatalog catalog)
    {
        catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        return new GameArtworkReconciliationService(
            new GamesService(new FakeLibraryManager(folders)),
            (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService)),
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null);
    }

    private static async Task WaitForWatchedRootsAsync(
        GameArtworkReconciliationService service,
        IReadOnlyList<string> expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<string> observed;
        do
        {
            observed = service.WatchedLibraryRootsForTests;
            if (observed.OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(expected.OrderBy(root => root, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"Timed out waiting for watched roots [{string.Join(", ", expected)}]; observed [{string.Join(", ", observed)}]");
    }
}
