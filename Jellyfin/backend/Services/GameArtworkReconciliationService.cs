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
/// items consistently, so reconciliation is triggered by plugin startup, completion of Jellyfin's
/// built-in "RefreshLibrary" scheduled task (<see cref="ITaskManager.TaskCompleted"/>), and file
/// system events from configured ROM roots. See <see cref="StartAsync"/> for the trigger details.
/// </summary>
public sealed class GameArtworkReconciliationService : IHostedService
{
    private static readonly TimeSpan LibraryFileSystemQuietPeriod = TimeSpan.FromSeconds(5);
    private long _lastWatcherOverflowWarningTicks = long.MinValue / 2;

    // v3 repairs false terminal misses caused by RDB and thumbnail alternate-title separators.
    private const string ProviderVersion = "libretro-thumbnails-v3";
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
    // Bulk and interactive acquisition are best-effort at this threshold. One unresolved preview
    // per incomplete system may exceed it so preview discovery can never be stranded by admission.
    private const int MaxQueuedBestEffortRemoteWork = 20_000;
    private const int MaxQueuedThumbnailWork = 20_000;

    // Bounded well below the encode gate on large hosts: artwork backfill is background work
    // sharing a box with playback and transcoding, and only two downloaders feed it, so more
    // encoders buy nothing. On the NAS-class hardware Jellyfin often runs on this resolves to
    // 1-2 -- i.e. unchanged from the single worker it replaces.
    private const int MaxThumbnailWorkers = 4;

    private static readonly TimeSpan WorkerStopGrace = TimeSpan.FromSeconds(10);

    private static int ThumbnailWorkerCount =>
        Math.Clamp(DerivedThumbnailService.DefaultEncodeConcurrency, 1, MaxThumbnailWorkers);
    private readonly GamesService _games;
    private readonly GameThumbService _thumbs;
    private readonly GameArtworkCatalog _catalog;
    private readonly ILogger<GameArtworkReconciliationService> _logger;
    private readonly ITaskManager? _taskManager;
    private readonly bool _watchLibraryFileSystem;
    private readonly TimeSpan _workerStopGrace;
    private readonly ArtworkWorkScheduler _remote = new();
    private readonly ConcurrentQueue<ArtworkWork> _thumbnail = new();
    private readonly SemaphoreSlim _thumbnailAvailable = new(0);
    private readonly object _remoteQueueGate = new();
    private readonly object _thumbnailQueueGate = new();
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<string, QueuedRemoteWork> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _thumbnailQueued = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reconciliationAvailable = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task[] _remoteWorkers = Array.Empty<Task>();
    private Task[] _thumbnailWorkers = Array.Empty<Task>();
    private Task? _reconciliationWorker;
    private readonly Dictionary<string, FileSystemWatcher> _libraryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _libraryWatcherLock = new();
    private readonly object _libraryWatcherDebounceLock = new();
    private Timer? _libraryWatcherDebounce;
    private bool _startupRecoveryDone;
    private int _reopenMetadataDeferred;
    private int _nextRemoteQueueVersion;
    private int _nextThumbnailQueueVersion;
    private Task _shutdownCleanup = Task.CompletedTask;

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
        : this(games, thumbs, catalog, logger, taskManager, watchLibraryFileSystem: true)
    {
    }

    internal GameArtworkReconciliationService(
        GamesService games,
        GameThumbService thumbs,
        GameArtworkCatalog catalog,
        ILogger<GameArtworkReconciliationService> logger,
        ITaskManager? taskManager,
        bool watchLibraryFileSystem,
        // The grace exists so a wedged worker cannot strand restart; a test drives it with
        // milliseconds rather than waiting out the production value.
        TimeSpan? workerStopGraceForTests = null)
    {
        _games = games;
        _thumbs = thumbs;
        _catalog = catalog;
        _logger = logger;
        _taskManager = taskManager;
        _watchLibraryFileSystem = watchLibraryFileSystem;
        _workerStopGrace = workerStopGraceForTests ?? WorkerStopGrace;
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
        QueueRemote(candidate, ArtworkLane.Interactive, null);
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
            QueueRemote(candidate, ArtworkLane.Interactive, entry.RetryAfterUtc);
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
    internal bool EnqueueRemoteForTest(
        ArtworkCandidate candidate,
        ArtworkLane lane = ArtworkLane.Interactive,
        DateTimeOffset? notBefore = null) =>
        QueueRemote(candidate, lane, notBefore);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task priorCleanup;
            var started = false;
            lock (_lifecycleGate)
            {
                if (_stop is { IsCancellationRequested: false })
                {
                    return;
                }

                priorCleanup = _shutdownCleanup;
                if (_stop == null && priorCleanup.IsCompleted)
                {
                    // Do not call ILibraryManager.AddParts here. It replaces Jellyfin's complete
                    // resolver set instead of appending, so reconciliation is driven by the task
                    // completion event and ROM-root watchers without mutating library parts.
                    _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var token = _stop.Token;
                    _remoteWorkers = Enumerable.Range(0, 2).Select(_ => Task.Run(() => RemoteWorkerAsync(token), token)).ToArray();
                    // One worker could never reach the concurrency the encode gate already
                    // allows, so downloads outran encoding and left a backlog in OriginalReady.
                    _thumbnailWorkers = Enumerable.Range(0, ThumbnailWorkerCount)
                        .Select(_ => Task.Run(() => ThumbnailWorkerAsync(token), token)).ToArray();
                    _reconciliationWorker = Task.Run(() => ReconciliationWorkerAsync(token), token);
                    _thumbs.MetadataIndexAvailable += OnMetadataIndexAvailable;
                    if (_taskManager != null)
                    {
                        _taskManager.TaskCompleted += OnTaskCompleted;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "No task manager was supplied to game artwork reconciliation; scheduled library refreshes will not signal artwork reconciliation");
                    }

                    started = true;
                }
            }

            if (started)
            {
                QueueReconciliation("plugin startup");
                return;
            }

            await priorCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task cleanup;
        CancellationTokenSource? stop = null;
        TaskCompletionSource? cleanupCompletion = null;
        lock (_lifecycleGate)
        {
            if (_stop is { IsCancellationRequested: false } activeStop)
            {
                stop = activeStop;
                cleanupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                cleanup = cleanupCompletion.Task;
                _shutdownCleanup = cleanup;
                stop.Cancel();
            }
            else
            {
                cleanup = _shutdownCleanup;
            }
        }

        if (stop != null)
        {
            _thumbs.MetadataIndexAvailable -= OnMetadataIndexAvailable;
            if (_taskManager != null)
            {
                _taskManager.TaskCompleted -= OnTaskCompleted;
            }

            CancelLibraryWatcherDebounce();
            StopLibraryWatchers();
            if (_remoteWorkers.Length > 0)
            {
                _remote.Available.Release(_remoteWorkers.Length);
            }

            if (_thumbnailWorkers.Length > 0)
            {
                _thumbnailAvailable.Release(_thumbnailWorkers.Length);
            }

            TrySignalReconciliation();
            var workers = _remoteWorkers.Concat(_thumbnailWorkers).Concat(new[] { _reconciliationWorker }.OfType<Task>());
            _ = CompleteStopAsync(stop, Task.WhenAll(workers), cleanupCompletion!);
        }

        try
        {
            await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cleanup continues independently; StartAsync waits for it before creating new workers.
        }
    }

    private async Task CompleteStopAsync(
        CancellationTokenSource stop,
        Task workersStopped,
        TaskCompletionSource completion)
    {
        try
        {
            await FinishStopAsync(stop, workersStopped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Completed rather than faulted: every later StartAsync awaits this same task, so a
            // faulted cleanup would leave the service permanently unstartable.
            _logger.LogWarning(ex, "Cleaning up after game artwork reconciliation shutdown failed");
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task FinishStopAsync(CancellationTokenSource stop, Task workersStopped)
    {
        var workersQuiesced = false;
        try
        {
            // Bounded: a worker parked in a non-cancelable call (a filesystem operation on a dead
            // NAS mount) would otherwise never complete, and every later StartAsync waits on this
            // cleanup -- so an unbounded wait here makes one wedged worker permanently unstartable.
            // A straggler keeps its canceled lifetime token; queue versions and lifetime checks
            // prevent it from publishing into the replacement lifetime.
            await workersStopped.WaitAsync(_workerStopGrace).ConfigureAwait(false);
            workersQuiesced = true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "A game artwork worker did not stop within {Seconds}s; cleaning up without it",
                _workerStopGrace.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // A canceled worker is stopped and therefore safe to clean up.
            workersQuiesced = true;
        }
        catch (Exception ex)
        {
            // Task.WhenAll only faults after every worker has finished.
            workersQuiesced = true;
            _logger.LogWarning(ex, "A game artwork worker failed while stopping");
        }
        finally
        {
            lock (_remoteQueueGate)
            {
                _queued.Clear();
                _remote.Clear();
            }

            lock (_thumbnailQueueGate)
            {
                _thumbnail.Clear();
                _thumbnailQueued.Clear();
                while (_thumbnailAvailable.Wait(0))
                {
                    // Late delayed work rechecks this cleared identity before publishing a permit.
                }
            }

            _startupRecoveryDone = false;
            stop.Dispose();
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_stop, stop))
                {
                    _stop = null;
                }
            }

            // Never wait on a catalog gate that an abandoned worker may still hold. Its state is
            // re-derived by startup recovery and the next reconciliation pass.
            if (workersQuiesced)
            {
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
        if (_stop is not { IsCancellationRequested: false })
        {
            return;
        }

        if (reopenMetadataDeferred)
        {
            Interlocked.Exchange(ref _reopenMetadataDeferred, 1);
        }

        _logger.LogDebug("Queueing game artwork reconciliation after {Reason}", reason);
        TrySignalReconciliation();
    }

    /// <summary>
    /// Brings the watcher set in line with the currently configured game-library roots. Diff-based
    /// and idempotent, so calling it on every reconciliation pass costs one directory enumeration
    /// when nothing has changed. Beyond tracking configuration-page edits, that recurring call is
    /// what recovers a root that could not be watched earlier: a NAS share that was offline at
    /// startup, a library location changed outside Moonfin's own configuration page, or a watcher
    /// whose construction threw.
    /// </summary>
    private void SyncLibraryWatchers(CancellationToken lifetimeToken)
    {
        if (!_watchLibraryFileSystem || lifetimeToken.IsCancellationRequested)
        {
            return;
        }

        HashSet<string> desired;
        try
        {
            desired = _games.GetGameLibraries()
                .SelectMany(library => library.Locations)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Resolving libraries goes through Jellyfin's library manager; a failure here must not
            // take down reconciliation or a configuration save. Keep the existing watcher set.
            _logger.LogWarning(ex, "Resolving game library roots for artwork watching failed");
            return;
        }

        lock (_libraryWatcherLock)
        {
            // Root resolution can outlive shutdown. Check this worker's token rather than _stop,
            // which may already belong to a replacement lifetime.
            if (lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var root in _libraryWatchers.Keys.Where(root => !desired.Contains(root)).ToList())
            {
                _libraryWatchers[root].Dispose();
                _libraryWatchers.Remove(root);
                _logger.LogDebug("Stopped watching game library root {Root}", root);
            }

            foreach (var root in desired)
            {
                if (_libraryWatchers.ContainsKey(root))
                {
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,

                        // The 8 KB default overflows within a second of a romset copy. 64 KB is the
                        // documented maximum; overflow is still handled, just made rarer.
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = true,
                    };
                    watcher.Created += OnLibraryFileSystemChanged;
                    watcher.Changed += OnLibraryFileSystemChanged;
                    watcher.Deleted += OnLibraryFileSystemChanged;
                    watcher.Renamed += OnLibraryFileSystemChanged;
                    watcher.Error += OnLibraryWatcherError;
                    _libraryWatchers.Add(root, watcher);
                    _logger.LogDebug("Watching game library root {Root} for artwork reconciliation", root);
                }
                catch (Exception ex)
                {
                    // A root that is missing or unreachable right now is retried by the next sync,
                    // so this stays a warning rather than a failure.
                    _logger.LogWarning(ex, "Watching game library root {Root} for artwork reconciliation failed", root);
                }
            }
        }
    }

    /// <summary>
    /// Called when the configured game libraries change (see MoonfinPlugin.UpdateConfiguration).
    /// Queueing reconciliation is the load-bearing half: ROMs already sitting under a newly
    /// selected root raise no filesystem events, so without this they would wait for a scheduled
    /// scan or a restart. The reconciliation pass itself re-syncs the watcher set.
    /// </summary>
    public void OnGameLibrariesChanged() => QueueReconciliation("game library configuration changed");

    /// <summary>Returns the roots currently being watched. Test seam.</summary>
    internal IReadOnlyList<string> WatchedLibraryRootsForTests
    {
        get
        {
            lock (_libraryWatcherLock)
            {
                return _libraryWatchers.Keys.ToList();
            }
        }
    }

    private void StopLibraryWatchers()
    {
        lock (_libraryWatcherLock)
        {
            foreach (var watcher in _libraryWatchers.Values)
            {
                watcher.Dispose();
            }

            _libraryWatchers.Clear();
        }
    }

    private void OnLibraryFileSystemChanged(object sender, FileSystemEventArgs e) =>
        DebounceLibraryFileSystemReconciliation();

    /// <summary>
    /// Restarts the quiet period. One timer is reused for the life of the service and only its due
    /// time is pushed back, so copying in a romset costs one allocation rather than one per file.
    /// </summary>
    private void DebounceLibraryFileSystemReconciliation()
    {
        if (_stop is not { IsCancellationRequested: false })
        {
            return;
        }

        lock (_libraryWatcherDebounceLock)
        {
            // Checked inside the lock as well: StopAsync disposes the timer under it, and watcher
            // events keep arriving on their own threads while that happens.
            if (_stop is not { IsCancellationRequested: false })
            {
                return;
            }

            _libraryWatcherDebounce ??= new Timer(
                _ => QueueReconciliation("game-library filesystem change"),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _libraryWatcherDebounce.Change(LibraryFileSystemQuietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelLibraryWatcherDebounce()
    {
        lock (_libraryWatcherDebounceLock)
        {
            _libraryWatcherDebounce?.Dispose();
            _libraryWatcherDebounce = null;
        }
    }

    private void OnLibraryWatcherError(object sender, ErrorEventArgs e)
    {
        var error = e.GetException();
        var watcher = sender as FileSystemWatcher;

        if (error is InternalBufferOverflowException && watcher is { EnableRaisingEvents: true })
        {
            // Only events were lost; the watcher itself keeps working. A romset copy raises this
            // many times a second, so one warning per quiet period explains the reconcile without
            // burying the rest of the log.
            if (ShouldWarnAboutWatcherOverflow())
            {
                _logger.LogWarning(error, "Game-library watcher overflowed; reconciling artwork catalog");
            }
        }
        else
        {
            // Unlike overflow this leaves a watcher that never reports again, and SyncLibraryWatchers
            // skips any root it still holds, so the root would go unwatched until a restart.
            _logger.LogWarning(error, "Game-library watcher failed; recreating it and reconciling artwork catalog");
            RecycleLibraryWatcher(watcher);
        }

        // Debounced like an ordinary change: queueing directly ran a full library enumeration back
        // to back for the length of an import. The pass is still guaranteed, because this call arms
        // the timer itself.
        DebounceLibraryFileSystemReconciliation();
    }

    private bool ShouldWarnAboutWatcherOverflow()
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref _lastWatcherOverflowWarningTicks);
        return now - previous >= LibraryFileSystemQuietPeriod.Ticks
            && Interlocked.CompareExchange(ref _lastWatcherOverflowWarningTicks, now, previous) == previous;
    }

    /// <summary>
    /// Drops a watcher that can no longer report, so the next <see cref="SyncLibraryWatchers"/>
    /// treats its root as unwatched and builds a replacement.
    /// </summary>
    private void RecycleLibraryWatcher(FileSystemWatcher? watcher)
    {
        if (watcher == null)
        {
            return;
        }

        lock (_libraryWatcherLock)
        {
            foreach (var root in _libraryWatchers
                .Where(entry => ReferenceEquals(entry.Value, watcher))
                .Select(entry => entry.Key)
                .ToList())
            {
                _libraryWatchers.Remove(root);
            }
        }

        watcher.Dispose();
    }

    private void TrySignalReconciliation()
    {
        try
        {
            _reconciliationAvailable.Release();
        }
        catch (SemaphoreFullException)
        {
            // A reconciliation pass is pending or in progress; one follow-up pass captures all
            // intervening file-system events without queueing duplicate full-library scans.
        }
    }

    private async Task ReconciliationWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _reconciliationAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Before scanning, not after: a root added since the last pass must be watched and
                // scanned in the same pass, or its ROMs wait for the pass after next.
                SyncLibraryWatchers(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var reopenMetadataDeferred = Interlocked.Exchange(ref _reopenMetadataDeferred, 0) != 0;
                await ReconcileAsync(cancellationToken, reopenMetadataDeferred).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Game artwork reconciliation failed");
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken, bool reopenMetadataDeferred)
    {
            // GetStartupRecoveryWorkAsync re-reads and re-parses the entire catalog (25-35 MB at
            // 20k ROMs), so it only makes sense to pay for once, on the first (startup) pass. Later
            // passes -- now triggered by RefreshLibrary completions instead of a timer -- rely on
            // the ordinary per-candidate state already tracked in the catalog.
            IReadOnlyList<ArtworkCatalogWorkItem> recovery = Array.Empty<ArtworkCatalogWorkItem>();
            if (!_startupRecoveryDone)
            {
                recovery = await _catalog.GetStartupRecoveryWorkAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

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
                var deletable = orphaned.Where(IsManagedArtworkPath).ToList();
                if (deletable.Count > 0)
                {
                    // Deleting cached artwork is the one destructive thing reconciliation does and
                    // it was previously silent, which made a mass loss impossible to attribute.
                    _logger.LogWarning(
                        "Deleting {Count} orphaned game artwork files for library {LibraryId} (of {Orphaned} reported); first: {Sample}",
                        deletable.Count,
                        library.Id,
                        orphaned.Count,
                        string.Join(", ", deletable.Take(3)));
                }

                foreach (var path in deletable)
                {
                    try { File.Delete(path); } catch (Exception ex) { _logger.LogDebug(ex, "Removing orphaned game artwork {Path} failed", path); }
                }

                var repaired = await _catalog.RepairMissingArtifactsAsync(library.Id, cancellationToken).ConfigureAwait(false);
                if (repaired > 0)
                {
                    _logger.LogWarning(
                        "Reopened {Count} game artwork entries whose cached files were missing for library {LibraryId}",
                        repaired,
                        library.Id);
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

                    discovery = await _catalog.RecomputePreviewSelectionAsync(
                        library.Id,
                        candidates[0].SystemId,
                        inventoryGeneration,
                        cancellationToken).ConfigureAwait(false);
                    var selectionComplete = string.Equals(
                        discovery.PreviewSelectionGeneration,
                        inventoryGeneration,
                        StringComparison.Ordinal);
                    var previewQueued = selectionComplete;
                    foreach (var candidate in ordered)
                    {
                        var lane = previewQueued ? ArtworkLane.Bulk : ArtworkLane.Preview;
                        if (await QueueCandidateAsync(candidate, lane, cancellationToken, reopenMetadataDeferred).ConfigureAwait(false) &&
                            lane == ArtworkLane.Preview)
                        {
                            previewQueued = true;
                        }
                    }
                }
            }

            await EnqueueRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> QueueCandidateAsync(
        ArtworkCandidate candidate,
        ArtworkLane lane,
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
                return false;
            case ArtworkCatalogState.OriginalReady when !string.IsNullOrWhiteSpace(entry.OriginalPath):
                QueueThumbnail(candidate with { OriginalPath = entry.OriginalPath }, entry.RetryAfterUtc);
                return false;
            case ArtworkCatalogState.Missing:
                return false;
            case ArtworkCatalogState.MetadataDeferred:
                if (reopenMetadataDeferred)
                {
                    await _catalog.ReopenMetadataDeferredAsync(
                        candidate.Key,
                        candidate.SystemId,
                        candidate.GameId,
                        cancellationToken).ConfigureAwait(false);
                    return QueueRemote(candidate, lane, null);
                }
                return false;
            default:
                return QueueRemote(candidate, lane, entry.RetryAfterUtc);
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
                QueueRemote(candidate, ArtworkLane.Bulk, item.NotBeforeUtc);
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
                        await RefreshPreviewSelectionAsync(work.Candidate, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await _catalog.MarkOriginalReadyAsync(work.Candidate.Key, work.Candidate.SystemId, original, cancellationToken).ConfigureAwait(false);
                    QueueThumbnail(work.Candidate with { OriginalPath = original });
                    await RefreshPreviewSelectionAsync(work.Candidate, cancellationToken).ConfigureAwait(false);
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
        QueueRemote(work.Candidate, work.Lane, retryAfter);
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
            QueueRemote(work.Candidate, work.Lane, scheduled);
        }
        else
        {
            _logger.LogDebug(
                "Stopped automatic metadata retries for {GameId} after the {Hours}-hour retry window",
                work.Candidate.GameId,
                MetadataRetryWindow.TotalHours);
            await RefreshPreviewSelectionAsync(work.Candidate, cancellationToken).ConfigureAwait(false);
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

                if (!IsCurrentThumbnailWork(work))
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
                        CompleteThumbnail(work);
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
                    CompleteThumbnail(work);
                    QueueThumbnail(work.Candidate, retryAfter);
                    retryQueued = true;
                }
                finally
                {
                    if (!retryQueued)
                    {
                        CompleteThumbnail(work);
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

    private async Task RefreshPreviewSelectionAsync(ArtworkCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.Kind != GameThumbService.ThumbKind.Boxart)
        {
            return;
        }

        var system = await _catalog.GetSystemAsync(
            candidate.Key.LibraryId,
            candidate.SystemId,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(system.InventoryGeneration))
        {
            return;
        }

        var selection = await _catalog.RecomputePreviewSelectionAsync(
            candidate.Key.LibraryId,
            candidate.SystemId,
            system.InventoryGeneration,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(selection.NextUnresolvedGameId))
        {
            var expected = await _catalog.GetByGameIdAsync(
                candidate.Key.LibraryId,
                selection.NextUnresolvedGameId,
                "boxart",
                cancellationToken).ConfigureAwait(false);
            var nextUnresolved = expected == null
                ? null
                : TryCreatePersistedCandidate(
                    candidate.Key.LibraryId,
                    selection.NextUnresolvedGameId,
                    GameThumbService.ThumbKind.Boxart,
                    expected.Key.StorageKey);
            if (nextUnresolved != null)
            {
                await QueueCandidateAsync(nextUnresolved, ArtworkLane.Preview, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A missing source or changed fingerprint means the persisted inventory is stale; only
            // a normal reconciliation should replace that inventory and its deterministic order.
            DebounceLibraryFileSystemReconciliation();
        }
    }

    private bool QueueRemote(ArtworkCandidate candidate, ArtworkLane lane, DateTimeOffset? notBefore)
    {
        var work = ArtworkWork.Remote(candidate, lane);
        lock (_remoteQueueGate)
        {
            if (_queued.TryGetValue(work.Identity, out var existing))
            {
                if (existing.Started || !IsHigherPriority(lane, existing.Lane))
                {
                    return true;
                }

                existing = existing with { Lane = lane, Version = NextRemoteQueueVersion() };
                _queued[work.Identity] = existing;
                work = work with { QueueVersion = existing.Version };
            }
            else
            {
                if (_queued.Count >= MaxQueuedBestEffortRemoteWork &&
                    !MakeRoomForRemoteUnsafe(lane) &&
                    lane != ArtworkLane.Preview)
                {
                    return false;
                }

                var queued = new QueuedRemoteWork(lane, NextRemoteQueueVersion(), false);
                _queued.Add(work.Identity, queued);
                work = work with { QueueVersion = queued.Version };
            }
        }

        if (notBefore is { } retryAfter && retryAfter > DateTimeOffset.UtcNow)
        {
            _ = DelayQueueRemoteAsync(work, retryAfter, _stop?.Token ?? CancellationToken.None);
            return true;
        }

        EnqueueRemoteIfCurrent(work);
        return true;
    }

    private async Task DelayQueueRemoteAsync(ArtworkWork work, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(notBefore - DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            EnqueueRemoteIfCurrent(work);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteRemote(work);
        }
    }

    private void EnqueueRemoteIfCurrent(ArtworkWork work)
    {
        lock (_remoteQueueGate)
        {
            if (_queued.TryGetValue(work.Identity, out var queued) &&
                queued.Version == work.QueueVersion &&
                !queued.Started)
            {
                _remote.Enqueue(work);
            }
        }
    }

    private void QueueThumbnail(ArtworkCandidate candidate, DateTimeOffset? notBefore = null)
    {
        lock (_thumbnailQueueGate)
        {
            var work = ArtworkWork.Thumbnail(candidate) with
            {
                QueueVersion = Interlocked.Increment(ref _nextThumbnailQueueVersion),
            };
            if (_thumbnailQueued.Count >= MaxQueuedThumbnailWork ||
                !_thumbnailQueued.TryAdd(work.Identity, work.QueueVersion))
            {
                return;
            }

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
            lock (_thumbnailQueueGate)
            {
                if (_thumbnailQueued.TryGetValue(work.Identity, out var version) &&
                    version == work.QueueVersion)
                {
                    _thumbnail.Enqueue(work);
                    _thumbnailAvailable.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop cleanup owns removal; a late continuation must not remove a restarted lifetime's work.
        }
    }

    private bool IsCurrentThumbnailWork(ArtworkWork work)
    {
        lock (_thumbnailQueueGate)
        {
            return _thumbnailQueued.TryGetValue(work.Identity, out var version) &&
                version == work.QueueVersion;
        }
    }

    private void CompleteThumbnail(ArtworkWork work)
    {
        lock (_thumbnailQueueGate)
        {
            if (_thumbnailQueued.TryGetValue(work.Identity, out var version) &&
                version == work.QueueVersion)
            {
                _thumbnailQueued.TryRemove(work.Identity, out _);
            }
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

        var evictable = _queued.FirstOrDefault(pair => !pair.Value.Started && pair.Value.Lane == ArtworkLane.Bulk);
        if (string.IsNullOrEmpty(evictable.Key))
        {
            return false;
        }

        _queued.Remove(evictable.Key);
        return true;
    }

    private static bool IsHigherPriority(ArtworkLane candidate, ArtworkLane existing) => candidate < existing;

    private int NextRemoteQueueVersion() => Interlocked.Increment(ref _nextRemoteQueueVersion);

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
        if (library == null)
        {
            return null;
        }

        var game = _games.GetGames(library.Id, null)
            .FirstOrDefault(item => string.Equals(item.Id, gameId, StringComparison.Ordinal));
        return game == null ? null : TryCreateCandidate(library, game, kind);
    }

    private ArtworkCandidate? TryCreateCandidate(GameLibrary library, GameSummary game, GameThumbService.ThumbKind kind)
    {
        var source = _games.ResolveThumbSource(library.Id, game.Id);
        return source == null ? null : TryCreateCandidate(library, game.Id, source, kind);
    }

    private static ArtworkCandidate? TryCreateCandidate(
        GameLibrary library,
        string gameId,
        GameThumbSource source,
        GameThumbService.ThumbKind kind)
    {
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
        return new ArtworkCandidate(gameId, source.SystemName, kind, source, key, null);
    }

    private ArtworkCandidate? TryCreatePersistedCandidate(
        string libraryId,
        string gameId,
        GameThumbService.ThumbKind kind,
        string expectedStorageKey)
    {
        var library = _games.GetGameLibraries().FirstOrDefault(item =>
            string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        var source = _games.ResolveThumbSource(libraryId, gameId);
        var candidate = library == null || source == null
            ? null
            : TryCreateCandidate(library, gameId, source, kind);
        return candidate != null &&
            string.Equals(candidate.Key.StorageKey, expectedStorageKey, StringComparison.Ordinal)
                ? candidate
                : null;
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

    internal sealed record ArtworkWork(ArtworkCandidate Candidate, ArtworkLane Lane, bool IsThumbnail, int QueueVersion = 0)
    {
        public string Identity => IsThumbnail
            ? "thumbnail:" + Candidate.Key.StorageKey
            : "remote:" + Candidate.Key.StorageKey;
        public static ArtworkWork Remote(ArtworkCandidate candidate, ArtworkLane lane) => new(candidate, lane, false);
        public static ArtworkWork Thumbnail(ArtworkCandidate candidate) => new(candidate, ArtworkLane.Interactive, true);
    }

    private sealed record QueuedRemoteWork(ArtworkLane Lane, int Version, bool Started);

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
            Available.Release();
        }
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

    public void Clear()
    {
        lock (_gate)
        {
            _interactive.Clear();
            _bulk.Clear();
            _preview.Clear();
            _previewSystems.Clear();
            _nonBulkSelections = 0;
            while (Available.Wait(0))
            {
                // Cleared work must not leave stale permits for workers created by a later start.
            }
        }
    }
}
