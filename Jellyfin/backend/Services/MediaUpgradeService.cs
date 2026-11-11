using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.Upgrades;

namespace Moonfin.Server.Services;

/// <summary>
/// Tracks initial import timestamps and file replacement (upgrade) timestamps for Movies and
/// Episodes in Jellyfin, restoring original <c>DateCreated</c> values when "Hide Upgrades" is
/// enabled and restoring upgrade <c>DateCreated</c> values when disabled.
/// </summary>
public sealed class MediaUpgradeService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly MoonfinSettingsService _settings;
    private readonly MediaUpgradeLedger _ledger;
    private readonly ILogger<MediaUpgradeService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemWriteGates = new();
    private readonly ConcurrentDictionary<Guid, int> _selfWriteDepth = new();
    private readonly ConcurrentDictionary<Task, byte> _pendingArrivalUpdates = new();
    private CancellationTokenSource _stopCts = new();
    private Task _startupTask = Task.CompletedTask;
    private readonly object _hideUpgradesCacheLock = new();
    private long _hideUpgradesCacheGeneration;
    private int _hideUpgradesCache = -1;
    private long _hideUpgradesCachedAtTicks;

    public MediaUpgradeService(
        ILibraryManager libraryManager,
        MoonfinSettingsService settings,
        MediaUpgradeLedger ledger,
        ILogger<MediaUpgradeService> logger)
    {
        _libraryManager = libraryManager;
        _settings = settings;
        _ledger = ledger;
        _logger = logger;
        _settings.UpgradePreferenceChanged = OnUpgradePreferenceChangedAsync;
    }

    public MediaUpgradeLedger Ledger => _ledger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stopCts.IsCancellationRequested)
        {
            _stopCts.Dispose();
            _stopCts = new CancellationTokenSource();
        }

        _settings.UpgradePreferenceChanged = OnUpgradePreferenceChangedAsync;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;

        var stopToken = _stopCts.Token;
        _startupTask = Task.Run(async () =>
        {
            try
            {
                if (stopToken.IsCancellationRequested)
                {
                    return;
                }

                SeedFromLibrary();
                if (!stopToken.IsCancellationRequested)
                {
                    await _settings.ScheduleUpgradePreferenceSyncIfNeeded(Guid.Empty, force: true).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Media upgrade ledger initial seed failed");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;

        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var pending = _pendingArrivalUpdates.Keys.ToArray();
        if (pending.Length > 0)
        {
            var whenAll = Task.WhenAll(pending);
            var completed = await Task.WhenAny(whenAll, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed == whenAll)
            {
                await whenAll.ConfigureAwait(false);
            }
        }

        await _settings.StopUpgradePreferenceSyncAsync(cancellationToken).ConfigureAwait(false);

        if (!_startupTask.IsCompleted)
        {
            var completed = await Task.WhenAny(_startupTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed == _startupTask)
            {
                await _startupTask.ConfigureAwait(false);
            }
        }
    }

    public DateTime EffectiveDateCreated(Guid itemId, DateTime fallbackUtc, bool hideUpgradesEnabled) =>
        _ledger.EffectiveDateCreated(itemId, fallbackUtc, hideUpgradesEnabled);

    public async Task ApplyUpgradeDatesAsync(bool hideUpgradesEnabled, CancellationToken cancellationToken = default)
    {
        lock (_hideUpgradesCacheLock)
        {
            _hideUpgradesCacheGeneration++;
            _hideUpgradesCache = hideUpgradesEnabled ? 1 : 0;
            _hideUpgradesCachedAtTicks = Environment.TickCount64;
        }

        var targets = _ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled);
        var failedCount = 0;
        foreach (var (itemId, _) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                continue;
            }

            if (!await UpdateItemDateAsync(item, cancellationToken).ConfigureAwait(false))
            {
                failedCount++;
            }
        }

        if (failedCount > 0)
        {
            throw new InvalidOperationException($"Failed to update DateCreated for {failedCount} upgraded item(s).");
        }
    }

    private Task OnUpgradePreferenceChangedAsync(bool hideUpgradesEnabled, CancellationToken cancellationToken)
    {
        lock (_hideUpgradesCacheLock)
        {
            _hideUpgradesCacheGeneration++;
            _hideUpgradesCache = hideUpgradesEnabled ? 1 : 0;
            _hideUpgradesCachedAtTicks = Environment.TickCount64;
        }

        return ApplyUpgradeDatesAsync(hideUpgradesEnabled, cancellationToken);
    }

    private bool GetHideUpgradesCached()
    {
        long generationBeforeScan;
        lock (_hideUpgradesCacheLock)
        {
            var now = Environment.TickCount64;
            if (_hideUpgradesCache != -1 && now - _hideUpgradesCachedAtTicks < 30_000)
            {
                return _hideUpgradesCache == 1;
            }

            generationBeforeScan = _hideUpgradesCacheGeneration;
        }

        var enabled = _settings.IsAnyHideUpgradesEnabled();
        lock (_hideUpgradesCacheLock)
        {
            if (_hideUpgradesCacheGeneration == generationBeforeScan)
            {
                _hideUpgradesCache = enabled ? 1 : 0;
                _hideUpgradesCachedAtTicks = Environment.TickCount64;
                return enabled;
            }

            return _hideUpgradesCache != -1 ? _hideUpgradesCache == 1 : enabled;
        }
    }

    internal void SeedFromLibrary()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            IsVirtualItem = false,
            Recursive = true,
            EnableTotalRecordCount = false
        };

        var items = _libraryManager.GetItemsResult(query).Items;
        var candidates = new List<MediaUpgradeCandidate>(items.Count);
        foreach (var item in items)
        {
            if (ToCandidate(item) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        _ledger.Seed(candidates);
    }

    internal static MediaUpgradeCandidate? ToCandidate(BaseItem? item)
    {
        if (item == null || item.IsVirtualItem || item.Id == Guid.Empty)
        {
            return null;
        }

        switch (item)
        {
            case Movie movie:
                return new MediaUpgradeCandidate
                {
                    ItemId = movie.Id,
                    IsEpisode = false,
                    TvdbId = movie.GetProviderId(MetadataProvider.Tvdb),
                    TmdbId = movie.GetProviderId(MetadataProvider.Tmdb),
                    ImdbId = movie.GetProviderId(MetadataProvider.Imdb),
                    Name = movie.Name,
                    ProductionYear = movie.ProductionYear,
                    DateCreatedUtc = movie.DateCreated.ToUniversalTime()
                };

            case Episode episode:
                var series = episode.Series;
                return new MediaUpgradeCandidate
                {
                    ItemId = episode.Id,
                    IsEpisode = true,
                    TvdbId = series?.GetProviderId(MetadataProvider.Tvdb),
                    TmdbId = series?.GetProviderId(MetadataProvider.Tmdb),
                    ImdbId = series?.GetProviderId(MetadataProvider.Imdb),
                    SeriesId = episode.SeriesId != Guid.Empty ? episode.SeriesId : series?.Id,
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    Name = series?.Name ?? episode.SeriesName,
                    DateCreatedUtc = episode.DateCreated.ToUniversalTime()
                };

            default:
                return null;
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (ToCandidate(e.Item) is { } candidate)
            {
                _ledger.RecordRemoved(candidate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media upgrade removal check failed");
        }
    }

    internal Action? BeforeObserveArrivalForTesting { get; set; }
    internal Func<Task>? BeforeQueuedArrivalStartForTesting { get; set; }
    internal Func<BaseItem, DateTime, CancellationToken, Task>? PersistItemDateForTesting { get; set; }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item == null || item.IsVirtualItem || (_selfWriteDepth.TryGetValue(item.Id, out var depth) && depth > 0))
            {
                return;
            }

            if (ToCandidate(item) is not { } candidate)
            {
                return;
            }

            var hideUpgrades = GetHideUpgradesCached();
            BeforeObserveArrivalForTesting?.Invoke();
            var stopToken = _stopCts.Token;
            if (_ledger.ObserveArrival(candidate, hideUpgrades, out _, id => _libraryManager.GetItemById(id) != null) && !stopToken.IsCancellationRequested)
            {
                var updateTask = Task.Run(async () =>
                {
                    if (BeforeQueuedArrivalStartForTesting != null)
                    {
                        await BeforeQueuedArrivalStartForTesting().ConfigureAwait(false);
                    }

                    await UpdateItemDateAsync(item, stopToken).ConfigureAwait(false);
                }, CancellationToken.None);
                _pendingArrivalUpdates.TryAdd(updateTask, 0);
                _ = updateTask.ContinueWith(t => _pendingArrivalUpdates.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media upgrade arrival check failed");
        }
    }

    internal void HandleItemChangedForTesting(BaseItem item) => OnItemChanged(this, new ItemChangeEventArgs { Item = item });

    internal Task WaitForPendingArrivalUpdatesForTestingAsync()
    {
        var pending = _pendingArrivalUpdates.Keys.ToArray();
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    internal async Task<bool> UpdateItemDateAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var gate = _itemWriteGates.GetOrAdd(item.Id, static _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var hideUpgrades = GetHideUpgradesCached();
            var desiredUtc = _ledger.GetDesiredDateForItem(item.Id, hideUpgrades);
            if (!desiredUtc.HasValue)
            {
                return true;
            }

            var normalizedDesiredUtc = DateTime.SpecifyKind(desiredUtc.Value, DateTimeKind.Utc);
            if (Math.Abs((item.DateCreated.ToUniversalTime() - normalizedDesiredUtc).TotalSeconds) <= 1)
            {
                return true;
            }

            _selfWriteDepth.AddOrUpdate(item.Id, 1, static (_, c) => c + 1);
            var previousDate = item.DateCreated;
            try
            {
                item.DateCreated = normalizedDesiredUtc;
                try
                {
                    if (PersistItemDateForTesting != null)
                    {
                        await PersistItemDateForTesting(item, normalizedDesiredUtc, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    item.DateCreated = previousDate;
                    throw;
                }
            }
            finally
            {
                _selfWriteDepth.AddOrUpdate(item.Id, 0, static (_, c) => Math.Max(0, c - 1));
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update DateCreated for upgraded item {ItemId}", item.Id);
            return false;
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }
}

