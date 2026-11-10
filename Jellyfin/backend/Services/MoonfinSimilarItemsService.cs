using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moonfin.Recommendations;

namespace Moonfin.Server.Services;

/// <summary>
/// Gathers candidates from the library for Moonfin Recommends and ranks them with the shared
/// scoring.
/// </summary>
public class MoonfinSimilarItemsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MoonfinSimilarItemsService> _logger;

    private static readonly MethodInfo? _internalItemsQuerySetUser = typeof(InternalItemsQuery).GetMethod("SetUser", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo? _internalItemsQueryOrderBy = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.OrderBy));

    // GetPeople returned List<PersonInfo> on 10.10 and IReadOnlyList<PersonInfo> from 10.11 on, so
    // a direct call only binds on the version it was compiled against. Looking the method up by
    // name and parameters ignores the return type and works on every version. GetPeopleByItems is
    // Jellyfin 12 only and reads the whole batch in a single query.
    private static readonly MethodInfo? _libMgrGetPeople = typeof(ILibraryManager).GetMethod("GetPeople", [typeof(BaseItem)]);
    private static readonly MethodInfo? _libMgrGetPeopleByItems = typeof(ILibraryManager).GetMethod("GetPeopleByItems", [typeof(IReadOnlyList<Guid>)]);

    public MoonfinSimilarItemsService(
        ILibraryManager libraryManager,
        ILogger<MoonfinSimilarItemsService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem item,
        object? user,
        int? limit,
        IReadOnlyList<Guid>? excludeItemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var effectiveLimit = SimilarScoring.EffectiveLimit(limit);

        var isSeries = item is Series;
        if (item is not Movie && !isSeries)
        {
            return Task.FromResult<IReadOnlyList<BaseItem>>(Array.Empty<BaseItem>());
        }

        var seed = new SimilarSeed(Facts(item), Credits(GetItemPeople(item)));

        var excludedIds = new HashSet<Guid>(excludeItemIds ?? Array.Empty<Guid>())
        {
            item.Id
        };

        var targetType = isSeries ? BaseItemKind.Series : BaseItemKind.Movie;
        var candidatesMap = new Dictionary<Guid, BaseItem>();

        if (seed.Genres.Count > 0)
        {
            CollectCandidates(candidatesMap, excludedIds, targetType, user, q => q.Genres = seed.Genres.ToArray());
        }

        if (seed.Tags.Count > 0)
        {
            CollectCandidates(candidatesMap, excludedIds, targetType, user, q => q.Tags = seed.Tags.ToArray());
        }

        if (candidatesMap.Count < SimilarScoring.SparseCandidateThreshold)
        {
            CollectCandidates(candidatesMap, excludedIds, targetType, user, null, SimilarScoring.SparseFallbackCount);
        }

        var sorted = SimilarScoring.Rank(
            candidatesMap.Values,
            seed,
            effectiveLimit,
            Facts,
            i => i.PremiereDate,
            batch =>
            {
                var byItem = GetPeopleForItems(batch);
                return i => byItem.TryGetValue(i.Id, out var people) ? Credits(people) : null;
            },
            cancellationToken);

        return Task.FromResult<IReadOnlyList<BaseItem>>(sorted);
    }

    private static SimilarItemFacts Facts(BaseItem item) =>
        new(item.Name, item.Genres, item.Tags, item.Studios, item.ProductionYear, item.CommunityRating);

    private static IEnumerable<(string? Name, CreditRole Role)> Credits(IEnumerable<PersonInfo> people) =>
        people.Select(p => ((string?)p.Name, p.Type switch
        {
            PersonKind.Actor or PersonKind.GuestStar => CreditRole.Actor,
            PersonKind.Director => CreditRole.Director,
            PersonKind.Writer => CreditRole.Writer,
            _ => CreditRole.Other
        }));

    private void CollectCandidates(
        Dictionary<Guid, BaseItem> candidatesMap,
        HashSet<Guid> excludedIds,
        BaseItemKind targetType,
        object? user,
        Action<InternalItemsQuery>? applyFilter,
        int? limitOverride = null)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [targetType],
            IsVirtualItem = false,
            Recursive = true,
            Limit = limitOverride ?? SimilarScoring.MaxCandidatesPerQuery,
            EnableTotalRecordCount = false
        };
        applyFilter?.Invoke(query);

        // A genre or tag filter matches any of the supplied values, so a popular genre alone can
        // cover most of the library, and the unfiltered fallback covers all of it. Ordering first
        // means the capped pool is the best rated or the newest rather than whatever the database
        // happened to return.
        SetOrderByDescending(query, applyFilter == null ? ItemSortBy.PremiereDate : ItemSortBy.CommunityRating);
        ApplyUser(query, user);

        foreach (var candidate in _libraryManager.GetItemsResult(query).Items)
        {
            if (!excludedIds.Contains(candidate.Id))
            {
                candidatesMap[candidate.Id] = candidate;
            }
        }
    }

    private static void ApplyUser(InternalItemsQuery query, object? user)
    {
        if (user == null || _internalItemsQuerySetUser == null) return;
        _internalItemsQuerySetUser.Invoke(query, [user]);
    }

    /// <summary>
    /// Sets OrderBy descending through reflection. The SortOrder half of the tuple moved assemblies
    /// between Jellyfin 10.10 and 10.11, so a direct assignment only binds on one of them.
    /// </summary>
    private static void SetOrderByDescending(InternalItemsQuery query, ItemSortBy sortBy)
    {
        if (_internalItemsQueryOrderBy == null) return;

        var elementType = _internalItemsQueryOrderBy.PropertyType.GetGenericArguments()[0];
        var sortOrderType = elementType.GetGenericArguments()[1];
        var descending = Enum.Parse(sortOrderType, "Descending");

        var array = Array.CreateInstance(elementType, 1);
        array.SetValue(Activator.CreateInstance(elementType, sortBy, descending), 0);

        _internalItemsQueryOrderBy.SetValue(query, array);
    }

    private IReadOnlyList<PersonInfo> GetItemPeople(BaseItem item)
    {
        if (_libMgrGetPeople == null)
        {
            return Array.Empty<PersonInfo>();
        }

        try
        {
            if (_libMgrGetPeople.Invoke(_libraryManager, [item]) is IEnumerable<PersonInfo> people)
            {
                return people as IReadOnlyList<PersonInfo> ?? people.ToArray();
            }
        }
        catch (TargetInvocationException ex)
        {
            _logger.LogDebug(ex, "Could not read people for '{ItemName}'.", item.Name);
        }

        return Array.Empty<PersonInfo>();
    }

    /// <summary>
    /// Reads cast and crew for a batch of items, one query on Jellyfin 12 and one query per item on
    /// older servers. The caller keeps the batch small so the fallback stays cheap.
    /// </summary>
    private Dictionary<Guid, IReadOnlyList<PersonInfo>> GetPeopleForItems(IReadOnlyList<BaseItem> items)
    {
        var result = new Dictionary<Guid, IReadOnlyList<PersonInfo>>(items.Count);

        if (_libMgrGetPeopleByItems != null)
        {
            try
            {
                IReadOnlyList<Guid> ids = items.Select(i => i.Id).ToArray();
                if (_libMgrGetPeopleByItems.Invoke(_libraryManager, [ids]) is IReadOnlyDictionary<Guid, IReadOnlyList<PersonInfo>> byItem)
                {
                    foreach (var (id, people) in byItem)
                    {
                        result[id] = people;
                    }

                    return result;
                }
            }
            catch (TargetInvocationException ex)
            {
                _logger.LogDebug(ex, "Batch people lookup failed, falling back to per item reads.");
            }
        }

        foreach (var item in items)
        {
            result[item.Id] = GetItemPeople(item);
        }

        return result;
    }
}
