using System.Runtime.CompilerServices;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class GameArtworkCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-artwork-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingEntry_RemainsDurableUntilFingerprintProviderOrExplicitRefreshChanges()
    {
        var catalog = new GameArtworkCatalog(_root);
        var key = Key("fingerprint-a");

        var missing = await catalog.MarkMissingAsync(key, "snes", "provider-v1");
        Assert.Equal(ArtworkCatalogState.Missing, missing.State);

        Assert.Equal(ArtworkCatalogState.Missing, (await catalog.GetOrCreateAsync(key, "snes", "provider-v1")).State);
        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.GetOrCreateAsync(key, "snes", "provider-v2")).State);
        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.GetOrCreateAsync(Key("fingerprint-b"), "snes", "provider-v2")).State);

        await catalog.MarkMissingAsync(Key("fingerprint-b"), "snes", "provider-v2");
        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.RequestRefreshAsync(Key("fingerprint-b"), "snes")).State);
    }

    [Fact]
    public async Task MetadataPending_UsesBoundedCadenceThenDefersUntilReopened()
    {
        var catalog = new GameArtworkCatalog(_root);
        var key = Key("metadata-pending");
        var started = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var firstDelay = TimeSpan.FromMinutes(5);
        var repeatDelay = TimeSpan.FromHours(3);
        var window = TimeSpan.FromHours(24);

        var first = await catalog.MarkMetadataPendingAsync(
            key, "snes", "provider-v1", started, firstDelay, repeatDelay, window);
        Assert.Equal(started + firstDelay, first);
        Assert.Equal(ArtworkCatalogState.Retryable, (await catalog.GetAsync(key))?.State);

        var secondAt = first!.Value;
        var second = await catalog.MarkMetadataPendingAsync(
            key, "snes", "provider-v1", secondAt, firstDelay, repeatDelay, window);
        Assert.Equal(secondAt + repeatDelay, second);

        var exhausted = await catalog.MarkMetadataPendingAsync(
            key, "snes", "provider-v1", started + window, firstDelay, repeatDelay, window);
        Assert.Null(exhausted);
        Assert.Equal(ArtworkCatalogState.MetadataDeferred, (await catalog.GetAsync(key))?.State);
        Assert.DoesNotContain(await catalog.GetStartupRecoveryWorkAsync(), item => item.Entry.Key == key);

        Assert.Equal(ArtworkCatalogState.Pending, (await catalog.GetOrCreateAsync(key, "snes", "provider-v2")).State);
    }

    [Theory]
    [InlineData("C:\\roms\\game.sfc")]
    [InlineData("../game.sfc")]
    [InlineData("/roms/game.sfc")]
    public void KeyCreation_RejectsRomPathsOutsideTheLibrary(string relativePath)
    {
        Assert.Throws<ArgumentException>(() =>
            ArtworkCatalogKey.Create("library-a", relativePath, "fingerprint", "boxart"));
    }

    [Fact]
    public async Task ReadyTransitions_PersistLocalPathsRevisionsAndSystemGeneration()
    {
        var catalog = new GameArtworkCatalog(_root);
        var key = Key("fingerprint-a");

        var original = await catalog.MarkOriginalReadyAsync(key, "snes", "C:/catalog/game.png");
        var thumbnail = await catalog.MarkThumbnailReadyAsync(key, "snes", "C:/catalog/game-thumb.jpg");
        var snapshot = await catalog.GetSystemAsync("library-a", "snes");

        Assert.Equal(ArtworkCatalogState.OriginalReady, original.State);
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, thumbnail.State);
        Assert.Equal("C:/catalog/game.png", thumbnail.OriginalPath);
        Assert.Equal("C:/catalog/game-thumb.jpg", thumbnail.ThumbnailPath);
        Assert.True(thumbnail.Revision > original.Revision);
        Assert.True(snapshot.Generation >= 2);

        // Writes are debounced, so durability is asserted against an explicit flush.
        await catalog.FlushAsync();
        var reloaded = new GameArtworkCatalog(_root);
        Assert.Equal(thumbnail, await reloaded.GetAsync(key));

        // "C:/catalog/game-thumb.jpg" round-trips through the catalog above, but it sits outside
        // any managed artwork root. IsManagedArtworkPath is the only runtime guard stopping a path
        // like this from being served to a client, so serving it must be refused too.
        var service = BuildReconciliationService(catalog);
        Assert.Null(service.ResolveLocalArtwork(thumbnail, thumbnail.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task RepairMissingArtifacts_ReopensOnlyEntriesWhoseCacheFilesWereDeleted()
    {
        var catalog = new GameArtworkCatalog(_root);
        var artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var missingOriginal = Path.Combine(artifacts, "boxart.png");
        var missingThumbnail = Path.Combine(artifacts, "boxart_thumb.jpg");
        var retainedOriginal = Path.Combine(artifacts, "snap.png");
        var removedThumbnail = Path.Combine(artifacts, "snap_thumb.jpg");
        var healthyOriginal = Path.Combine(artifacts, "title.png");
        var healthyThumbnail = Path.Combine(artifacts, "title_thumb.jpg");
        foreach (var path in new[] { missingOriginal, missingThumbnail, retainedOriginal, removedThumbnail, healthyOriginal, healthyThumbnail })
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }

        var boxart = ArtworkCatalogKey.Create("library-a", "SNES/Game.sfc", "fingerprint", "boxart");
        var snap = ArtworkCatalogKey.Create("library-a", "SNES/Game.sfc", "fingerprint", "snap");
        var title = ArtworkCatalogKey.Create("library-a", "SNES/Game.sfc", "fingerprint", "title");
        foreach (var key in new[] { boxart, snap, title })
        {
            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", "game");
        }

        await catalog.MarkOriginalReadyAsync(boxart, "snes", missingOriginal);
        await catalog.MarkThumbnailReadyAsync(boxart, "snes", missingThumbnail);
        await catalog.MarkOriginalReadyAsync(snap, "snes", retainedOriginal);
        var snapReady = await catalog.MarkThumbnailReadyAsync(snap, "snes", removedThumbnail);
        await catalog.MarkOriginalReadyAsync(title, "snes", healthyOriginal);
        await catalog.MarkThumbnailReadyAsync(title, "snes", healthyThumbnail);
        await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", ["game"]);
        var published = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        Assert.Single(published.PreviewGameIds);

        File.Delete(missingOriginal);
        File.Delete(missingThumbnail);
        File.Delete(removedThumbnail);

        Assert.Equal(2, await catalog.RepairMissingArtifactsAsync("library-a"));

        var repairedBoxart = await catalog.GetAsync(boxart);
        var repairedSnap = await catalog.GetAsync(snap);
        var unchangedTitle = await catalog.GetAsync(title);
        var repairedSystem = await catalog.GetSystemAsync("library-a", "snes");
        Assert.Equal(ArtworkCatalogState.Pending, repairedBoxart?.State);
        Assert.Null(repairedBoxart?.OriginalPath);
        Assert.Null(repairedBoxart?.ThumbnailPath);
        Assert.Equal(ArtworkCatalogState.OriginalReady, repairedSnap?.State);
        Assert.Equal(retainedOriginal, repairedSnap?.OriginalPath);
        Assert.Null(repairedSnap?.ThumbnailPath);
        Assert.True(repairedSnap?.Revision > snapReady.Revision);
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, unchangedTitle?.State);
        Assert.Empty(repairedSystem.PreviewGameIds);
        Assert.Null(repairedSystem.PreviewSelectionGeneration);

        var recovery = await catalog.GetStartupRecoveryWorkAsync();
        Assert.Contains(recovery, item => item.Entry.Key == boxart && item.Stage == ArtworkCatalogWorkStage.AcquireOriginal);
        Assert.Contains(recovery, item => item.Entry.Key == snap && item.Stage == ArtworkCatalogWorkStage.DeriveThumbnail);
        Assert.DoesNotContain(recovery, item => item.Entry.Key == title);
    }

    private static GameArtworkReconciliationService BuildReconciliationService(GameArtworkCatalog catalog)
    {
        var libraryManager = new FakeLibraryManager(new List<VirtualFolderInfo>());
        var games = new GamesService(libraryManager);
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        return new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance);
    }

    [Fact]
    public async Task PreviewSelection_IsStableForInventoryGenerationAndChangesWhenInventoryChanges()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g1", "g2", "g3", "g4", "g5", "g6" };
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            Array.Empty<ArtworkCatalogProjectionEntry>(),
            "provider-v1",
            new ArtworkCatalogSystemMetadata("snes", "Super Nintendo", "snes9x", candidates.Length));

        var discoveryFirst = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        foreach (var gameId in discoveryFirst.PreviewCandidateGameIds.Take(4))
        {
            var key = ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart");
            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", gameId);
            await catalog.MarkOriginalReadyAsync(key, "snes", $"C:/catalog/{gameId}.png");
        }

        var first = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        var discoveryRepeated = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates.Reverse());
        var repeated = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        foreach (var gameId in discoveryFirst.PreviewCandidateGameIds.Skip(4))
        {
            var key = ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart");
            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", gameId);
            await catalog.MarkOriginalReadyAsync(key, "snes", $"C:/catalog/{gameId}.png");
        }

        var discoveryChanged = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-b", candidates);
        var changed = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-b");

        Assert.Equal(discoveryFirst.PreviewCandidateGameIds, discoveryRepeated.PreviewCandidateGameIds);
        Assert.Equal(first.PreviewGameIds, repeated.PreviewGameIds);
        Assert.Equal(4, first.PreviewGameIds.Count);
        Assert.Equal("inventory-a", first.PreviewSelectionGeneration);
        Assert.Equal("inventory-b", changed.PreviewSelectionGeneration);
        Assert.True(changed.Generation > first.Generation);
        var metadata = Assert.Single(await catalog.GetSystemMetadataAsync("library-a"));
        Assert.Equal(("Super Nintendo", "snes9x", candidates.Length), (metadata.Name, metadata.Core, metadata.GameCount));
    }

    [Fact]
    public async Task PreviewSelection_NextUnresolvedCandidateBlocksPublicationUntilItBecomesTerminal()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g1", "g2", "g3", "g4", "g5" };
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        var ordered = discovery.PreviewCandidateGameIds;
        var keys = ordered.ToDictionary(
            gameId => gameId,
            gameId => ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart"),
            StringComparer.Ordinal);

        foreach (var gameId in ordered)
        {
            await catalog.GetOrCreateAsync(keys[gameId], "snes", "provider-v1", gameId);
        }

        foreach (var gameId in ordered.Skip(1).Take(4))
        {
            await catalog.MarkOriginalReadyAsync(keys[gameId], "snes", $"C:/catalog/{gameId}.png");
        }

        var blocked = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        Assert.Empty(blocked.PreviewGameIds);
        Assert.Null(blocked.PreviewSelectionGeneration);

        await catalog.MarkMissingAsync(keys[ordered[0]], "snes", "provider-v1");
        var complete = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        Assert.Equal(ordered.Skip(1).Take(4), complete.PreviewGameIds);
        Assert.Equal("inventory-a", complete.PreviewSelectionGeneration);
    }

    // A completion elsewhere in the system recomputes the selection while a reopened candidate is
    // still non-terminal. Publishing an empty list there would blank the panels mid-import.
    [Fact]
    public async Task PreviewSelection_KeepsPublishedPanelsWhileACandidateReopens()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g1", "g2", "g3", "g4", "g5" };
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        var ordered = discovery.PreviewCandidateGameIds;
        var keys = ordered.ToDictionary(
            gameId => gameId,
            gameId => ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart"),
            StringComparer.Ordinal);
        foreach (var gameId in ordered)
        {
            await catalog.GetOrCreateAsync(keys[gameId], "snes", "provider-v1", gameId);
            await catalog.MarkOriginalReadyAsync(keys[gameId], "snes", $"C:/catalog/{gameId}.png");
        }

        var completed = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        Assert.Equal(4, completed.PreviewGameIds.Count);

        await catalog.RequestRefreshAsync(keys[ordered[0]], "snes", ordered[0]);
        var reopened = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        Assert.Equal(completed.PreviewGameIds, reopened.PreviewGameIds);
        Assert.Null(reopened.PreviewSelectionGeneration);
        Assert.Equal(ordered[0], reopened.NextUnresolvedGameId);
    }

    // The resumable scan trusts its stored prefix. Removing a confirmed entry through the
    // single-entry path must invalidate that checkpoint, or the selection keeps publishing a game
    // whose catalog entry is gone.
    [Fact]
    public async Task PreviewSelection_RemovingAConfirmedEntryInvalidatesTheScanCheckpoint()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g1", "g2", "g3", "g4", "g5", "g6" };
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        var ordered = discovery.PreviewCandidateGameIds;
        var keys = ordered.ToDictionary(
            gameId => gameId,
            gameId => ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart"),
            StringComparer.Ordinal);
        foreach (var gameId in ordered)
        {
            await catalog.GetOrCreateAsync(keys[gameId], "snes", "provider-v1", gameId);
            await catalog.MarkOriginalReadyAsync(keys[gameId], "snes", $"C:/catalog/{gameId}.png");
        }

        var completed = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        var dropped = completed.PreviewGameIds[0];
        Assert.True(await catalog.RemoveAsync(keys[dropped], "snes"));

        var after = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        Assert.DoesNotContain(dropped, after.PreviewGameIds);

        await catalog.GetOrCreateAsync(keys[dropped], "snes", "provider-v1", dropped);
        await catalog.MarkOriginalReadyAsync(keys[dropped], "snes", $"C:/catalog/{dropped}.png");
        var rediscovered = await catalog.GetOrCreatePreviewDiscoveryAsync(
            "library-a",
            "snes",
            "inventory-a",
            candidates);
        var restored = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        Assert.Contains(dropped, rediscovered.PreviewCandidateGameIds);
        Assert.Contains(dropped, restored.PreviewGameIds);
    }

    [Fact]
    public async Task PreviewSelection_EmptyCompletionPersistsAndANewInventoryResetsIt()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g1", "g2", "g3" };
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);

        foreach (var gameId in discovery.PreviewCandidateGameIds)
        {
            var key = ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart");
            await catalog.GetOrCreateAsync(key, "snes", "provider-v1", gameId);
            await catalog.MarkMissingAsync(key, "snes", "provider-v1");
        }

        var complete = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        Assert.Empty(complete.PreviewGameIds);
        Assert.Equal("inventory-a", complete.PreviewSelectionGeneration);

        await catalog.FlushAsync();
        var reloaded = new GameArtworkCatalog(_root);
        var persisted = await reloaded.GetSystemAsync("library-a", "snes");
        Assert.Empty(persisted.PreviewGameIds);
        Assert.Equal("inventory-a", persisted.PreviewSelectionGeneration);

        var reset = await reloaded.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-b", candidates);
        Assert.Empty(reset.PreviewGameIds);
        Assert.Null(reset.PreviewSelectionGeneration);
    }

    [Fact]
    public async Task PreviewSelection_TraversesThousandsOfCachedTerminalCandidatesIteratively()
    {
        const int candidateCount = 5_000;
        var catalog = new GameArtworkCatalog(_root);
        var entries = Enumerable.Range(0, candidateCount)
            .Select(index => new ArtworkCatalogProjectionEntry(
                ArtworkCatalogKey.Create("library-a", $"SNES/Game-{index:D4}.sfc", $"fingerprint-{index:D4}", "boxart"),
                "snes",
                $"game-{index:D4}"))
            .ToArray();

        await catalog.SynchronizeProjectionAsync(
            "library-a",
            entries,
            "provider-v1",
            new ArtworkCatalogSystemMetadata("snes", "SNES", "snes9x", candidateCount));
        await catalog.GetOrCreatePreviewDiscoveryAsync(
            "library-a",
            "snes",
            "inventory-a",
            entries.Select(entry => entry.GameId));
        foreach (var entry in entries)
        {
            await catalog.MarkMissingAsync(entry.Key, "snes", "provider-v1");
        }

        var complete = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        Assert.Empty(complete.PreviewGameIds);
        Assert.Equal("inventory-a", complete.PreviewSelectionGeneration);
    }

    // Reconciliation calls RecomputePreviewSelectionAsync once per boxart completion, always
    // restarting the walk at index 0. On a sparse system (most games never get artwork) the
    // terminal prefix grows with every call, so a full reconciliation pass is O(N^2) lookups.
    // The scan must resume from where the last call proved the prefix terminal instead.
    [Fact]
    public async Task PreviewSelection_ResumesFromLastProvenIndexInsteadOfRescanningFromZero()
    {
        const int candidateCount = 2_000;
        var catalog = new GameArtworkCatalog(_root);
        var entries = Enumerable.Range(0, candidateCount)
            .Select(index => new ArtworkCatalogProjectionEntry(
                ArtworkCatalogKey.Create("library-a", $"SNES/Game-{index:D4}.sfc", $"fingerprint-{index:D4}", "boxart"),
                "snes",
                $"game-{index:D4}"))
            .ToArray();

        await catalog.SynchronizeProjectionAsync(
            "library-a",
            entries,
            "provider-v1",
            new ArtworkCatalogSystemMetadata("snes", "SNES", "snes9x", candidateCount));
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync(
            "library-a",
            "snes",
            "inventory-a",
            entries.Select(entry => entry.GameId));
        var keysByGameId = entries.ToDictionary(entry => entry.GameId, entry => entry.Key, StringComparer.Ordinal);

        // Resolved in the walk's own order (discovery permutes candidates by hash, not insertion
        // order), so the terminal prefix genuinely grows by one every call -- the pathological
        // pattern this fix targets. Mirrors the real call pattern: one boxart completion, then one
        // recompute, repeated for every candidate; almost all resolve Missing, matching a large
        // arcade set with poor provider coverage.
        foreach (var gameId in discovery.PreviewCandidateGameIds)
        {
            await catalog.MarkMissingAsync(keysByGameId[gameId], "snes", "provider-v1");
            await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        }

        // O(N) resumed scanning costs roughly one lookup per candidate per call; O(N^2) rescanning
        // from zero costs roughly N^2/2. A generous linear-ish bound still rejects the quadratic
        // behavior by three orders of magnitude at this N.
        Assert.True(
            catalog.PreviewScanLookupCount <= candidateCount * 4L,
            $"Expected roughly linear lookup cost (~{candidateCount * 4L}), but observed {catalog.PreviewScanLookupCount} lookups.");
    }

    // A candidate strictly before the resumed scan's stored index can still reopen (an explicit
    // refresh, a reopened metadata defer, a provider-version change). The resumed scan must not
    // treat a stale cached classification as authoritative once that happens.
    [Fact]
    public async Task PreviewSelection_ReopeningAConfirmedCandidateBeforeTheScanIndexIsPickedUpOnResume()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = new[] { "g0", "g1", "g2", "g3", "g4", "g5", "g6" };
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        var ordered = discovery.PreviewCandidateGameIds;
        var keys = ordered.ToDictionary(
            gameId => gameId,
            gameId => ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart"),
            StringComparer.Ordinal);

        foreach (var gameId in ordered)
        {
            await catalog.GetOrCreateAsync(keys[gameId], "snes", "provider-v1", gameId);
        }

        // ordered[0] is a durable miss; ordered[1..4] are confirmed -- the walk advances the scan
        // index past all five and publishes the first four confirmed candidates.
        await catalog.MarkMissingAsync(keys[ordered[0]], "snes", "provider-v1");
        foreach (var gameId in ordered.Skip(1).Take(4))
        {
            await catalog.MarkOriginalReadyAsync(keys[gameId], "snes", $"C:/catalog/{gameId}.png");
        }

        var initial = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        Assert.Equal(ordered.Skip(1).Take(4), initial.PreviewGameIds);

        // ordered[0] reopens and becomes confirmed too. In candidate order it now outranks the
        // fourth previously-confirmed candidate, so a correct from-scratch scan swaps it in.
        await catalog.RequestRefreshAsync(keys[ordered[0]], "snes", ordered[0]);
        await catalog.MarkOriginalReadyAsync(keys[ordered[0]], "snes", $"C:/catalog/{ordered[0]}.png");

        var resumed = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");

        var expected = new[] { ordered[0] }.Concat(ordered.Skip(1).Take(3)).ToArray();
        Assert.Equal(expected, resumed.PreviewGameIds);
    }

    // Same sparse-scan pattern as PreviewSelection_ResumesFromLastProvenIndexInsteadOfRescanningFromZero,
    // but comparing against a fresh catalog that only ever does one full scan from index 0 -- the
    // resumed scan's incremental result must match it exactly at every step, not just at the end.
    [Fact]
    public async Task PreviewSelection_ResumedScanMatchesAFullScanFromScratch()
    {
        var catalog = new GameArtworkCatalog(_root);
        var candidates = Enumerable.Range(0, 30).Select(index => $"game-{index:D2}").ToArray();
        var discovery = await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", candidates);
        var ordered = discovery.PreviewCandidateGameIds;
        var keys = ordered.ToDictionary(
            gameId => gameId,
            gameId => ArtworkCatalogKey.Create("library-a", $"SNES/{gameId}.sfc", $"fingerprint-{gameId}", "boxart"),
            StringComparer.Ordinal);

        foreach (var gameId in ordered)
        {
            await catalog.GetOrCreateAsync(keys[gameId], "snes", "provider-v1", gameId);
        }

        // Every fifth candidate confirms; the rest resolve missing. Resolve them one at a time,
        // recomputing (resumed) after each, and compare against an independent from-scratch scan.
        // An incomplete from-scratch walk keeps the previously published selection, exactly as
        // RecomputePreviewSelectionAsync does -- so the oracle tracks that same "last published"
        // state itself rather than assuming every step is complete.
        var expectedPublished = new List<string>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var gameId = ordered[index];
            if (index % 5 == 0)
            {
                await catalog.MarkOriginalReadyAsync(keys[gameId], "snes", $"C:/catalog/{gameId}.png");
            }
            else
            {
                await catalog.MarkMissingAsync(keys[gameId], "snes", "provider-v1");
            }

            var resumed = await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
            var (fromScratch, complete) = await ComputeFromScratchSelectionAsync(catalog, ordered);
            if (complete)
            {
                expectedPublished = fromScratch;
            }

            Assert.Equal(expectedPublished, resumed.PreviewGameIds);
        }
    }

    /// <summary>Independent oracle: an unindexed, always-from-zero walk of the current boxart states.</summary>
    private static async Task<(List<string> Confirmed, bool Complete)> ComputeFromScratchSelectionAsync(
        GameArtworkCatalog catalog,
        IReadOnlyList<string> ordered)
    {
        var confirmed = new List<string>(4);
        foreach (var gameId in ordered)
        {
            var entry = await catalog.GetByGameIdAsync("library-a", gameId, "boxart");
            var isConfirmed = entry is { State: ArtworkCatalogState.ThumbnailReady } ||
                (entry is { State: ArtworkCatalogState.OriginalReady } && !string.IsNullOrWhiteSpace(entry.OriginalPath));
            if (isConfirmed)
            {
                confirmed.Add(gameId);
                if (confirmed.Count == 4)
                {
                    return (confirmed, true);
                }

                continue;
            }

            var isTerminalSkip = entry is { State: ArtworkCatalogState.Missing or ArtworkCatalogState.MetadataDeferred };
            if (isTerminalSkip)
            {
                continue;
            }

            return (confirmed, false);
        }

        return (confirmed, true);
    }

    [Fact]
    public async Task StartupRecovery_RebuildsPendingRetryAndThumbnailWorkWithoutMissingEntries()
    {
        var catalog = new GameArtworkCatalog(_root);
        var pending = Key("pending");
        var retry = Key("retry");
        var original = Key("original");
        var missing = Key("missing");
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(3);

        await catalog.GetOrCreateAsync(pending, "snes", "provider-v1");
        await catalog.MarkRetryableAsync(retry, "snes", ArtworkCatalogWorkStage.AcquireOriginal, retryAt);
        await catalog.MarkOriginalReadyAsync(original, "snes", "C:/catalog/original.png");
        await catalog.MarkMissingAsync(missing, "snes", "provider-v1");

        // Writes are debounced; a clean shutdown flushes, which is what this reload stands in for.
        await catalog.FlushAsync();
        var recovered = await new GameArtworkCatalog(_root).GetStartupRecoveryWorkAsync();

        Assert.Contains(recovered, item => item.Entry.Key == pending && item.Stage == ArtworkCatalogWorkStage.AcquireOriginal);
        Assert.Contains(recovered, item => item.Entry.Key == retry && item.NotBeforeUtc == retryAt);
        Assert.Contains(recovered, item => item.Entry.Key == original && item.Stage == ArtworkCatalogWorkStage.DeriveThumbnail);
        Assert.DoesNotContain(recovered, item => item.Entry.Key == missing);
    }

    [Fact]
    public async Task StartupRecovery_ReadsAtomicBackupWhenPrimaryCatalogIsDamaged()
    {
        var catalog = new GameArtworkCatalog(_root);
        var key = Key("pending");
        await catalog.GetOrCreateAsync(key, "snes", "provider-v1");

        var path = catalog.GetLibraryPath("library-a");
        File.Copy(path, AtomicFile.BackupPath(path), overwrite: true);
        await File.WriteAllTextAsync(path, "not-json");

        var recovered = await new GameArtworkCatalog(_root).GetStartupRecoveryWorkAsync();

        Assert.Contains(recovered, item => item.Entry.Key == key);
    }

    [Fact]
    public async Task ProjectionAndPrune_KeepCurrentGameIdsAndInvalidateStalePreviewState()
    {
        var catalog = new GameArtworkCatalog(_root);
        var retained = ArtworkCatalogKey.Create("library-a", "SNES\\Retained.sfc", "retained", "boxart");
        var removed = ArtworkCatalogKey.Create("library-a", "SNES\\Removed.sfc", "removed", "boxart");

        await catalog.GetOrCreateAsync(retained, "snes", "provider-v1", "current-game");
        await catalog.GetOrCreateAsync(removed, "snes", "provider-v1", "removed-game");
        await catalog.MarkOriginalReadyAsync(retained, "snes", "C:/catalog/retained.png");
        await catalog.MarkOriginalReadyAsync(removed, "snes", "C:/catalog/removed.png");
        await catalog.GetOrCreatePreviewDiscoveryAsync("library-a", "snes", "inventory-a", new[] { "current-game", "removed-game" });
        await catalog.RecomputePreviewSelectionAsync("library-a", "snes", "inventory-a");
        var before = await catalog.GetSystemProjectionAsync("library-a", "snes");

        await catalog.PruneAsync("library-a", new[] { retained });
        var after = await catalog.GetSystemProjectionAsync("library-a", "snes");

        Assert.NotNull(before);
        Assert.NotNull(after);
        var beforeProjection = before!;
        var afterProjection = after!;
        Assert.True(afterProjection.System.Generation > beforeProjection.System.Generation);
        Assert.Equal(new[] { "current-game" }, afterProjection.Entries.Select(entry => entry.GameId).Distinct());
        Assert.Equal(new[] { "current-game" }, afterProjection.System.PreviewGameIds);
        Assert.DoesNotContain(afterProjection.Entries, entry => entry.Key == removed);
    }

    [Fact]
    public async Task SystemProjection_AtFiveThousandGames_IsBoundedToItsPersistedSystem()
    {
        var catalog = new GameArtworkCatalog(_root);
        var titles = Enumerable.Range(0, 5_000)
            .Select(index => new ArtworkCatalogProjectionEntry(
                ArtworkCatalogKey.Create("library-a", $"SNES/Game-{index:D4}.sfc", $"fingerprint-{index:D4}", "boxart"),
                "snes",
                $"snes-game-{index:D4}"))
            .ToArray();

        await catalog.SynchronizeProjectionAsync(
            "library-a",
            titles,
            "provider-v1",
            new ArtworkCatalogSystemMetadata("snes", "SNES", "snes9x", titles.Length));
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            new[]
            {
                new ArtworkCatalogProjectionEntry(
                    ArtworkCatalogKey.Create("library-a", "NES/Other.nes", "other", "boxart"),
                    "nes",
                    "nes-other"),
            },
            "provider-v1",
            new ArtworkCatalogSystemMetadata("nes", "NES", "fceumm", 1));
        await catalog.SynchronizeProjectionAsync(
            "library-b",
            new[]
            {
                new ArtworkCatalogProjectionEntry(
                    ArtworkCatalogKey.Create("library-b", "SNES/Outside.sfc", "outside", "boxart"),
                    "snes",
                    "outside-library"),
            },
            "provider-v1");

        var projection = await catalog.GetSystemProjectionAsync("library-a", "snes");

        Assert.NotNull(projection);
        var system = projection!;
        Assert.Equal(5_000, system.Entries.Count);
        Assert.All(system.Entries, entry => Assert.Equal("snes", entry.SystemId));
        Assert.DoesNotContain(system.Entries, entry => entry.GameId == "nes-other" || entry.GameId == "outside-library");
        Assert.Equal(5_000, system.Entries.Select(entry => entry.GameId).Distinct().Count());
    }

    [Fact]
    public async Task GetSystemProjectionAsync_ResolvesRegardlessOfSystemIdCasing()
    {
        var catalog = new GameArtworkCatalog(_root);
        var key = ArtworkCatalogKey.Create("library-a", "SNES/Game.sfc", "fingerprint-case", "boxart");

        // Reconciliation observed the on-disk directory as "SNES" (mixed case).
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            new[] { new ArtworkCatalogProjectionEntry(key, "SNES", "snes-game") },
            "provider-v1",
            new ArtworkCatalogSystemMetadata("SNES", "SNES", "snes9x", 1));

        // GamesService compares system ids case-insensitively, so a lowercase request must resolve too.
        var projection = await catalog.GetSystemProjectionAsync("library-a", "snes");

        Assert.NotNull(projection);
        Assert.Single(projection!.Entries);
        Assert.Equal("snes-game", projection.Entries[0].GameId);
    }

    [Fact]
    public async Task LoadingLibrary_MergesMixedCaseSystemKeysKeepingTheNewerGeneration()
    {
        var catalog = new GameArtworkCatalog(_root);
        var path = catalog.GetLibraryPath("library-a");
        Directory.CreateDirectory(_root);

        // Simulates a catalog persisted before SystemId normalization existed: two systems that
        // differ only by case, which should now collapse into one on load.
        var legacyJson = """
        {
          "schemaVersion": 1,
          "libraryId": "library-a",
          "entries": {},
          "systems": {
            "SNES": { "systemId": "SNES", "name": "Super Nintendo (stale)", "core": "snes9x", "gameCount": 10, "generation": 1, "inventoryGeneration": null, "previewGameIds": [], "previewCandidateGameIds": [] },
            "snes": { "systemId": "snes", "name": "Super Nintendo", "core": "snes9x", "gameCount": 12, "generation": 5, "inventoryGeneration": null, "previewGameIds": [], "previewCandidateGameIds": [] }
          }
        }
        """;
        await File.WriteAllTextAsync(path, legacyJson);

        var reloaded = new GameArtworkCatalog(_root);
        var snapshot = await reloaded.GetSystemAsync("library-a", "snes");
        var metadata = await reloaded.GetSystemMetadataAsync("library-a");

        Assert.Equal(5, snapshot.Generation);
        Assert.Null(snapshot.PreviewSelectionGeneration);
        Assert.Single(metadata);
        Assert.Equal("Super Nintendo", metadata[0].Name);
    }

    [Fact]
    public async Task BulkTransitions_CoalesceIntoAFewWholeDocumentWrites()
    {
        var catalog = new GameArtworkCatalog(_root);

        for (var index = 0; index < 200; index++)
        {
            await catalog.MarkOriginalReadyAsync(
                ArtworkCatalogKey.Create("library-a", $"SNES/Game-{index:D3}.sfc", $"fingerprint-{index:D3}", "boxart"),
                "snes",
                $"C:/catalog/game-{index:D3}.png");
        }

        await catalog.FlushAsync();

        // One write per transition is the defect: 200 full serializations of a document that grows
        // with every entry. Everything but the debounce boundaries must coalesce.
        Assert.InRange(catalog.DocumentWriteCount, 1, 10);

        var reloaded = new GameArtworkCatalog(_root);
        var recovered = await reloaded.GetStartupRecoveryWorkAsync();
        Assert.Equal(200, recovered.Count(item => item.Stage == ArtworkCatalogWorkStage.DeriveThumbnail));
    }

    [Fact]
    public async Task DebouncedTransition_ReachesDiskWithoutAnExplicitFlush()
    {
        var catalog = new GameArtworkCatalog(_root, persistDebounce: TimeSpan.FromMilliseconds(50));
        var key = Key("fingerprint-a");

        await catalog.MarkOriginalReadyAsync(key, "snes", "C:/catalog/game.png");
        await catalog.MarkThumbnailReadyAsync(key, "snes", "C:/catalog/game-thumb.jpg");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        ArtworkCatalogEntry? persisted = null;
        while (DateTime.UtcNow < deadline)
        {
            persisted = await new GameArtworkCatalog(_root).GetAsync(key);
            if (persisted?.State == ArtworkCatalogState.ThumbnailReady)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.Equal(ArtworkCatalogState.ThumbnailReady, persisted?.State);
    }

    [Fact]
    public async Task Prune_IsDurableBeforeItsCallerDeletesTheOrphanedArtwork()
    {
        var catalog = new GameArtworkCatalog(_root);
        var retained = ArtworkCatalogKey.Create("library-a", "SNES/Retained.sfc", "retained", "boxart");
        var removed = ArtworkCatalogKey.Create("library-a", "SNES/Removed.sfc", "removed", "boxart");
        await catalog.GetOrCreateAsync(retained, "snes", "provider-v1", "retained-game");
        await catalog.MarkOriginalReadyAsync(removed, "snes", "C:/catalog/removed.png");

        var orphaned = await catalog.PruneAsync("library-a", new[] { retained });

        // The caller deletes these files as soon as PruneAsync returns, so a crash before the
        // catalog reached disk would leave an entry pointing at artwork that no longer exists.
        Assert.Contains("C:/catalog/removed.png", orphaned);
        Assert.Null(await new GameArtworkCatalog(_root).GetAsync(removed));
    }

    [Fact]
    public async Task GameIdIndex_AgreesWithALinearScanAcrossEveryMutationPathAndAReload()
    {
        var catalog = new GameArtworkCatalog(_root);
        var boxart = ArtworkCatalogKey.Create("library-a", "SNES/Mario.sfc", "fingerprint-a", "boxart");
        var snap = ArtworkCatalogKey.Create("library-a", "SNES/Mario.sfc", "fingerprint-a", "snap");
        var other = ArtworkCatalogKey.Create("library-a", "SNES/Zelda.sfc", "fingerprint-z", "boxart");
        var replaced = ArtworkCatalogKey.Create("library-a", "SNES/Zelda.sfc", "fingerprint-z2", "boxart");

        // Every path that can touch the entry collection, in the order a real library hits them.
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            new[]
            {
                new ArtworkCatalogProjectionEntry(boxart, "snes", "mario"),
                new ArtworkCatalogProjectionEntry(snap, "snes", "mario"),
                new ArtworkCatalogProjectionEntry(other, "snes", "zelda"),
            },
            "provider-v1",
            new ArtworkCatalogSystemMetadata("snes", "SNES", "snes9x", 2));
        await AssertIndexMatchesScanAsync(catalog, "mario", "zelda");

        await catalog.MarkOriginalReadyAsync(boxart, "snes", "C:/catalog/mario.png");
        await catalog.MarkThumbnailReadyAsync(boxart, "snes", "C:/catalog/mario-thumb.jpg");
        await catalog.RequestRefreshAsync(snap, "snes", "mario");
        await AssertIndexMatchesScanAsync(catalog, "mario", "zelda");

        // A game id is briefly shared by two fingerprints: the replacement is projected before the
        // reconciliation that produced it prunes the ROM it replaced.
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            new[] { new ArtworkCatalogProjectionEntry(replaced, "snes", "zelda") },
            "provider-v1");
        await AssertIndexMatchesScanAsync(catalog, "mario", "zelda");

        await catalog.PruneAsync("library-a", new[] { boxart, snap, replaced });
        await AssertIndexMatchesScanAsync(catalog, "mario", "zelda");

        // GetOrCreateAsync evicts the old fingerprint's entry for the same ROM path and role.
        await catalog.GetOrCreateAsync(
            ArtworkCatalogKey.Create("library-a", "SNES/Mario.sfc", "fingerprint-b", "boxart"),
            "snes",
            "provider-v1",
            "mario");
        Assert.True(await catalog.RemoveAsync(replaced, "snes"));
        await AssertIndexMatchesScanAsync(catalog, "mario", "zelda");

        // A re-projected game id is the mutation that can return a *wrong* entry rather than none,
        // so the old id must stop resolving the moment the entry carries the new one.
        await catalog.SynchronizeProjectionAsync(
            "library-a",
            new[] { new ArtworkCatalogProjectionEntry(snap, "snes", "mario-renamed") },
            "provider-v1");
        Assert.Null(await catalog.GetByGameIdAsync("library-a", "mario", "snap"));
        Assert.NotNull(await catalog.GetByGameIdAsync("library-a", "mario-renamed", "snap"));
        await AssertIndexMatchesScanAsync(catalog, "mario", "mario-renamed", "zelda");

        // The index is derived state rebuilt on load, so it must survive a restart too.
        await catalog.FlushAsync();
        await AssertIndexMatchesScanAsync(new GameArtworkCatalog(_root), "mario", "mario-renamed", "zelda");
    }

    /// <summary>
    /// Compares the by-game-id index against the projection, which is still an unindexed pass over
    /// every entry and therefore an independent oracle for what a lookup must return.
    /// </summary>
    private static async Task AssertIndexMatchesScanAsync(GameArtworkCatalog catalog, params string[] gameIds)
    {
        var projection = await catalog.GetSystemProjectionAsync("library-a", "snes");
        Assert.NotNull(projection);
        foreach (var gameId in gameIds)
        {
            foreach (var role in new[] { "boxart", "snap", "title" })
            {
                var scanned = projection!.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.GameId, gameId, StringComparison.Ordinal) &&
                    string.Equals(entry.Key.Role, role, StringComparison.Ordinal));
                Assert.Equal(scanned, await catalog.GetByGameIdAsync("library-a", gameId, role));
            }
        }
    }

    [Fact]
    public async Task GetOrCreateAsync_SupersedesTheEntryForAChangedFingerprintAtTheSamePathAndRole()
    {
        var catalog = new GameArtworkCatalog(_root);
        var libraryId = "library-a";
        var path = "SNES/Mario.sfc";
        var role = "boxart";
        var oldKey = ArtworkCatalogKey.Create(libraryId, path, "fingerprint-a", role);
        var newKey = ArtworkCatalogKey.Create(libraryId, path, "fingerprint-b", role);

        await catalog.MarkOriginalReadyAsync(oldKey, "snes", "C:/catalog/mario.png");

        var created = await catalog.GetOrCreateAsync(newKey, "snes", "provider-v1");

        Assert.Equal(ArtworkCatalogState.Pending, created.State);
        Assert.Null(await catalog.GetAsync(oldKey));
        Assert.NotNull(await catalog.GetAsync(newKey));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ArtworkCatalogKey Key(string fingerprint) =>
        ArtworkCatalogKey.Create("library-a", "SNES\\Super Mario World.sfc", fingerprint, "boxart");
}
