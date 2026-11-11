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
    private readonly ConcurrentDictionary<Guid, byte> _updating = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _queuedDesiredDates = new();
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
        var targets = _ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled);
        foreach (var (itemId, desiredUtc) in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                continue;
            }

            if (!_updating.ContainsKey(itemId) && Math.Abs((item.DateCreated.ToUniversalTime() - desiredUtc).TotalSeconds) <= 1)
            {
                continue;
            }

            await UpdateItemDateAsync(item, desiredUtc, cancellationToken).ConfigureAwait(false);
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

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item == null || item.IsVirtualItem || _updating.ContainsKey(item.Id))
            {
                return;
            }

            if (ToCandidate(item) is not { } candidate)
            {
                return;
            }

            var hideUpgrades = GetHideUpgradesCached();
            var stopToken = _stopCts.Token;
            if (_ledger.ObserveArrival(candidate, hideUpgrades, out var desiredUtc, id => _libraryManager.GetItemById(id) != null) && desiredUtc.HasValue && !stopToken.IsCancellationRequested)
            {
                var updateTask = Task.Run(() => UpdateItemDateAsync(item, desiredUtc.Value, stopToken), CancellationToken.None);
                _pendingArrivalUpdates.TryAdd(updateTask, 0);
                _ = updateTask.ContinueWith(t => _pendingArrivalUpdates.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media upgrade arrival check failed");
        }
    }

    private async Task UpdateItemDateAsync(BaseItem item, DateTime desiredUtc, CancellationToken cancellationToken)
    {
        var normalizedDesiredUtc = DateTime.SpecifyKind(desiredUtc, DateTimeKind.Utc);
        _queuedDesiredDates[item.Id] = normalizedDesiredUtc;
        if (!_updating.TryAdd(item.Id, 0))
        {
            return;
        }

        try
        {
            while (_queuedDesiredDates.TryRemove(item.Id, out var nextUtc))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (Math.Abs((item.DateCreated.ToUniversalTime() - nextUtc).TotalSeconds) <= 1)
                {
                    continue;
                }

                item.DateCreated = nextUtc;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update DateCreated for upgraded item {ItemId}", item.Id);
        }
        finally
        {
            _updating.TryRemove(item.Id, out _);
            if (!cancellationToken.IsCancellationRequested && _queuedDesiredDates.TryRemove(item.Id, out var trailingUtc))
            {
                await UpdateItemDateAsync(item, trailingUtc, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

