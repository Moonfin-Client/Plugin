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
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;

        _ = Task.Run(async () =>
        {
            try
            {
                SeedFromLibrary();
                if (_settings.IsAnyHideUpgradesEnabled())
                {
                    await ApplyUpgradeDatesAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Media upgrade ledger initial seed failed");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
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

            if (Math.Abs((item.DateCreated.ToUniversalTime() - desiredUtc).TotalSeconds) <= 1)
            {
                continue;
            }

            await UpdateItemDateAsync(item, desiredUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task OnUpgradePreferenceChangedAsync(bool hideUpgradesEnabled)
    {
        Volatile.Write(ref _hideUpgradesCache, hideUpgradesEnabled ? 1 : 0);
        Interlocked.Exchange(ref _hideUpgradesCachedAtTicks, Environment.TickCount64);
        return ApplyUpgradeDatesAsync(hideUpgradesEnabled, CancellationToken.None);
    }

    private bool GetHideUpgradesCached()
    {
        var now = Environment.TickCount64;
        var cached = Volatile.Read(ref _hideUpgradesCache);
        if (cached != -1 && now - Interlocked.Read(ref _hideUpgradesCachedAtTicks) < 30_000)
        {
            return cached == 1;
        }

        var enabled = _settings.IsAnyHideUpgradesEnabled();
        Volatile.Write(ref _hideUpgradesCache, enabled ? 1 : 0);
        Interlocked.Exchange(ref _hideUpgradesCachedAtTicks, now);
        return enabled;
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
            if (_ledger.ObserveArrival(candidate, hideUpgrades, out var desiredUtc) && desiredUtc.HasValue)
            {
                _ = Task.Run(() => UpdateItemDateAsync(item, desiredUtc.Value, CancellationToken.None));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Media upgrade arrival check failed");
        }
    }

    private async Task UpdateItemDateAsync(BaseItem item, DateTime desiredUtc, CancellationToken cancellationToken)
    {
        if (!_updating.TryAdd(item.Id, 0))
        {
            return;
        }

        try
        {
            item.DateCreated = DateTime.SpecifyKind(desiredUtc, DateTimeKind.Utc);
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update DateCreated for upgraded item {ItemId}", item.Id);
        }
        finally
        {
            _updating.TryRemove(item.Id, out _);
        }
    }
}

