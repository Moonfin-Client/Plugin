using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Moonfin.Upgrades;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Tracks initial import timestamps and file replacement (upgrade) timestamps for Movies and
    /// Episodes in Emby, restoring original <c>DateCreated</c> values when "Hide Upgrades" is
    /// enabled and restoring upgrade <c>DateCreated</c> values when disabled.
    /// </summary>
    public sealed class MediaUpgradeService : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly MoonfinSettingsService _settings;
        private readonly MediaUpgradeLedger _ledger;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Guid, byte> _updating = new ConcurrentDictionary<Guid, byte>();
        private CancellationTokenSource _stopCts = new CancellationTokenSource();
        private Task _startupTask = Task.CompletedTask;
        private bool _started;
        private int _hideUpgradesCache = -1;
        private long _hideUpgradesCachedAtTicks;

        public MediaUpgradeService(
            ILibraryManager libraryManager,
            MoonfinSettingsService settings,
            MediaUpgradeLedger ledger,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _settings = settings;
            _ledger = ledger;
            _logger = logger;
            _settings.UpgradePreferenceChanged = OnUpgradePreferenceChangedAsync;
        }

        public MediaUpgradeLedger Ledger => _ledger;

        public void Start()
        {
            if (_started) return;
            _started = true;
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
                    if (stopToken.IsCancellationRequested) return;
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
                    _logger.Warn("Media upgrade ledger initial seed failed: " + ex.Message);
                }
            });
        }

        public void Dispose()
        {
            if (_started)
            {
                _started = false;
                _libraryManager.ItemAdded -= OnItemChanged;
                _libraryManager.ItemUpdated -= OnItemChanged;
                _libraryManager.ItemRemoved -= OnItemRemoved;
            }

            try
            {
                _stopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (!_settings.StopUpgradePreferenceSync(TimeSpan.FromSeconds(5)))
            {
                _logger.Warn("Media upgrade preference sync worker did not finish before shutdown");
            }

            if (!_startupTask.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger.Warn("Media upgrade startup task did not finish before shutdown");
            }

            _stopCts.Dispose();
        }

        public DateTime EffectiveDateCreated(Guid itemId, DateTime fallbackUtc, bool hideUpgradesEnabled) =>
            _ledger.EffectiveDateCreated(itemId, fallbackUtc, hideUpgradesEnabled);

        public void ApplyUpgradeDates(bool hideUpgradesEnabled, CancellationToken cancellationToken = default)
        {
            var targets = _ledger.GetDesiredDatesForUpgradedItems(hideUpgradesEnabled);
            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var item = _libraryManager.GetItemById(target.ItemId);
                if (item == null) continue;

                if (Math.Abs((item.DateCreated.UtcDateTime - target.DesiredDateUtc).TotalSeconds) <= 1)
                {
                    continue;
                }

                UpdateItemDate(item, target.DesiredDateUtc);
            }
        }

        private Task OnUpgradePreferenceChangedAsync(bool hideUpgradesEnabled, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _hideUpgradesCache, hideUpgradesEnabled ? 1 : 0);
            Interlocked.Exchange(ref _hideUpgradesCachedAtTicks, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            ApplyUpgradeDates(hideUpgradesEnabled, cancellationToken);
            return Task.CompletedTask;
        }

        private bool GetHideUpgradesCached()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsVirtualItem = false,
                Recursive = true
            };

            var items = _libraryManager.GetItemsResult(query).Items;
            var candidates = new List<MediaUpgradeCandidate>(items.Length);
            foreach (var item in items)
            {
                var candidate = ToCandidate(item);
                if (candidate != null)
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

            if (item is Movie movie)
            {
                return new MediaUpgradeCandidate
                {
                    ItemId = movie.Id,
                    IsEpisode = false,
                    TvdbId = ProviderId(movie, "Tvdb"),
                    TmdbId = ProviderId(movie, "Tmdb"),
                    ImdbId = ProviderId(movie, "Imdb"),
                    Name = movie.Name,
                    ProductionYear = movie.ProductionYear,
                    DateCreatedUtc = movie.DateCreated.UtcDateTime
                };
            }

            if (item is Episode episode)
            {
                var series = episode.Series;
                return new MediaUpgradeCandidate
                {
                    ItemId = episode.Id,
                    IsEpisode = true,
                    TvdbId = series != null ? ProviderId(series, "Tvdb") : null,
                    TmdbId = series != null ? ProviderId(series, "Tmdb") : null,
                    ImdbId = series != null ? ProviderId(series, "Imdb") : null,
                    SeriesId = series?.Id,
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    Name = series?.Name ?? episode.SeriesName,
                    DateCreatedUtc = episode.DateCreated.UtcDateTime
                };
            }

            return null;
        }

        private static string? ProviderId(BaseItem item, string name)
        {
            string? value = null;
            item.ProviderIds?.TryGetValue(name, out value);
            return value;
        }

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var candidate = ToCandidate(e.Item);
                if (candidate != null)
                {
                    _ledger.RecordRemoved(candidate);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Media upgrade removal check failed: " + ex.Message);
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

                var candidate = ToCandidate(item);
                if (candidate == null)
                {
                    return;
                }

                var hideUpgrades = GetHideUpgradesCached();
                if (_ledger.ObserveArrival(candidate, hideUpgrades, out var desiredUtc) && desiredUtc.HasValue)
                {
                    UpdateItemDate(item, desiredUtc.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Media upgrade arrival check failed: " + ex.Message);
            }
        }

        private void UpdateItemDate(BaseItem item, DateTime desiredUtc)
        {
            if (!_updating.TryAdd(item.Id, 0))
            {
                return;
            }

            try
            {
                var utc = DateTime.SpecifyKind(desiredUtc, DateTimeKind.Utc);
                item.DateCreated = new DateTimeOffset(utc);
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not update DateCreated for upgraded item " + item.Id + ": " + ex.Message);
            }
            finally
            {
                _updating.TryRemove(item.Id, out _);
            }
        }
    }
}

