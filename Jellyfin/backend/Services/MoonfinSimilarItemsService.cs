using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Server side scoring for Moonfin Recommends.
///
/// Scoring runs in two passes. The first scores every candidate on metadata that already sits on
/// the item, which costs nothing extra. The second takes only the best of those and adds cast and
/// crew overlap, which needs a database read per item on Jellyfin 10.x. Splitting it keeps the
/// people reads bounded no matter how large the library is.
/// </summary>
public class MoonfinSimilarItemsService
{
    private const int MaxCandidatesPerQuery = 400;
    private const int PeopleScoringPoolSize = 60;
    private const int SparseCandidateThreshold = 20;
    private const int SparseFallbackCount = 40;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 200;

    private const double GenrePoints = 5.0;
    private const double GenreCap = 35.0;
    private const double TagPoints = 4.0;
    private const double TagCap = 20.0;
    private const double StudioPoints = 4.0;
    private const double StudioCap = 8.0;
    private const double ActorPoints = 6.0;
    private const double ActorCap = 18.0;
    private const double DirectorPoints = 8.0;
    private const double DirectorCap = 15.0;
    private const double WriterPoints = 4.0;
    private const double WriterCap = 8.0;
    private const double YearMaxPoints = 10.0;
    private const int YearDecayRange = 15;
    private const double RatingMaxPoints = 5.0;
    private const double TitleMatchPoints = 10.0;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MoonfinSimilarItemsService> _logger;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "from", "under", "over", "about", "chapter", "part", "movie", "film"
    };

    private static readonly Regex NonWordRegex = new(@"[^\w\s]", RegexOptions.Compiled);

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
        var effectiveLimit = limit.GetValueOrDefault(DefaultLimit);
        if (effectiveLimit <= 0) effectiveLimit = DefaultLimit;
        if (effectiveLimit > MaxLimit) effectiveLimit = MaxLimit;

        var isSeries = item is Series;
        if (item is not Movie && !isSeries)
        {
            return Task.FromResult<IReadOnlyList<BaseItem>>(Array.Empty<BaseItem>());
        }

        var seed = new SeedProfile(item, GetItemPeople(item));

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

        if (candidatesMap.Count < SparseCandidateThreshold)
        {
            CollectCandidates(candidatesMap, excludedIds, targetType, user, null, SparseFallbackCount);
        }

        var scored = new List<(BaseItem Item, double Score)>(candidatesMap.Count);
        foreach (var candidate in candidatesMap.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scored.Add((candidate, ScoreMetadata(candidate, seed)));
        }

        var pool = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.PremiereDate ?? DateTime.MinValue)
            .Take(Math.Max(effectiveLimit, PeopleScoringPoolSize))
            .ToList();

        var peopleCount = Math.Min(pool.Count, PeopleScoringPoolSize);
        if (seed.HasPeople && peopleCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var peopleByItem = GetPeopleForItems(pool.Take(peopleCount).Select(x => x.Item).ToList());
            for (var i = 0; i < peopleCount; i++)
            {
                var (candidate, score) = pool[i];
                if (peopleByItem.TryGetValue(candidate.Id, out var people))
                {
                    pool[i] = (candidate, score + ScorePeople(people, seed));
                }
            }
        }

        var sorted = pool
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.PremiereDate ?? DateTime.MinValue)
            .Take(effectiveLimit)
            .Select(x => x.Item)
            .ToList();

        return Task.FromResult<IReadOnlyList<BaseItem>>(sorted);
    }

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
            Limit = limitOverride ?? MaxCandidatesPerQuery,
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

    private static void CollectPeople(
        IEnumerable<PersonInfo> people,
        HashSet<string> actors,
        HashSet<string> directors,
        HashSet<string> writers)
    {
        foreach (var person in people)
        {
            if (string.IsNullOrEmpty(person.Name)) continue;

            switch (person.Type)
            {
                case PersonKind.Actor:
                case PersonKind.GuestStar:
                    actors.Add(person.Name);
                    break;
                case PersonKind.Director:
                    directors.Add(person.Name);
                    break;
                case PersonKind.Writer:
                    writers.Add(person.Name);
                    break;
            }
        }
    }

    private static double ScoreMetadata(BaseItem candidate, SeedProfile seed)
    {
        double score = 0.0;

        score += Math.Min(CountOverlap(candidate.Genres, seed.Genres) * GenrePoints, GenreCap);
        score += Math.Min(CountOverlap(candidate.Tags, seed.Tags) * TagPoints, TagCap);
        score += Math.Min(CountOverlap(candidate.Studios, seed.Studios) * StudioPoints, StudioCap);

        if (candidate.ProductionYear.HasValue && seed.Year.HasValue)
        {
            var diff = Math.Abs(candidate.ProductionYear.Value - seed.Year.Value);
            if (diff < YearDecayRange)
            {
                score += YearMaxPoints * (1.0 - ((double)diff / YearDecayRange));
            }
        }

        if (candidate.CommunityRating.HasValue)
        {
            score += seed.CommunityRating.HasValue
                ? RatingMaxPoints * Math.Max(0.0, 1.0 - (Math.Abs(candidate.CommunityRating.Value - seed.CommunityRating.Value) / 10.0))
                : RatingMaxPoints * (candidate.CommunityRating.Value / 10.0);
        }

        if (seed.Keywords.Count > 0 && IsSequelOrSimilarTitle(seed.Keywords, candidate.Name))
        {
            score += TitleMatchPoints;
        }

        return score;
    }

    private static double ScorePeople(IEnumerable<PersonInfo> people, SeedProfile seed)
    {
        var actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPeople(people, actors, directors, writers);

        return Math.Min(actors.Count(seed.Actors.Contains) * ActorPoints, ActorCap)
            + Math.Min(directors.Count(seed.Directors.Contains) * DirectorPoints, DirectorCap)
            + Math.Min(writers.Count(seed.Writers.Contains) * WriterPoints, WriterCap);
    }

    private static int CountOverlap(IReadOnlyList<string>? values, HashSet<string> seedValues)
    {
        if (values == null || seedValues.Count == 0) return 0;

        var matches = 0;
        for (var i = 0; i < values.Count; i++)
        {
            if (seedValues.Contains(values[i])) matches++;
        }

        return matches;
    }

    private static HashSet<string> ExtractKeyWords(string? title)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(title)) return keywords;

        var cleaned = NonWordRegex.Replace(title.ToLowerInvariant(), " ");
        foreach (var word in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 4 && !StopWords.Contains(word)) keywords.Add(word);
        }

        return keywords;
    }

    private static bool IsSequelOrSimilarTitle(HashSet<string> seedKeywords, string? candidateTitle)
    {
        var candidateKeywords = ExtractKeyWords(candidateTitle);
        if (candidateKeywords.Count == 0) return false;

        return seedKeywords.IsSubsetOf(candidateKeywords) || candidateKeywords.IsSubsetOf(seedKeywords);
    }

    /// <summary>
    /// The seed item's metadata, read once so scoring never touches it again per candidate.
    /// </summary>
    private sealed class SeedProfile
    {
        public SeedProfile(BaseItem item, IReadOnlyList<PersonInfo> people)
        {
            Genres = new HashSet<string>(item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Tags = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Studios = new HashSet<string>(item.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Year = item.ProductionYear;
            CommunityRating = item.CommunityRating;
            Keywords = ExtractKeyWords(item.Name);

            Actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPeople(people, Actors, Directors, Writers);
        }

        public HashSet<string> Genres { get; }

        public HashSet<string> Tags { get; }

        public HashSet<string> Studios { get; }

        public int? Year { get; }

        public float? CommunityRating { get; }

        public HashSet<string> Keywords { get; }

        public HashSet<string> Actors { get; }

        public HashSet<string> Directors { get; }

        public HashSet<string> Writers { get; }

        public bool HasPeople => Actors.Count > 0 || Directors.Count > 0 || Writers.Count > 0;
    }
}
