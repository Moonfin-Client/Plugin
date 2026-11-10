using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Moonfin.CustomRows;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Matches the titles of outside lists (custom rows and IMDb charts) to the library, so a
    /// client can show the server's own artwork for a title the library has and link to it
    /// directly, rather than waiting for Seerr to report it as available.
    ///
    /// The match is made when a list is fetched and kept in the list caches, which every user
    /// shares, so it holds the item regardless of who may see it. Whether the asking user can is
    /// checked on the way out. Titles added to the library later are matched as they arrive, and
    /// the IMDb lists sync task matches every cached list again, which also covers caches written
    /// before lists carried matches.
    /// </summary>
    public class LocalItemIdService
    {
        // Emby takes many provider ids in one query, so a list is looked up in a few batches.
        private const int ProviderIdChunk = 100;

        private readonly ILibraryManager _libraryManager;
        private readonly CustomRowCacheService _customRowCache;
        private readonly ImdbListsCacheService _imdbCache;
        private readonly ILogger _logger;

        public LocalItemIdService(
            ILibraryManager libraryManager,
            CustomRowCacheService customRowCache,
            ImdbListsCacheService imdbCache,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _customRowCache = customRowCache;
            _imdbCache = imdbCache;
            _logger = logger;
        }

        /// <summary>
        /// Sets <see cref="CustomRowItem.LocalItemId"/> on each item to its library copy, or clears
        /// it when the library has none.
        /// </summary>
        public void Resolve(IEnumerable<CustomRowItem> items)
        {
            var list = items.ToList();
            var wanted = list.Select(IdsOf).Where(ids => !ids.IsEmpty).Distinct().ToList();
            var found = new Dictionary<TitleIds, string?>();
            foreach (var series in new[] { false, true })
            {
                var ofKind = wanted.Where(ids => ids.Series == series).ToList();
                if (ofKind.Count == 0) continue;

                var candidates = FindCandidates(series, ofKind);
                foreach (var ids in ofKind)
                    found[ids] = Best(ids, candidates);
            }

            foreach (var item in list)
            {
                var ids = IdsOf(item);
                item.LocalItemId = !ids.IsEmpty && found.TryGetValue(ids, out var localId) ? localId : null;
            }
        }

        /// <summary>Matches every cached custom row and IMDb chart against the library again and saves both caches.</summary>
        public async Task ResolveCachedAsync(CancellationToken cancellationToken)
        {
            var lists = _customRowCache.AllItemLists().Concat(_imdbCache.AllItemLists()).ToList();
            var matched = 0;
            var total = 0;
            foreach (var list in lists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Resolve(list);
                total += list.Count;
                matched += list.Count(item => item.LocalItemId != null);
            }

            await FlushAsync().ConfigureAwait(false);
            _logger.Info("{0}", "Matched " + matched + " of " + total + " cached list titles to the library");
        }

        /// <summary>
        /// Points cached titles at <paramref name="libraryItem"/> when they are the same title and
        /// don't already point at an item that still exists. Returns how many entries changed, so
        /// the caller knows whether the caches need saving.
        /// </summary>
        public int MatchArrival(BaseItem libraryItem)
        {
            var ids = IdsOf(libraryItem);
            if (ids == null || ids.IsEmpty) return 0;

            var localId = IdOf(libraryItem);
            var changed = 0;
            foreach (var list in _customRowCache.AllItemLists().Concat(_imdbCache.AllItemLists()))
            {
                foreach (var item in list)
                {
                    if (item.LocalItemId == localId || !IdsOf(item).Matches(ids)) continue;
                    if (item.LocalItemId != null && FindItem(item.LocalItemId) != null) continue;

                    item.LocalItemId = localId;
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>Saves both list caches.</summary>
        public async Task FlushAsync()
        {
            await _customRowCache.FlushAsync().ConfigureAwait(false);
            await _imdbCache.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The items as the endpoint returns them. Without <paramref name="includeLocalItemIds"/>
        /// no item carries a local id, so older clients get exactly the response they always did.
        /// With it, an item carries its local id only when <paramref name="user"/> can see that
        /// item. The cached items themselves are never changed.
        /// </summary>
        public List<CustomRowItem> ForResponse(List<CustomRowItem> items, User? user, bool includeLocalItemIds)
        {
            if (!items.Any(item => item.LocalItemId != null)) return items;
            if (!includeLocalItemIds) return items.Select(item => item.WithLocalItemId(null)).ToList();

            var visible = new Dictionary<string, bool>();
            return items.Select(item =>
            {
                var localId = item.LocalItemId;
                if (localId == null) return item;

                if (!visible.TryGetValue(localId, out var canSee))
                {
                    canSee = IsVisibleTo(localId, user);
                    visible[localId] = canSee;
                }

                return item.WithLocalItemId(canSee ? localId : null);
            }).ToList();
        }

        internal static TitleIds IdsOf(CustomRowItem item) => new TitleIds(
            TitleIds.IsSeriesType(item.Type),
            item.ProviderIds?.Tmdb,
            item.ProviderIds?.Imdb,
            item.ProviderIds?.Tvdb);

        /// <summary>The ids of a library movie or series, or null for any other kind of item.</summary>
        internal static TitleIds? IdsOf(BaseItem item)
        {
            if (!(item is Movie) && !(item is Series)) return null;
            return new TitleIds(item is Series, ProviderId(item, "Tmdb"), ProviderId(item, "Imdb"), ProviderId(item, "Tvdb"));
        }

        internal static string IdOf(BaseItem item) => item.InternalId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The candidate sharing the most specific id with <paramref name="ids"/>, or null.</summary>
        internal static string? Best(TitleIds ids, IEnumerable<BaseItem> candidates)
        {
            BaseItem? best = null;
            var bestRank = int.MaxValue;
            foreach (var candidate in candidates)
            {
                var candidateIds = IdsOf(candidate);
                var rank = candidateIds == null ? -1 : ids.MatchRank(candidateIds);
                if (rank >= 0 && rank < bestRank)
                {
                    best = candidate;
                    bestRank = rank;
                }
            }

            return best == null ? null : IdOf(best);
        }

        private List<BaseItem> FindCandidates(bool series, IReadOnlyCollection<TitleIds> titles)
        {
            var pairs = titles
                .SelectMany(ids => new[] { ("Tmdb", ids.Tmdb), ("Imdb", ids.Imdb), ("Tvdb", ids.Tvdb) })
                .Where(pair => pair.Item2 != null)
                .Select(pair => new KeyValuePair<string, string>(pair.Item1, pair.Item2!))
                .Distinct()
                .ToList();

            var found = new Dictionary<long, BaseItem>();
            for (var i = 0; i < pairs.Count; i += ProviderIdChunk)
            {
                try
                {
                    var query = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { series ? "Series" : "Movie" },
                        IsVirtualItem = false,
                        Recursive = true,
                        AnyProviderIdEquals = pairs.Skip(i).Take(ProviderIdChunk).ToList()
                    };

                    foreach (var item in _libraryManager.GetItemsResult(query).Items)
                        found[item.InternalId] = item;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Library lookup for list titles failed", ex);
                }
            }

            return found.Values.ToList();
        }

        private BaseItem? FindItem(string localId)
        {
            try
            {
                return long.TryParse(localId, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId)
                    ? _libraryManager.GetItemById(internalId)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        // Fails closed: an id that doesn't parse, an item that's gone or a missing user all
        // leave the title as the outside entry it was.
        private bool IsVisibleTo(string localId, User? user)
        {
            if (user == null) return false;
            try
            {
                var item = FindItem(localId);
                return item != null && item.IsVisibleStandalone(user);
            }
            catch
            {
                return false;
            }
        }

        private static string? ProviderId(BaseItem item, string name)
        {
            string? value = null;
            item.ProviderIds?.TryGetValue(name, out value);
            return value;
        }
    }

    /// <summary>
    /// Matches titles that land in the library to the outside lists already cached, so a list
    /// shows the library copy without waiting for its next fetch. Updates are watched too, since a
    /// fresh scan can add an item before its provider ids are read. A scan touches many items at
    /// once, so the caches are saved on a timer rather than once per item.
    /// </summary>
    public sealed class LocalItemArrivalNotifier : IDisposable
    {
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

        private readonly ILibraryManager _libraryManager;
        private readonly LocalItemIdService _localItems;
        private readonly ILogger _logger;
        private Timer? _flushTimer;
        private int _pendingChanges;
        private bool _started;

        public LocalItemArrivalNotifier(ILibraryManager libraryManager, LocalItemIdService localItems, ILogger logger)
        {
            _libraryManager = libraryManager;
            _localItems = localItems;
            _logger = logger;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _flushTimer = new Timer(_ => _ = FlushIfChangedAsync(), null, FlushInterval, FlushInterval);
        }

        public void Dispose()
        {
            if (!_started) return;
            _started = false;
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _flushTimer?.Dispose();
            _flushTimer = null;
            FlushIfChangedAsync().GetAwaiter().GetResult();
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null || item.IsVirtualItem || LocalItemIdService.IdsOf(item) == null) return;

                var changed = _localItems.MatchArrival(item);
                if (changed > 0) Interlocked.Add(ref _pendingChanges, changed);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Matching a library change to the cached lists failed", ex);
            }
        }

        private async Task FlushIfChangedAsync()
        {
            if (Interlocked.Exchange(ref _pendingChanges, 0) == 0) return;
            try
            {
                await _localItems.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Saving the cached lists after library matches failed", ex);
            }
        }
    }
}
