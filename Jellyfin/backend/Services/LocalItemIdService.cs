using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moonfin.CustomRows;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Matches the titles of outside lists (custom rows and IMDb charts) to the library, so a client
/// can show the server's own artwork for a title the library has and link to it directly, rather
/// than waiting for Seerr to report it as available.
///
/// The match is made when a list is fetched and kept in the list caches, which every user
/// shares, so it holds the item regardless of who may see it. Whether the asking user can is
/// checked on the way out. Titles added to the library later are matched as they arrive, and the
/// custom lists sync task matches every cached list again, which also covers caches written
/// before lists carried matches.
/// </summary>
public class LocalItemIdService
{
    private readonly ILibraryManager _libraryManager;
    private readonly CustomRowCacheService _customRowCache;
    private readonly ImdbListsCacheService _imdbCache;
    private readonly IServiceProvider _services;
    private readonly ILogger<LocalItemIdService> _logger;

    public LocalItemIdService(
        ILibraryManager libraryManager,
        CustomRowCacheService customRowCache,
        ImdbListsCacheService imdbCache,
        IServiceProvider services,
        ILogger<LocalItemIdService> logger)
    {
        _libraryManager = libraryManager;
        _customRowCache = customRowCache;
        _imdbCache = imdbCache;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Sets <see cref="CustomRowItem.LocalItemId"/> on each item to its library copy, or clears it
    /// when the library has none. A title that shows up more than once is looked up once.
    /// </summary>
    public void Resolve(IEnumerable<CustomRowItem> items)
    {
        var found = new Dictionary<TitleIds, string?>();
        foreach (var item in items)
        {
            var ids = IdsOf(item);
            if (ids.IsEmpty)
            {
                item.LocalItemId = null;
                continue;
            }

            if (!found.TryGetValue(ids, out var localId))
            {
                localId = FindInLibrary(ids);
                found[ids] = localId;
            }

            item.LocalItemId = localId;
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

        await _customRowCache.FlushAsync().ConfigureAwait(false);
        await _imdbCache.FlushAsync().ConfigureAwait(false);
        _logger.LogInformation("Matched {Matched} of {Total} cached list titles to the library", matched, total);
    }

    /// <summary>
    /// Points cached titles at <paramref name="libraryItem"/> when they are the same title and
    /// don't already point at an item that still exists. Returns how many entries changed, so
    /// the caller knows whether the caches need saving.
    /// </summary>
    public int MatchArrival(BaseItem libraryItem)
    {
        var ids = IdsOf(libraryItem);
        if (ids == null || ids.IsEmpty)
        {
            return 0;
        }

        var localId = libraryItem.Id.ToString("N");
        var changed = 0;
        foreach (var list in _customRowCache.AllItemLists().Concat(_imdbCache.AllItemLists()))
        {
            foreach (var item in list)
            {
                if (item.LocalItemId == localId || !IdsOf(item).Matches(ids))
                {
                    continue;
                }

                if (item.LocalItemId != null && Exists(item.LocalItemId))
                {
                    continue;
                }

                item.LocalItemId = localId;
                changed++;
            }
        }

        return changed;
    }

    /// <summary>Saves both list caches after <see cref="MatchArrival"/> changed them.</summary>
    public async Task FlushAsync()
    {
        await _customRowCache.FlushAsync().ConfigureAwait(false);
        await _imdbCache.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The items as the endpoint returns them. Without <paramref name="includeLocalItemIds"/>
    /// no item carries a local id, so older clients get exactly the response they always did.
    /// With it, an item carries its local id only when <paramref name="userId"/> can see that
    /// item. The cached items themselves are never changed.
    /// </summary>
    public List<CustomRowItem> ForResponse(List<CustomRowItem> items, Guid userId, bool includeLocalItemIds)
    {
        if (!items.Any(item => item.LocalItemId != null))
        {
            return items;
        }

        if (!includeLocalItemIds)
        {
            return items.Select(item => item.WithLocalItemId(null)).ToList();
        }

        object? user;
        try
        {
            user = UserReflection.ResolveUser(_services, userId);
        }
        catch (Exception ex)
        {
            // Newer servers read the user from the database, which can fail. The titles then
            // go out without ids, as for any user that can't be resolved.
            _logger.LogDebug(ex, "User lookup failed for user {UserId}", userId);
            user = null;
        }

        var visible = new Dictionary<string, bool>();
        return items.Select(item =>
        {
            var localId = item.LocalItemId;
            if (localId == null)
            {
                return item;
            }

            if (!visible.TryGetValue(localId, out var canSee))
            {
                canSee = IsVisibleTo(localId, user);
                visible[localId] = canSee;
            }

            return item.WithLocalItemId(canSee ? localId : null);
        }).ToList();
    }

    internal static TitleIds IdsOf(CustomRowItem item) => new(
        TitleIds.IsSeriesType(item.Type),
        item.ProviderIds?.Tmdb,
        item.ProviderIds?.Imdb,
        item.ProviderIds?.Tvdb);

    /// <summary>The ids of a library movie or series, or null for any other kind of item.</summary>
    internal static TitleIds? IdsOf(BaseItem item) => item switch
    {
        Series => new TitleIds(
            true,
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Imdb),
            item.GetProviderId(MetadataProvider.Tvdb)),
        Movie => new TitleIds(
            false,
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Imdb),
            item.GetProviderId(MetadataProvider.Tvdb)),
        _ => null
    };

    private string? FindInLibrary(TitleIds ids)
    {
        // One query per provider, best first. Jellyfin 10.10 reads only the first entry of
        // HasAnyProviderId, so asking for all of them at once could miss a title the library
        // only knows by a later one.
        var wanted = new List<KeyValuePair<string, string>>();
        if (ids.Tmdb != null) wanted.Add(new(MetadataProvider.Tmdb.ToString(), ids.Tmdb));
        if (ids.Imdb != null) wanted.Add(new(MetadataProvider.Imdb.ToString(), ids.Imdb));
        if (ids.Tvdb != null) wanted.Add(new(MetadataProvider.Tvdb.ToString(), ids.Tvdb));

        try
        {
            foreach (var provider in wanted)
            {
                var best = BestCandidate(ids, provider);
                if (best != null)
                {
                    return best.Id.ToString("N");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library lookup failed for list title (tmdb {Tmdb}, imdb {Imdb})", ids.Tmdb, ids.Imdb);
            return null;
        }
    }

    private BaseItem? BestCandidate(TitleIds ids, KeyValuePair<string, string> provider)
    {
        // Jellyfin 10.10 matches the value as a substring (603 also finds 6030), so every
        // candidate is read, without a limit that could cut off the exact one, and checked
        // against the exact ids. GetItemList changed its return type in Jellyfin 10.11, so a
        // compiled call only binds on the version it was built against. GetItemsResult has the
        // same signature everywhere and answers the same question.
        var candidates = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { [provider.Key] = provider.Value },
            IncludeItemTypes = [ids.Series ? BaseItemKind.Series : BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true,
            EnableTotalRecordCount = false
        }).Items;

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

        return best;
    }

    private bool Exists(string localId)
    {
        try
        {
            return Guid.TryParse(localId, out var id) && _libraryManager.GetItemById(id) != null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lookup failed for library item {ItemId}", localId);
            return false;
        }
    }

    // Fails closed: an id that doesn't parse, an item that's gone or a user that can't be
    // resolved all leave the title as the outside entry it was.
    private bool IsVisibleTo(string localId, object? user)
    {
        if (user == null || !Guid.TryParse(localId, out var id))
        {
            return false;
        }

        try
        {
            var item = _libraryManager.GetItemById(id);
            return item != null && UserReflection.IsVisibleStandaloneTo(item, user);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Visibility check failed for library item {ItemId}", localId);
            return false;
        }
    }
}
