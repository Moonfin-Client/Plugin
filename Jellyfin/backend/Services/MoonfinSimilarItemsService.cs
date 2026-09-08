using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Server-side scoring service for Moonfin Recommends.
/// Matches the weighted recommendation algorithm used by Moonfin clients.
/// </summary>
public class MoonfinSimilarItemsService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MoonfinSimilarItemsService> _logger;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "from", "under", "over", "about", "chapter", "part", "movie", "film"
    };

    public MoonfinSimilarItemsService(
        ILibraryManager libraryManager,
        ILogger<MoonfinSimilarItemsService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static readonly MethodInfo? _internalItemsQuerySetUser = typeof(InternalItemsQuery).GetMethod("SetUser", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo? _internalItemsQueryUserProperty = typeof(InternalItemsQuery).GetProperty("User", BindingFlags.Public | BindingFlags.Instance);

    private static void ApplyUser(InternalItemsQuery query, object? user)
    {
        if (user == null) return;
        if (_internalItemsQuerySetUser != null)
        {
            _internalItemsQuerySetUser.Invoke(query, [user]);
        }
        else if (_internalItemsQueryUserProperty != null)
        {
            _internalItemsQueryUserProperty.SetValue(query, user);
        }
    }

    private static readonly MethodInfo? _baseItemGetPeople = typeof(BaseItem).GetMethod("GetPeople", Type.EmptyTypes);
    private static readonly PropertyInfo? _baseItemPeopleProp = typeof(BaseItem).GetProperty("People", BindingFlags.Public | BindingFlags.Instance);
    private static readonly MethodInfo? _libMgrGetPeople = typeof(ILibraryManager).GetMethod("GetPeople", [typeof(BaseItem)]);

    private IEnumerable<object> GetItemPeople(BaseItem item)
    {
        if (_baseItemGetPeople != null)
        {
            try
            {
                var res = _baseItemGetPeople.Invoke(item, null);
                if (res is IEnumerable list) return list.Cast<object>();
            }
            catch { }
        }

        if (_baseItemPeopleProp != null)
        {
            try
            {
                var res = _baseItemPeopleProp.GetValue(item);
                if (res is IEnumerable list) return list.Cast<object>();
            }
            catch { }
        }

        if (_libMgrGetPeople != null)
        {
            try
            {
                var res = _libMgrGetPeople.Invoke(_libraryManager, [item]);
                if (res is IEnumerable list) return list.Cast<object>();
            }
            catch { }
        }

        return Array.Empty<object>();
    }

    private static (string? Name, string? Role) GetPersonDetails(object p)
    {
        var pType = p.GetType();
        var name = pType.GetProperty("Name")?.GetValue(p)?.ToString();
        var role = pType.GetProperty("Type")?.GetValue(p)?.ToString();
        return (name, role);
    }

    /// <summary>
    /// Gets scored recommendations for a given media item.
    /// </summary>
    public Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        BaseItem item,
        object? user,
        int? limit,
        IReadOnlyList<Guid>? excludeItemIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var effectiveLimit = limit.GetValueOrDefault(100);
        if (effectiveLimit <= 0) effectiveLimit = 100;

        var isSeries = item is Series;
        var isMovie = item is Movie;

        // Only Movie and Series types are eligible for Moonfin Recommends
        if (!isMovie && !isSeries)
        {
            return Task.FromResult<IReadOnlyList<BaseItem>>(Array.Empty<BaseItem>());
        }

        var baseGenres = new HashSet<string>(item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var baseTags = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var baseStudios = new HashSet<string>(item.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var baseYear = item.ProductionYear;
        var baseName = item.Name ?? string.Empty;

        var people = GetItemPeople(item);
        var actorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in people)
        {
            var (pName, pRole) = GetPersonDetails(p);
            if (string.IsNullOrEmpty(pName) || string.IsNullOrEmpty(pRole)) continue;

            if (string.Equals(pRole, "Actor", StringComparison.OrdinalIgnoreCase))
            {
                actorNames.Add(pName);
            }
            else if (string.Equals(pRole, "Director", StringComparison.OrdinalIgnoreCase))
            {
                directorNames.Add(pName);
            }
            else if (string.Equals(pRole, "Writer", StringComparison.OrdinalIgnoreCase))
            {
                writerNames.Add(pName);
            }
        }

        var excludedIds = new HashSet<Guid>(excludeItemIds ?? Array.Empty<Guid>())
        {
            item.Id
        };

        var targetType = isSeries ? BaseItemKind.Series : BaseItemKind.Movie;
        var candidatesMap = new Dictionary<Guid, BaseItem>();

        // Query candidates sharing genres (unbounded across library)
        if (baseGenres.Count > 0)
        {
            var genreQuery = new InternalItemsQuery
            {
                IncludeItemTypes = [targetType],
                IsVirtualItem = false,
                Recursive = true,
                Genres = baseGenres.ToArray(),
                DtoOptions = new DtoOptions { Fields = [ItemFields.Genres, ItemFields.Tags, ItemFields.Studios, ItemFields.People] }
            };
            ApplyUser(genreQuery, user);

            var results = _libraryManager.GetItemsResult(genreQuery).Items;
            foreach (var cand in results)
            {
                if (!excludedIds.Contains(cand.Id))
                {
                    candidatesMap[cand.Id] = cand;
                }
            }
        }

        // Query candidates sharing tags (unbounded across library)
        if (baseTags.Count > 0)
        {
            var tagQuery = new InternalItemsQuery
            {
                IncludeItemTypes = [targetType],
                IsVirtualItem = false,
                Recursive = true,
                Tags = baseTags.ToArray(),
                DtoOptions = new DtoOptions { Fields = [ItemFields.Genres, ItemFields.Tags, ItemFields.Studios, ItemFields.People] }
            };
            ApplyUser(tagQuery, user);

            var results = _libraryManager.GetItemsResult(tagQuery).Items;
            foreach (var cand in results)
            {
                if (!excludedIds.Contains(cand.Id))
                {
                    candidatesMap[cand.Id] = cand;
                }
            }
        }

        // If candidate pool is still sparse, fetch recent items of same type
        if (candidatesMap.Count < 20)
        {
            var fallbackQuery = new InternalItemsQuery
            {
                IncludeItemTypes = [targetType],
                IsVirtualItem = false,
                Recursive = true,
                Limit = 40,
                DtoOptions = new DtoOptions { Fields = [ItemFields.Genres, ItemFields.Tags, ItemFields.Studios, ItemFields.People] }
            };
            SetPremiereDateDescending(fallbackQuery);
            ApplyUser(fallbackQuery, user);

            var results = _libraryManager.GetItemsResult(fallbackQuery).Items;
            foreach (var cand in results)
            {
                if (!excludedIds.Contains(cand.Id))
                {
                    candidatesMap[cand.Id] = cand;
                }
            }
        }

        // Score candidates
        var scoredList = new List<(BaseItem Item, double Score)>(candidatesMap.Count);
        foreach (var cand in candidatesMap.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var score = ScoreCandidate(
                cand,
                baseGenres,
                baseTags,
                baseStudios,
                baseYear,
                baseName,
                actorNames,
                directorNames,
                writerNames);

            scoredList.Add((cand, score));
        }

        // Sort by score descending, then premiere date descending
        var sorted = scoredList
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.PremiereDate ?? DateTime.MinValue)
            .Take(effectiveLimit)
            .Select(x => x.Item)
            .ToList();

        return Task.FromResult<IReadOnlyList<BaseItem>>(sorted);
    }

    private double ScoreCandidate(
        BaseItem candidate,
        HashSet<string> genres,
        HashSet<string> tags,
        HashSet<string> studios,
        int? baseYear,
        string baseName,
        HashSet<string> actorNames,
        HashSet<string> directorNames,
        HashSet<string> writerNames)
    {
        double score = 0.0;

        // Shared Genres: +3.0 each
        if (candidate.Genres != null)
        {
            foreach (var g in candidate.Genres)
            {
                if (genres.Contains(g)) score += 3.0;
            }
        }

        // Shared Tags: +3.0 each
        if (candidate.Tags != null)
        {
            foreach (var t in candidate.Tags)
            {
                if (tags.Contains(t)) score += 3.0;
            }
        }

        // Shared People (Actors +5.0, Directors +6.0, Writers +6.0)
        var candPeople = GetItemPeople(candidate);
        foreach (var p in candPeople)
        {
            var (pName, pRole) = GetPersonDetails(p);
            if (string.IsNullOrEmpty(pName) || string.IsNullOrEmpty(pRole)) continue;

            if (string.Equals(pRole, "Actor", StringComparison.OrdinalIgnoreCase))
            {
                if (actorNames.Contains(pName)) score += 5.0;
            }
            else if (string.Equals(pRole, "Director", StringComparison.OrdinalIgnoreCase))
            {
                if (directorNames.Contains(pName)) score += 6.0;
            }
            else if (string.Equals(pRole, "Writer", StringComparison.OrdinalIgnoreCase))
            {
                if (writerNames.Contains(pName)) score += 6.0;
            }
        }

        // Shared Studios: +3.0 each
        if (candidate.Studios != null)
        {
            foreach (var s in candidate.Studios)
            {
                if (studios.Contains(s)) score += 3.0;
            }
        }

        // Production Year: +2.0 same year, +1.0 within 3 years
        if (candidate.ProductionYear.HasValue && baseYear.HasValue)
        {
            var diff = Math.Abs(candidate.ProductionYear.Value - baseYear.Value);
            if (diff == 0) score += 2.0;
            else if (diff <= 3) score += 1.0;
        }

        // Sequel or Similar Title: +10.0
        if (IsSequelOrSimilarTitle(baseName, candidate.Name ?? string.Empty))
        {
            score += 10.0;
        }

        // Community Rating: +(CommunityRating / 10.0)
        if (candidate.CommunityRating.HasValue)
        {
            score += candidate.CommunityRating.Value / 10.0;
        }

        return score;
    }

    private static List<string> ExtractKeyWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        var cleaned = Regex.Replace(s.ToLowerInvariant(), @"[^\w\s]", " ");
        return cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .ToList();
    }

    private static bool IsSequelOrSimilarTitle(string titleA, string titleB)
    {
        var a = ExtractKeyWords(titleA);
        var b = ExtractKeyWords(titleB);
        if (a.Count == 0 || b.Count == 0) return false;

        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);

        // One title's keywords are completely contained in the other
        if (setA.IsSubsetOf(setB) || setB.IsSubsetOf(setA)) return true;

        return false;
    }

    /// <summary>
    /// Sets OrderBy to (ItemSortBy.PremiereDate, SortOrder.Descending) via reflection
    /// to avoid compile-time or runtime type mismatches between Jellyfin 10.x, 11.x, and 12.x.
    /// </summary>
    private static void SetPremiereDateDescending(InternalItemsQuery query)
    {
        try
        {
            var orderByProp = typeof(InternalItemsQuery).GetProperty(nameof(InternalItemsQuery.OrderBy));
            if (orderByProp == null) return;

            var elementType = orderByProp.PropertyType.GetGenericArguments()[0];
            var sortOrderType = elementType.GetGenericArguments()[1];
            // SortOrder.Descending is enum value 1 (Ascending = 0, Descending = 1)
            var descending = Enum.ToObject(sortOrderType, 1);

            var tuple = Activator.CreateInstance(elementType, ItemSortBy.PremiereDate, descending);
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(tuple, 0);

            orderByProp.SetValue(query, array);
        }
        catch
        {
            // Silently ignore if order cannot be set dynamically
        }
    }
}
