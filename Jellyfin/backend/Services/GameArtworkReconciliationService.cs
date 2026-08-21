using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Owns all non-request-path retro artwork work. Jellyfin does not index raw ROM files as media
/// items consistently, so reconciliation is triggered by plugin startup (once) and by every
/// completion of Jellyfin's built-in "RefreshLibrary" scheduled task (<see cref="ITaskManager.TaskCompleted"/>),
/// which picks up filesystem-only ROM changes whenever a library scan runs. See the comment in
/// <see cref="StartAsync"/> for why this subscription is safe and what it deliberately does not do.
/// </summary>
public sealed class GameArtworkReconciliationService : IHostedService
{
    // v2 repairs false terminal misses recorded while a cold RDB index was still downloading.
    private const string ProviderVersion = "libretro-thumbnails-v2";
    private static readonly GameThumbService.ThumbKind[] ArtworkKinds =
    [
        GameThumbService.ThumbKind.Boxart,
        GameThumbService.ThumbKind.Snap,
        GameThumbService.ThumbKind.Title,
    ];
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MetadataFirstRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MetadataRepeatRetryDelay = TimeSpan.FromHours(3);
    private static readonly TimeSpan MetadataRetryWindow = TimeSpan.FromHours(24);
    private const int MaxQueuedRemoteWork = 20_000;
    private const int MaxQueuedThumbnailWork = 20_000;
    private readonly GamesService _games;
    private readonly GameThumbService _thumbs;
    private readonly GameArtworkCatalog _catalog;
    private readonly ILogger<GameArtworkReconciliationService> _logger;
    private readonly ITaskManager? _taskManager;
    private readonly ArtworkWorkScheduler _remote = new();
    private readonly ConcurrentQueue<ArtworkWork> _thumbnail = new();
    private readonly SemaphoreSlim _thumbnailAvailable = new(0);
    private readonly object _remoteQueueGate = new();
    private readonly Dictionary<string, QueuedRemoteWork> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _thumbnailQueued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PreviewPlan> _previewPlans = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task[] _remoteWorkers = Array.Empty<Task>();
    private Task? _thumbnailWorker;
    private bool _startupRecoveryDone;

    /// <param name="games">Resolves game libraries/systems/games from the filesystem.</param>
    /// <param name="thumbs">Looks up and derives artwork thumbnails.</param>
    /// <param name="catalog">Persisted artwork catalog state.</param>
    /// <param name="logger">Logger for this service.</param>
    /// <param name="taskManager">
    /// Jellyfin's DI-provided task manager. Used to subscribe to <see cref="ITaskManager.TaskCompleted"/>
    /// so a completed "RefreshLibrary" scan triggers reconciliation. Nullable/optional only because unit
    /// tests construct this service directly without standing up a real task manager; production always
    /// resolves a real instance through DI.
    /// </param>
    public GameArtworkReconciliationService(
        GamesService games,
        GameThumbService thumbs,
        GameArtworkCatalog catalog,
        ILogger<GameArtworkReconciliationService> logger,
        ITaskManager? taskManager = null)
    {
        _games = games;
        _thumbs = thumbs;
        _catalog = catalog;
        _logger = logger;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Explicit phase-4 controller hook. It resets durable state, then queues remote work; the
    /// controller never performs a provider lookup itself.
    /// </summary>
    public async Task<bool> RequestRefreshAsync(
        string libraryId,
        string gameId,
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = TryCreateCandidate(libraryId, gameId, ParseKind(kind));
        if (candidate == null)
        {
            return false;
        }

        await _catalog.RequestRefreshAsync(candidate.Key, candidate.SystemId, candidate.GameId, cancellationToken).ConfigureAwait(false);
        QueueRemote(candidate, ArtworkLane.Interactive, null, null);
        return true;
    }

    /// <summary>
    /// Priority-hint phase-4 controller hook. This promotes cataloged work only; an unknown game
    /// remains unknown, so passive manifest reads cannot create a background download.
    /// </summary>
    public Task<bool> PromoteAsync(
        string libraryId,
        string gameId,
        string? kind = null,
        CancellationToken cancellationToken = default) =>
        PromoteAsync(libraryId, gameId, kind, null, cancellationToken);

    /// <summary>
    /// Batch-friendly overload. A caller promoting many games in one request resolves the library
    /// once with <see cref="CreateGameLookup"/> and passes the snapshot here, so a 128-item x
    /// 3-role priority request costs one filesystem enumeration instead of 384. Passing null keeps
    /// the single-shot behavior of the overload above.
    /// </summary>
    public async Task<bool> PromoteAsync(
        string libraryId,
        string gameId,
        string? kind,
        GameLookupSnapshot? lookup,
        CancellationToken cancellationToken = default)
    {
        var candidate = TryCreateCandidate(libraryId, gameId, ParseKind(kind), lookup);
        if (candidate == null)
        {
            return false;
        }

        var entry = await _catalog.GetAsync(candidate.Key, cancellationToken).ConfigureAwait(false);
        if (entry == null)
        {
            return false;
        }

        if (entry.State == ArtworkCatalogState.OriginalReady && !string.IsNullOrWhiteSpace(entry.OriginalPath))
        {
            QueueThumbnail(candidate with { OriginalPath = entry.OriginalPath }, entry.RetryAfterUtc);
        }
        else if (entry.State is ArtworkCatalogState.Pending or ArtworkCatalogState.Retryable)
        {
            QueueRemote(candidate, ArtworkLane.Interactive, null, entry.RetryAfterUtc);
        }

        return true;
    }

    /// <summary>
    /// Mutation endpoints use this current filesystem-backed check before changing queue state.
    /// Read-only manifests and asset routes deliberately use the persisted projection instead.
    /// </summary>
    public bool IsCurrentGameMember(string libraryId, string gameId) =>
        IsCurrentGameMember(libraryId, gameId, null);

    /// <summary>
    /// Batch-friendly overload of <see cref="IsCurrentGameMember(string, string)"/>. The decision is
    /// identical -- a snapshot holds exactly what the per-call enumeration would have returned --
    /// but validating a whole priority batch costs one library walk instead of one per item.
    /// </summary>
    public bool IsCurrentGameMember(string libraryId, string gameId, GameLookupSnapshot? lookup) =>
        TryCreateCandidate(libraryId, gameId, GameThumbService.ThumbKind.Boxart, lookup) != null;

    /// <summary>
    /// Resolves the library and its current game inventory once, for callers that then check
    /// membership or promote many games in a single request. This is deliberately a point-in-time
    /// snapshot: a game added to disk after it is taken is not visible to the batch, which matches
    /// the pre-existing per-call behavior closely enough that no request can observe the difference
    /// (each per-call walk was equally stale by the time the next one ran) while removing the
    /// O(items x library) cost that made the priority endpoint an authenticated DoS lever.
    /// </summary>
    public GameLookupSnapshot CreateGameLookup(string libraryId)
    {
        var library = _games.GetGameLibraries().FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (library == null)
        {
            // GetGames would return nothing for an unknown library, so skip the walk entirely.
            return new GameLookupSnapshot(libraryId, null, new Dictionary<string, GameSummary>(StringComparer.Ordinal));
        }

        var games = new Dictionary<string, GameSummary>(StringComparer.Ordinal);
        foreach (var game in _games.GetGames(libraryId, null))
        {
            // TryAdd, not the indexer: the replaced FirstOrDefault took the first match.
            games.TryAdd(game.Id, game);
        }

        return new GameLookupSnapshot(libraryId, library, games);
    }

    /// <summary>
    /// Returns the current filesystem-backed system inventory for browse requests. Artwork
    /// reconciliation is intentionally asynchronous, so the catalog must never gate whether a
    /// system or game is visible to clients during startup, recovery, or a catalog rebuild.
    /// </summary>
    public Task<IReadOnlyList<GameSystem>> GetSystemsAsync(string libraryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_games.GetSystems(libraryId));
    }

    /// <summary>
    /// Reads one system's cataloged artwork without creating entries or scheduling provider work.
    /// Controllers use this projection instead of reading the catalog's persisted JSON directly.
    /// </summary>
    public async Task<GameArtworkSystemReadResult?> GetSystemArtworkAsync(
        string libraryId,
        string systemId,
        CancellationToken cancellationToken = default)
    {
        var projection = await _catalog.GetSystemProjectionAsync(libraryId, systemId, cancellationToken).ConfigureAwait(false);
        return projection == null ? null : ToReadResult(projection);
    }

    /// <summary>
    /// Reads several systems' cataloged artwork in one catalog pass, keyed by the system id the
    /// caller supplied. The systems route needs all of them for a single page render, and resolving
    /// them one at a time re-walked the whole catalog per system.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GameArtworkSystemReadResult>> GetSystemArtworkBatchAsync(
        string libraryId,
        IEnumerable<string> systemIds,
        CancellationToken cancellationToken = default)
    {
        var requested = systemIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        var projections = await _catalog.GetSystemProjectionsAsync(libraryId, requested, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, GameArtworkSystemReadResult>(StringComparer.Ordinal);
        foreach (var (systemId, projection) in projections)
        {
            results.Add(systemId, ToReadResult(projection));
        }

        return results;
    }

    private static GameArtworkSystemReadResult ToReadResult(ArtworkCatalogSystemProjection projection)
    {
        var entries = projection.Entries
            .Select(entry => new GameArtworkReadEntry(entry.GameId, entry.Key.Role, entry))
            .ToArray();
        var gameIds = entries.Select(entry => entry.GameId).Distinct(StringComparer.Ordinal).ToArray();
        return new GameArtworkSystemReadResult(projection.System, gameIds, entries);
    }

    /// <summary>Reads one cataloged game-role entry without scheduling provider or encoder work.</summary>
    public async Task<GameArtworkReadEntry?> GetArtworkAsync(
        string libraryId,
        string gameId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseKind(role, out var kind))
        {
            return null;
        }

        var entry = await _catalog.GetByGameIdAsync(libraryId, gameId, KindName(kind), cancellationToken).ConfigureAwait(false);
        return entry == null ? null : new GameArtworkReadEntry(gameId, KindName(kind), entry);
    }

    /// <summary>
    /// Resolves a versioned artifact to the exact locally cataloged original or thumbnail. It
    /// never asks a provider and rejects persisted paths outside the plugin's artwork cache.
    /// </summary>
    public async Task<GameArtworkLocalAsset?> GetLocalArtworkAsync(
        string libraryId,
        string gameId,
        string role,
        string revision,
        bool preferOriginal = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseKind(role, out _))
        {
            return null;
        }

        var artwork = await GetArtworkAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false);
        return artwork == null ? null : ResolveLocalArtwork(artwork.Entry, revision, preferOriginal);
    }

    /// <summary>
    /// Resolves an artifact from an entry the caller has already read. Request handlers that
    /// inspect the entry's state before serving it use this rather than
    /// <see cref="GetLocalArtworkAsync"/>, so one request costs one catalog lookup instead of two.
    /// </summary>
    public GameArtworkLocalAsset? ResolveLocalArtwork(
        ArtworkCatalogEntry entry,
        string revision,
        bool preferOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(revision, entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return null;
        }

        var isThumbnail = !preferOriginal && entry.State == ArtworkCatalogState.ThumbnailReady &&
            !string.IsNullOrWhiteSpace(entry.ThumbnailPath) &&
            !string.Equals(entry.ThumbnailPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase);
        var path = isThumbnail ? entry.ThumbnailPath : entry.OriginalPath;
        if (entry.State is not (ArtworkCatalogState.OriginalReady or ArtworkCatalogState.ThumbnailReady) ||
            string.IsNullOrWhiteSpace(path) ||
            !IsManagedArtworkPath(path) ||
            !File.Exists(path))
        {
            return null;
        }

        return new GameArtworkLocalAsset(entry, path, GetContentType(path), isThumbnail);
    }

    /// <summary>
    /// Test-only seam: enqueues a candidate directly into the remote-work queue, bypassing the
    /// reconciliation scan. Production code only ever reaches <see cref="QueueRemote"/> through
    /// <see cref="RequestRefreshAsync"/>, one of the <c>PromoteAsync</c> overloads, or
    /// <see cref="ReconcileAsync"/>.
    /// </summary>
    internal void EnqueueRemoteForTest(ArtworkCandidate candidate) => QueueRemote(candidate, ArtworkLane.Interactive, null, null);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stop.Token;
        // Do not call ILibraryManager.AddParts here, and do not implement ILibraryPostScanTask to
        // register a post-scan hook through it. AddParts assigns wholesale rather than appending
        // (see Emby.Server.Implementations.Library.LibraryManager.AddParts) -- Jellyfin calls it
        // exactly once at startup with every discovered resolver/rule/comparer export, and a plugin
        // calling it again afterwards replaces that with only what the plugin itself passed in,
        // wiping every other library's ability to resolve files into items. Reconciliation is
        // therefore driven by the startup queue below plus a subscription to
        // ITaskManager.TaskCompleted (see OnTaskCompleted): that is a plain event handler `+=` on a
        // DI singleton, not a library-manager mutation, so it carries none of the AddParts risk.
        //
        // Known gap: the web UI's per-library "Scan library files" action calls
        // POST /Items/{id}/Refresh, which does not run through the scheduled-task system and so does
        // not raise TaskCompleted. Closing that gap would need a separate, debounced
        // ItemAdded/ItemRemoved trigger; that is not implemented here.
        _remoteWorkers = Enumerable.Range(0, 2).Select(_ => Task.Run(() => RemoteWorkerAsync(token), token)).ToArray();
        _thumbnailWorker = Task.Run(() => ThumbnailWorkerAsync(token), token);
        _thumbs.MetadataIndexAvailable += OnMetadataIndexAvailable;
        if (_taskManager != null)
        {
            _taskManager.TaskCompleted += OnTaskCompleted;
        }
        else
        {
            // Only unit tests construct this service without a task manager. If it ever happens in
            // production the plugin still works, but artwork stops tracking the library after the
            // startup pass -- a silent, gradual staleness that is very hard to diagnose from the
            // symptoms, so say so loudly here.
            _logger.LogWarning(
                "No task manager was supplied to game artwork reconciliation; it will run once at startup and will not react to library scans");
        }

        QueueReconciliation("plugin startup");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _thumbs.MetadataIndexAvailable -= OnMetadataIndexAvailable;
        if (_taskManager != null)
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }

        if (_stop == null)
        {
            return;
        }

        _stop.Cancel();
        _remote.Available.Release(_remoteWorkers.Length);
        _thumbnailAvailable.Release();
        var workers = _remoteWorkers.Concat(new[] { _thumbnailWorker }.OfType<Task>());
        try
        {
            await Task.WhenAll(workers).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: _stop.Cancel() above is exactly how these workers are asked to exit. Each
            // worker loop now catches its own cancellation and returns cleanly, but this stays as a
            // safety net for the WaitAsync(cancellationToken) call itself.
        }
        finally
        {
            _stop.Dispose();
            _stop = null;

            // The workers have stopped, so nothing can dirty the catalog after this point. Catalog
            // writes are debounced; paying one write per library here is what keeps a clean
            // shutdown from discarding the last window of state transitions.
            try
            {
                await _catalog.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Shutdown must not fault: the lost state is re-derivable by startup recovery.
                _logger.LogWarning(ex, "Flushing the game artwork catalog during shutdown failed");
            }
        }
    }

    /// <summary>
    /// Fires on Jellyfin's scheduled-task thread whenever any scheduled task finishes. Must stay
    /// cheap and never throw: it only filters for the built-in "RefreshLibrary" task and hands off
    /// to <see cref="QueueReconciliation"/>, which does its work on a separate background task.
    /// </summary>
    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Result?.Key, "RefreshLibrary", StringComparison.Ordinal))
        {
            QueueReconciliation("library scan completed");
        }
    }

    private void OnMetadataIndexAvailable(string platform)
    {
        _logger.LogDebug("Metadata index for {Platform} is ready; reopening deferred artwork work", platform);
        QueueReconciliation("metadata index ready", reopenMetadataDeferred: true);
    }

    private void QueueReconciliation(string reason, bool reopenMetadataDeferred = false)
    {
        var token = _stop?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(token, reopenMetadataDeferred).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Game artwork reconciliation triggered by {Reason} failed", reason);
            }
        }, token);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken, bool reopenMetadataDeferred)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // GetStartupRecoveryWorkAsync re-reads and re-parses the entire catalog (25-35 MB at
            // 20k ROMs), so it only makes sense to pay for once, on the first (startup) pass. Later
            // passes -- now triggered by RefreshLibrary completions instead of a timer -- rely on
            // the ordinary per-candidate state already tracked in the catalog.
            IReadOnlyList<ArtworkCatalogWorkItem> recovery = Array.Empty<ArtworkCatalogWorkItem>();
            if (!_startupRecoveryDone)
            {
                recovery = await _catalog.GetStartupRecoveryWorkAsync(cancellationToken).ConfigureAwait(false);

                // Latched only after the read succeeds. Latching first would let one failed or
                // cancelled startup pass strand the recoverable jobs until the next process start,
                // which the timer this replaced used to paper over by simply trying again.
                _startupRecoveryDone = true;
                if (recovery.Count > 0)
                {
                    _logger.LogDebug("Rebuilding {Count} recoverable game-artwork jobs from the catalog during reconciliation", recovery.Count);
                }
            }

            foreach (var library in _games.GetGameLibraries())
            {
                var reconciledSystems = new List<(GameSystem System, List<ArtworkCandidate> All, List<ArtworkCandidate> Boxart)>();
                foreach (var system in _games.GetSystems(library.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var games = _games.GetGames(library.Id, system.Id);
                    var allCandidates = games
                        .SelectMany(game => ArtworkKinds.Select(kind => TryCreateCandidate(library, game, kind)))
                        .Where(candidate => candidate != null)
                        .Cast<ArtworkCandidate>()
                        .ToList();
                    var candidates = allCandidates.Where(candidate => candidate.Kind == GameThumbService.ThumbKind.Boxart).ToList();
                    reconciledSystems.Add((system, allCandidates, candidates));
                }

                var orphaned = await _catalog.PruneAsync(
                    library.Id,
                    reconciledSystems.SelectMany(item => item.All).Select(candidate => candidate.Key),
                    cancellationToken).ConfigureAwait(false);
                foreach (var path in orphaned.Where(IsManagedArtworkPath))
                {
                    try { File.Delete(path); } catch (Exception ex) { _logger.LogDebug(ex, "Removing orphaned game artwork {Path} failed", path); }
                }

                foreach (var (system, allCandidates, candidates) in reconciledSystems)
                {
                    // Persist a complete current game/role projection. Only box art is normally
                    // prewarmed; snap/title stay pending until a visible client priority hint.
                    await _catalog.SynchronizeProjectionAsync(
                        library.Id,
                        allCandidates.Select(candidate => new ArtworkCatalogProjectionEntry(candidate.Key, candidate.SystemId, candidate.GameId)),
                        ProviderVersion,
                        new ArtworkCatalogSystemMetadata(system.Id, system.Name, system.Core, system.GameCount),
                        cancellationToken).ConfigureAwait(false);

                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    var inventoryGeneration = Hash(string.Join("\n", candidates
                        .OrderBy(candidate => candidate.GameId, StringComparer.Ordinal)
                        .Select(candidate => candidate.GameId + "|" + candidate.Key.RomFingerprint)));
                    var discovery = await _catalog.GetOrCreatePreviewDiscoveryAsync(
                        library.Id,
                        candidates[0].SystemId,
                        inventoryGeneration,
                        candidates.Select(candidate => candidate.GameId),
                        cancellationToken).ConfigureAwait(false);
                    var ordered = discovery.PreviewCandidateGameIds
                        .Select(id => candidates.FirstOrDefault(candidate => string.Equals(candidate.GameId, id, StringComparison.Ordinal)))
                        .Where(candidate => candidate != null)
                        .Cast<ArtworkCandidate>()
                        .ToArray();

                    if (discovery.PreviewGameIds.Count == 0)
                    {
                        var plan = new PreviewPlan(library.Id, candidates[0].SystemId, inventoryGeneration, ordered);
                        if (_previewPlans.TryAdd(plan.Identity, plan))
                        {
                            var first = plan.TakeNext();
                            if (first != null)
                            {
                                await QueueCandidateAsync(first, ArtworkLane.Preview, plan, cancellationToken, reopenMetadataDeferred).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        foreach (var candidate in ordered)
                        {
                            await QueueCandidateAsync(candidate, ArtworkLane.Bulk, null, cancellationToken, reopenMetadataDeferred).ConfigureAwait(false);
                        }
                    }
                }
            }

            await EnqueueRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task QueueCandidateAsync(
        ArtworkCandidate candidate,
        ArtworkLane lane,
        PreviewPlan? preview,
        CancellationToken cancellationToken,
        bool reopenMetadataDeferred = false)
    {
        var entry = await _catalog.GetOrCreateAsync(
            candidate.Key,
            candidate.SystemId,
            ProviderVersion,
            candidate.GameId,
            cancellationToken).ConfigureAwait(false);
        switch (entry.State)
        {
            case ArtworkCatalogState.ThumbnailReady:
                await AdvancePreviewAsync(preview, candidate, true, cancellationToken).ConfigureAwait(false);
                break;
            case ArtworkCatalogState.OriginalReady when !string.IsNullOrWhiteSpace(entry.OriginalPath):
                QueueThumbnail(candidate with { OriginalPath = entry.OriginalPath }, entry.RetryAfterUtc);
                await AdvancePreviewAsync(preview, candidate, true, cancellationToken).ConfigureAwait(false);
                break;
            case ArtworkCatalogState.Missing:
                await AdvancePreviewAsync(preview, candidate, false, cancellationToken).ConfigureAwait(false);
                break;
            case ArtworkCatalogState.MetadataDeferred:
                if (reopenMetadataDeferred)
                {
                    await _catalog.ReopenMetadataDeferredAsync(
                        candidate.Key,
                        candidate.SystemId,
                        candidate.GameId,
                        cancellationToken).ConfigureAwait(false);
                    QueueRemote(candidate, lane, preview, null);
                }
                else
                {
                    await AdvancePreviewAsync(preview, candidate, false, cancellationToken).ConfigureAwait(false);
                }
                break;
            default:
                QueueRemote(candidate, lane, preview, entry.RetryAfterUtc);
                break;
        }
    }

    private Task EnqueueRecoveryAsync(IReadOnlyList<ArtworkCatalogWorkItem> recovery, CancellationToken cancellationToken)
    {
        foreach (var item in recovery)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Entry.GameId) || !TryParseKind(item.Entry.Key.Role, out var kind))
            {
                continue;
            }

            var candidate = TryCreateCandidate(item.Entry.Key.LibraryId, item.Entry.GameId, kind);
            if (candidate == null || !string.Equals(candidate.Key.StorageKey, item.Entry.Key.StorageKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (item.Stage == ArtworkCatalogWorkStage.DeriveThumbnail &&
                !string.IsNullOrWhiteSpace(item.Entry.OriginalPath) &&
                IsManagedArtworkPath(item.Entry.OriginalPath) &&
                File.Exists(item.Entry.OriginalPath))
            {
                QueueThumbnail(candidate with { OriginalPath = item.Entry.OriginalPath }, item.NotBeforeUtc);
            }
            else if (item.Stage == ArtworkCatalogWorkStage.AcquireOriginal)
            {
                QueueRemote(candidate, ArtworkLane.Bulk, null, item.NotBeforeUtc);
            }
        }

        return Task.CompletedTask;
    }

    private async Task RemoteWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _remote.Available.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!_remote.TryDequeue(out var work))
                {
                    continue;
                }

                if (!TryStartRemote(work))
                {
                    continue;
                }

                var retryQueued = false;
                try
                {
                    string? original = null;
                    GameArtworkLookupResult? transientFailure = null;
                    GameArtworkLookupResult? metadataPending = null;
                    foreach (var core in work.Candidate.Source.Cores)
                    {
                        var lookup = await _thumbs.LookupThumbAsync(core, work.Candidate.Source.CoreWasDefaulted, work.Candidate.Source.SystemName, work.Candidate.Source.RomPath, work.Candidate.Source.Title, work.Candidate.Kind, cancellationToken).ConfigureAwait(false);
                        if (lookup.Outcome == GameArtworkLookupOutcome.Found)
                        {
                            original = lookup.Path;
                            break;
                        }

                        if (lookup.Outcome == GameArtworkLookupOutcome.TransientFailure)
                        {
                            transientFailure ??= lookup;
                        }
                        else if (lookup.Outcome == GameArtworkLookupOutcome.MetadataPending)
                        {
                            metadataPending ??= lookup;
                        }
                    }

                    if (original == null)
                    {
                        if (metadataPending != null)
                        {
                            await DeferMetadataAsync(work, cancellationToken).ConfigureAwait(false);
                            retryQueued = true;
                            continue;
                        }

                        if (transientFailure != null)
                        {
                            await RetryRemoteAsync(work, transientFailure.RetryDelay, cancellationToken).ConfigureAwait(false);
                            retryQueued = true;
                            continue;
                        }

                        await _catalog.MarkMissingAsync(work.Candidate.Key, work.Candidate.SystemId, ProviderVersion, cancellationToken).ConfigureAwait(false);
                        await AdvancePreviewAsync(work.Preview, work.Candidate, false, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _catalog.MarkOriginalReadyAsync(work.Candidate.Key, work.Candidate.SystemId, original, cancellationToken).ConfigureAwait(false);
                    QueueThumbnail(work.Candidate with { OriginalPath = original });
                    await AdvancePreviewAsync(work.Preview, work.Candidate, true, cancellationToken).ConfigureAwait(false);
                }
                catch (ThumbLookupTimedOutException)
                {
                    await RetryRemoteAsync(work, null, cancellationToken).ConfigureAwait(false);
                    retryQueued = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Includes failures raised by RetryRemoteAsync itself (e.g. a catalog persist
                    // hitting a full disk). Without the outer try/catch below, an exception here
                    // would kill this worker silently and stop all artwork acquisition for the
                    // process lifetime.
                    _logger.LogDebug(ex, "Artwork acquisition failed for {GameId}", work.Candidate.GameId);
                    await RetryRemoteAsync(work, null, cancellationToken).ConfigureAwait(false);
                    retryQueued = true;
                }
                finally
                {
                    if (!retryQueued)
                    {
                        CompleteRemote(work);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The inner handlers above can themselves throw (a catalog persist hits a full disk
                // or a momentarily read-only mount). Without this outer catch, the worker task dies
                // silently -- nothing awaits it until StopAsync -- and artwork acquisition stops for
                // the process lifetime while the queue keeps filling up behind it.
                _logger.LogError(ex, "Remote artwork worker iteration failed; continuing");
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Remote artwork worker exited without a cancellation request");
        }
    }

    private async Task RetryRemoteAsync(ArtworkWork work, TimeSpan? providerRetryDelay, CancellationToken cancellationToken)
    {
        var delay = providerRetryDelay is { } suggested && suggested > TimeSpan.Zero ? suggested : RetryDelay;
        var retryAfter = DateTimeOffset.UtcNow.Add(delay);
        await _catalog.MarkRetryableAsync(work.Candidate.Key, work.Candidate.SystemId, ArtworkCatalogWorkStage.AcquireOriginal, retryAfter, cancellationToken).ConfigureAwait(false);
        CompleteRemote(work);
        QueueRemote(work.Candidate, work.Lane, work.Preview, retryAfter);
    }

    private async Task DeferMetadataAsync(ArtworkWork work, CancellationToken cancellationToken)
    {
        var retryAfter = await _catalog.MarkMetadataPendingAsync(
            work.Candidate.Key,
            work.Candidate.SystemId,
            ProviderVersion,
            DateTimeOffset.UtcNow,
            MetadataFirstRetryDelay,
            MetadataRepeatRetryDelay,
            MetadataRetryWindow,
            cancellationToken).ConfigureAwait(false);
        CompleteRemote(work);
        if (retryAfter is { } scheduled)
        {
            _logger.LogDebug(
                "Deferring artwork for {GameId} until {RetryAfterUtc} while its metadata index is pending",
                work.Candidate.GameId,
                scheduled);
            QueueRemote(work.Candidate, work.Lane, work.Preview, scheduled);
        }
        else
        {
            _logger.LogDebug(
                "Stopped automatic metadata retries for {GameId} after the {Hours}-hour retry window",
                work.Candidate.GameId,
                MetadataRetryWindow.TotalHours);
            await AdvancePreviewAsync(work.Preview, work.Candidate, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ThumbnailWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _thumbnailAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!_thumbnail.TryDequeue(out var work))
                {
                    continue;
                }

                var retryQueued = false;
                try
                {
                    var thumbnail = await _thumbs.GetDerivedThumbAsync(work.Candidate.OriginalPath!, cancellationToken).ConfigureAwait(false);
                    if (thumbnail.Outcome == DerivedThumbnailOutcome.TimedOut)
                    {
                        // The encode may still be running in the background; this is not a final
                        // answer. Mark retryable (not ready) so GetStartupRecoveryWorkAsync and the
                        // next reconciliation pass revisit it instead of serving the full-size
                        // original forever.
                        var retryAfter = DateTimeOffset.UtcNow.Add(RetryDelay);
                        await _catalog.MarkRetryableAsync(work.Candidate.Key, work.Candidate.SystemId, ArtworkCatalogWorkStage.DeriveThumbnail, retryAfter, cancellationToken).ConfigureAwait(false);
                        _thumbnailQueued.TryRemove(work.Identity, out _);
                        QueueThumbnail(work.Candidate, retryAfter);
                        retryQueued = true;
                    }
                    else
                    {
                        await _catalog.MarkThumbnailReadyAsync(work.Candidate.Key, work.Candidate.SystemId, thumbnail.Path, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Includes failures raised by MarkRetryableAsync itself (e.g. a catalog persist
                    // hitting a full disk). Without the outer try/catch below, an exception here
                    // would kill this worker silently and stop all thumbnail derivation for the
                    // process lifetime.
                    _logger.LogDebug(ex, "Artwork thumbnail generation failed for {GameId}", work.Candidate.GameId);
                    var retryAfter = DateTimeOffset.UtcNow.Add(RetryDelay);
                    await _catalog.MarkRetryableAsync(work.Candidate.Key, work.Candidate.SystemId, ArtworkCatalogWorkStage.DeriveThumbnail, retryAfter, cancellationToken).ConfigureAwait(false);
                    _thumbnailQueued.TryRemove(work.Identity, out _);
                    QueueThumbnail(work.Candidate, retryAfter);
                    retryQueued = true;
                }
                finally
                {
                    if (!retryQueued)
                    {
                        _thumbnailQueued.TryRemove(work.Identity, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The inner handlers above can themselves throw. Without this outer catch, the
                // worker task dies silently -- nothing awaits it until StopAsync -- and thumbnail
                // derivation stops for the process lifetime while the queue keeps filling up
                // behind it.
                _logger.LogError(ex, "Thumbnail artwork worker iteration failed; continuing");
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Thumbnail artwork worker exited without a cancellation request");
        }
    }

    private async Task AdvancePreviewAsync(PreviewPlan? plan, ArtworkCandidate candidate, bool artworkFound, CancellationToken cancellationToken)
    {
        if (plan == null)
        {
            return;
        }

        var next = plan.Complete(candidate, artworkFound, out var confirmed, out var bulkRemainder);
        if (confirmed != null)
        {
            await _catalog.CommitPreviewSelectionAsync(plan.LibraryId, plan.SystemId, plan.InventoryGeneration, confirmed, cancellationToken).ConfigureAwait(false);
            _previewPlans.TryRemove(plan.Identity, out _);
            foreach (var bulk in bulkRemainder!)
            {
                await QueueCandidateAsync(bulk, ArtworkLane.Bulk, null, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (next != null)
        {
            await QueueCandidateAsync(next, ArtworkLane.Preview, plan, cancellationToken).ConfigureAwait(false);
        }
    }

    private void QueueRemote(ArtworkCandidate candidate, ArtworkLane lane, PreviewPlan? preview, DateTimeOffset? notBefore)
    {
        var work = ArtworkWork.Remote(candidate, lane, preview);
        lock (_remoteQueueGate)
        {
            if (_queued.TryGetValue(work.Identity, out var existing))
            {
                if (existing.Started || !IsHigherPriority(lane, existing.Lane))
                {
                    return;
                }

                existing = existing with { Lane = lane, Version = existing.Version + 1 };
                _queued[work.Identity] = existing;
                work = work with { QueueVersion = existing.Version };
            }
            else
            {
                if (_queued.Count >= MaxQueuedRemoteWork && !MakeRoomForRemoteUnsafe(lane))
                {
                    return;
                }

                var queued = new QueuedRemoteWork(lane, 1, false);
                _queued.Add(work.Identity, queued);
                work = work with { QueueVersion = queued.Version };
            }
        }

        if (notBefore is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
        {
            _ = DelayQueueRemoteAsync(work, retryAfter, _stop?.Token ?? CancellationToken.None);
            return;
        }

        _remote.Enqueue(work);
    }

    private async Task DelayQueueRemoteAsync(ArtworkWork work, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(notBefore - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _remote.Enqueue(work);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteRemote(work);
        }
    }

    private void QueueThumbnail(ArtworkCandidate candidate, DateTimeOffset? notBefore = null)
    {
        var work = ArtworkWork.Thumbnail(candidate);
        if (_thumbnailQueued.Count >= MaxQueuedThumbnailWork)
        {
            return;
        }

        if (_thumbnailQueued.TryAdd(work.Identity, 0))
        {
            if (notBefore is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
            {
                _ = DelayQueueThumbnailAsync(work, retryAfter, _stop?.Token ?? CancellationToken.None);
                return;
            }

            _thumbnail.Enqueue(work);
            _thumbnailAvailable.Release();
        }
    }

    private async Task DelayQueueThumbnailAsync(ArtworkWork work, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(notBefore - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            _thumbnail.Enqueue(work);
            _thumbnailAvailable.Release();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _thumbnailQueued.TryRemove(work.Identity, out _);
        }
    }

    private bool TryStartRemote(ArtworkWork work)
    {
        lock (_remoteQueueGate)
        {
            if (!_queued.TryGetValue(work.Identity, out var queued) || queued.Started || queued.Version != work.QueueVersion)
            {
                return false;
            }

            _queued[work.Identity] = queued with { Started = true };
            return true;
        }
    }

    private void CompleteRemote(ArtworkWork work)
    {
        lock (_remoteQueueGate)
        {
            if (_queued.TryGetValue(work.Identity, out var queued) && queued.Version == work.QueueVersion)
            {
                _queued.Remove(work.Identity);
            }
        }
    }

    private bool MakeRoomForRemoteUnsafe(ArtworkLane incoming)
    {
        if (incoming != ArtworkLane.Interactive)
        {
            return false;
        }

        var evictable = _queued.FirstOrDefault(pair => !pair.Value.Started && pair.Value.Lane != ArtworkLane.Interactive);
        if (string.IsNullOrEmpty(evictable.Key))
        {
            return false;
        }

        _queued.Remove(evictable.Key);
        return true;
    }

    private static bool IsHigherPriority(ArtworkLane candidate, ArtworkLane existing) => candidate < existing;

    private ArtworkCandidate? TryCreateCandidate(
        string libraryId,
        string gameId,
        GameThumbService.ThumbKind kind,
        GameLookupSnapshot? lookup = null)
    {
        // A snapshot is only usable for the library it was taken against; anything else falls back
        // to the uncached resolve rather than silently answering from the wrong inventory.
        if (lookup != null && string.Equals(lookup.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase))
        {
            return lookup.Library == null || !lookup.Games.TryGetValue(gameId, out var known)
                ? null
                : TryCreateCandidate(lookup.Library, known, kind);
        }

        var library = _games.GetGameLibraries().FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        var game = _games.GetGames(libraryId, null).FirstOrDefault(item => string.Equals(item.Id, gameId, StringComparison.Ordinal));
        return library == null || game == null ? null : TryCreateCandidate(library, game, kind);
    }

    private ArtworkCandidate? TryCreateCandidate(GameLibrary library, GameSummary game, GameThumbService.ThumbKind kind)
    {
        var source = _games.ResolveThumbSource(library.Id, game.Id);
        if (source == null)
        {
            return null;
        }

        var root = library.Locations.Where(path => GamePathResolver.IsWithinRoot(path, source.RomPath)).OrderByDescending(path => path.Length).FirstOrDefault();
        if (root == null)
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(root, source.RomPath).Replace('\\', '/');
        var info = new FileInfo(source.RomPath);
        if (!info.Exists)
        {
            return null;
        }

        var key = ArtworkCatalogKey.Create(library.Id, relativePath, $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}", KindName(kind));
        return new ArtworkCandidate(game.Id, source.SystemName, kind, source, key, null);
    }

    private static GameThumbService.ThumbKind ParseKind(string? kind) => GameThumbService.ParseKind(kind);

    private static bool TryParseKind(string? kind, out GameThumbService.ThumbKind parsed)
    {
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "boxart":
                parsed = GameThumbService.ThumbKind.Boxart;
                return true;
            case "snap":
                parsed = GameThumbService.ThumbKind.Snap;
                return true;
            case "title":
                parsed = GameThumbService.ThumbKind.Title;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static string KindName(GameThumbService.ThumbKind kind) => kind switch
    {
        GameThumbService.ThumbKind.Snap => "snap",
        GameThumbService.ThumbKind.Title => "title",
        _ => "boxart",
    };

    private bool IsManagedArtworkPath(string path)
    {
        var dataFolder = Directory.GetParent(Path.GetFullPath(_catalog.RootPath))?.FullName;
        return dataFolder != null && GamePathResolver.IsWithinRoot(Path.Combine(dataFolder, "game_thumbs"), path);
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal sealed record ArtworkCandidate(string GameId, string SystemId, GameThumbService.ThumbKind Kind, GameThumbSource Source, ArtworkCatalogKey Key, string? OriginalPath);

    internal sealed record ArtworkWork(ArtworkCandidate Candidate, ArtworkLane Lane, PreviewPlan? Preview, bool IsThumbnail, int QueueVersion = 0)
    {
        public string Identity => (IsThumbnail ? "thumbnail:" : "remote:") + Candidate.Key.StorageKey;
        public static ArtworkWork Remote(ArtworkCandidate candidate, ArtworkLane lane, PreviewPlan? preview) => new(candidate, lane, preview, false);
        public static ArtworkWork Thumbnail(ArtworkCandidate candidate) => new(candidate, ArtworkLane.Interactive, null, true);
    }

    private sealed record QueuedRemoteWork(ArtworkLane Lane, int Version, bool Started);

    internal sealed class PreviewPlan
    {
        private readonly object _gate = new();
        private readonly IReadOnlyList<ArtworkCandidate> _candidates;
        private readonly List<string> _confirmed = new(4);
        private int _next;
        private bool _finished;

        public PreviewPlan(string libraryId, string systemId, string inventoryGeneration, IReadOnlyList<ArtworkCandidate> candidates)
        {
            LibraryId = libraryId;
            SystemId = systemId;
            InventoryGeneration = inventoryGeneration;
            _candidates = candidates;
        }

        public string LibraryId { get; }
        public string SystemId { get; }
        public string InventoryGeneration { get; }
        public string Identity => LibraryId + "/" + SystemId + "/" + InventoryGeneration;

        public ArtworkCandidate? TakeNext()
        {
            lock (_gate)
            {
                return _next < _candidates.Count ? _candidates[_next++] : null;
            }
        }

        public ArtworkCandidate? Complete(ArtworkCandidate candidate, bool found, out IReadOnlyList<string>? confirmed, out IReadOnlyList<ArtworkCandidate>? bulkRemainder)
        {
            lock (_gate)
            {
                confirmed = null;
                bulkRemainder = null;
                if (_finished)
                {
                    return null;
                }

                if (found && !_confirmed.Contains(candidate.GameId, StringComparer.Ordinal))
                {
                    _confirmed.Add(candidate.GameId);
                }

                if (_confirmed.Count == 4 || _next >= _candidates.Count)
                {
                    _finished = true;
                    confirmed = _confirmed.ToArray();
                    bulkRemainder = _candidates.Skip(_next).ToArray();
                    return null;
                }

                return _candidates[_next++];
            }
        }
    }
}

/// <summary>
/// One library's game inventory resolved once, so a batched mutation request (notably
/// <c>POST ArtworkPriority</c>) pays for a single filesystem enumeration rather than one per item
/// per role. <see cref="Library"/> is null when the library id did not resolve, in which case
/// <see cref="Games"/> is empty and every membership check against it fails -- the same answer the
/// uncached path gives, at no I/O cost.
/// </summary>
public sealed record GameLookupSnapshot(
    string LibraryId,
    GameLibrary? Library,
    IReadOnlyDictionary<string, GameSummary> Games);

/// <summary>Read-only controller projection for a system's known artwork and game membership.</summary>
public sealed record GameArtworkSystemReadResult(
    ArtworkCatalogSystemSnapshot System,
    IReadOnlyList<string> GameIds,
    IReadOnlyList<GameArtworkReadEntry> Entries);

/// <summary>One game-role catalog entry returned by the reconciliation read API.</summary>
public sealed record GameArtworkReadEntry(string GameId, string Role, ArtworkCatalogEntry Entry);

/// <summary>The exact local artifact eligible for an authenticated controller response.</summary>
public sealed record GameArtworkLocalAsset(
    ArtworkCatalogEntry Entry,
    string Path,
    string ContentType,
    bool IsThumbnail);

internal enum ArtworkLane { Interactive, Preview, Bulk }

/// <summary>Fair remote-work selector: hints get low latency, previews rotate by system, and bulk is forced after four non-bulk selections.</summary>
internal sealed class ArtworkWorkScheduler
{
    private readonly object _gate = new();
    private readonly Queue<GameArtworkReconciliationService.ArtworkWork> _interactive = new();
    private readonly Queue<GameArtworkReconciliationService.ArtworkWork> _bulk = new();
    private readonly Dictionary<string, Queue<GameArtworkReconciliationService.ArtworkWork>> _preview = new(StringComparer.Ordinal);
    private readonly Queue<string> _previewSystems = new();
    private int _nonBulkSelections;

    public SemaphoreSlim Available { get; } = new(0);

    public void Enqueue(GameArtworkReconciliationService.ArtworkWork work)
    {
        lock (_gate)
        {
            if (work.Lane == ArtworkLane.Interactive)
            {
                _interactive.Enqueue(work);
            }
            else if (work.Lane == ArtworkLane.Preview)
            {
                var system = work.Candidate.Key.LibraryId + "/" + work.Candidate.SystemId;
                if (!_preview.TryGetValue(system, out var queue))
                {
                    queue = new Queue<GameArtworkReconciliationService.ArtworkWork>();
                    _preview.Add(system, queue);
                    _previewSystems.Enqueue(system);
                }

                queue.Enqueue(work);
            }
            else
            {
                _bulk.Enqueue(work);
            }
        }

        Available.Release();
    }

    public bool TryDequeue(out GameArtworkReconciliationService.ArtworkWork work)
    {
        lock (_gate)
        {
            if (_bulk.Count > 0 && _nonBulkSelections >= 4)
            {
                _nonBulkSelections = 0;
                work = _bulk.Dequeue();
                return true;
            }

            if (_interactive.Count > 0)
            {
                _nonBulkSelections++;
                work = _interactive.Dequeue();
                return true;
            }

            if (_previewSystems.Count > 0)
            {
                var system = _previewSystems.Dequeue();
                var queue = _preview[system];
                work = queue.Dequeue();
                if (queue.Count == 0)
                {
                    _preview.Remove(system);
                }
                else
                {
                    _previewSystems.Enqueue(system);
                }

                _nonBulkSelections++;
                return true;
            }

            if (_bulk.Count > 0)
            {
                _nonBulkSelections = 0;
                work = _bulk.Dequeue();
                return true;
            }

            work = null!;
            return false;
        }
    }
}
