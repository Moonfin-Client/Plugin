using System.Text.RegularExpressions;

namespace Moonfin.Recommendations;

public enum CreditRole
{
    Other,
    Actor,
    Director,
    Writer
}

/// <summary>The metadata Moonfin Recommends scores on, read off a server's item.</summary>
public sealed class SimilarItemFacts
{
    public SimilarItemFacts(
        string? name,
        IReadOnlyList<string>? genres,
        IReadOnlyList<string>? tags,
        IReadOnlyList<string>? studios,
        int? productionYear,
        float? communityRating)
    {
        Name = name;
        Genres = genres;
        Tags = tags;
        Studios = studios;
        ProductionYear = productionYear;
        CommunityRating = communityRating;
    }

    public string? Name { get; }

    public IReadOnlyList<string>? Genres { get; }

    public IReadOnlyList<string>? Tags { get; }

    public IReadOnlyList<string>? Studios { get; }

    public int? ProductionYear { get; }

    public float? CommunityRating { get; }
}

/// <summary>The seed item's metadata, read once so scoring never touches it again per candidate.</summary>
public sealed class SimilarSeed
{
    public SimilarSeed(SimilarItemFacts item, IEnumerable<(string? Name, CreditRole Role)> people)
    {
        Genres = new HashSet<string>(item.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Tags = new HashSet<string>(item.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Studios = new HashSet<string>(item.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Year = item.ProductionYear;
        CommunityRating = item.CommunityRating;
        Keywords = SimilarScoring.ExtractKeyWords(item.Name);
        SimilarScoring.CollectPeople(people, Actors, Directors, Writers);
    }

    public HashSet<string> Genres { get; }

    public HashSet<string> Tags { get; }

    public HashSet<string> Studios { get; }

    public int? Year { get; }

    public float? CommunityRating { get; }

    public HashSet<string> Keywords { get; }

    public HashSet<string> Actors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Writers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasPeople => Actors.Count > 0 || Directors.Count > 0 || Writers.Count > 0;
}

/// <summary>
/// Server side scoring for Moonfin Recommends.
///
/// Scoring runs in two passes. The first scores every candidate on metadata that already sits on
/// the item, which costs nothing extra. The second takes only the best of those and adds cast and
/// crew overlap, which can need a database read per item. Splitting it keeps the people reads
/// bounded no matter how large the library is.
/// </summary>
public static class SimilarScoring
{
    public const int MaxCandidatesPerQuery = 400;
    public const int PeopleScoringPoolSize = 60;
    public const int SparseCandidateThreshold = 20;
    public const int SparseFallbackCount = 40;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;

    private const double GenrePoints = 7.0;
    private const double GenreCap = 35.0;
    private const double TagPoints = 4.0;
    private const double TagCap = 20.0;
    private const double YearMaxPoints = 10.0;
    private const int YearDecayRange = 15;
    private const double RatingMaxPoints = 10.0;
    private const double TitleMatchPoints = 25.0;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "from", "under", "over", "about", "chapter", "part", "movie", "film"
    };

    private static readonly Regex NonWordRegex = new(@"[^\w\s]", RegexOptions.Compiled);

    public static int EffectiveLimit(int? limit)
    {
        var effective = limit.GetValueOrDefault(DefaultLimit);
        if (effective <= 0) return DefaultLimit;
        return Math.Min(effective, MaxLimit);
    }

    /// <summary>
    /// Ranks the candidates against the seed and returns the best <paramref name="limit"/> of them.
    /// <paramref name="loadPeople"/> gets the shortlist that the people pass scores and returns a
    /// lookup that answers null for an item it has no credits for.
    /// </summary>
    public static List<T> Rank<T>(
        IReadOnlyCollection<T> candidates,
        SimilarSeed seed,
        int limit,
        Func<T, SimilarItemFacts> factsOf,
        Func<T, DateTime?> premiereOf,
        Func<IReadOnlyList<T>, Func<T, IEnumerable<(string? Name, CreditRole Role)>?>> loadPeople,
        CancellationToken cancellationToken)
    {
        var scored = new List<(T Item, double Score)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scored.Add((candidate, ScoreMetadata(factsOf(candidate), seed)));
        }

        var pool = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => premiereOf(x.Item) ?? DateTime.MinValue)
            .Take(Math.Max(limit, PeopleScoringPoolSize))
            .ToList();

        var peopleCount = Math.Min(pool.Count, PeopleScoringPoolSize);
        if (seed.HasPeople && peopleCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var peopleOf = loadPeople(pool.Take(peopleCount).Select(x => x.Item).ToList());
            for (var i = 0; i < peopleCount; i++)
            {
                var (candidate, score) = pool[i];
                var people = peopleOf(candidate);
                if (people != null)
                {
                    pool[i] = (candidate, score + ScorePeople(people, seed));
                }
            }
        }

        return pool
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => premiereOf(x.Item) ?? DateTime.MinValue)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }

    internal static void CollectPeople(
        IEnumerable<(string? Name, CreditRole Role)> people,
        HashSet<string> actors,
        HashSet<string> directors,
        HashSet<string> writers)
    {
        foreach (var (name, role) in people)
        {
            if (string.IsNullOrEmpty(name)) continue;

            switch (role)
            {
                case CreditRole.Actor:
                    actors.Add(name!);
                    break;
                case CreditRole.Director:
                    directors.Add(name!);
                    break;
                case CreditRole.Writer:
                    writers.Add(name!);
                    break;
            }
        }
    }

    private static double ScoreDiminishing(int count, double first, double second, double third = 0.0)
    {
        if (count <= 0) return 0.0;
        if (count == 1) return first;
        if (count == 2) return first + second;
        return first + second + third;
    }

    private static double ScoreMetadata(SimilarItemFacts candidate, SimilarSeed seed)
    {
        double score = 0.0;

        score += Math.Min(CountOverlap(candidate.Genres, seed.Genres) * GenrePoints, GenreCap);
        score += Math.Min(CountOverlap(candidate.Tags, seed.Tags) * TagPoints, TagCap);
        score += ScoreDiminishing(CountOverlap(candidate.Studios, seed.Studios), 12.0, 8.0);

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

    private static double ScorePeople(IEnumerable<(string? Name, CreditRole Role)> people, SimilarSeed seed)
    {
        var actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPeople(people, actors, directors, writers);

        return ScoreDiminishing(actors.Count(seed.Actors.Contains), 10.0, 6.0, 4.0)
            + ScoreDiminishing(directors.Count(seed.Directors.Contains), 15.0, 10.0, 5.0)
            + ScoreDiminishing(writers.Count(seed.Writers.Contains), 15.0, 10.0, 5.0);
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

    internal static HashSet<string> ExtractKeyWords(string? title)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(title)) return keywords;

        var cleaned = NonWordRegex.Replace(title!.ToLowerInvariant(), " ");
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

        if (seedKeywords.IsSubsetOf(candidateKeywords) || candidateKeywords.IsSubsetOf(seedKeywords))
        {
            return true;
        }

        // One subject word that differs only by a suffix of two characters or fewer, so a plural
        // sequel like "Aliens" matches "Alien" while "The Alienist" doesn't.
        if (seedKeywords.Count == 1 && candidateKeywords.Count == 1)
        {
            var a = seedKeywords.First();
            var b = candidateKeywords.First();
            var shortWord = a.Length <= b.Length ? a : b;
            var longWord = a.Length <= b.Length ? b : a;
            return longWord.StartsWith(shortWord, StringComparison.OrdinalIgnoreCase) && longWord.Length - shortWord.Length <= 2;
        }

        return false;
    }
}
