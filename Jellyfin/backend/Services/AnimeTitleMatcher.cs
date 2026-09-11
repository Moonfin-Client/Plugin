using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Moonfin.Server.Services;

/// <summary>
/// Folds a title down to a comparison key and builds a lookup from every key to the show that owns it.
/// </summary>
public static class AnimeTitleMatcher
{
    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex ParentheticalGroup = new(@"\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex YearOnly = new(@"^(19|20)\d{2}$", RegexOptions.Compiled);

    /// <summary>
    /// The site sometimes appends a season marker to the title, which is not part of the name and should be ignored.
    /// </summary>
    private static readonly Regex SeasonSuffix = new(
        @"\s+(season\s*\d+|\d+(st|nd|rd|th)\s+season|part\s*\d+|cour\s*\d+|s\d+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Folds diacritics, lowercases, and removes punctuation and whitespace. 
    /// The result is a key that can be used to compare titles.
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var folded = FoldDiacritics(title)
            // The site writes "Hunter × Hunter" with U+00D7 while libraries use a letter x.
            // Dropping it as punctuation would fold the title to "hunter hunter".
            .Replace('×', 'x')
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("＆", " and ", StringComparison.Ordinal)
            .ToLowerInvariant();

        var cleaned = NonAlphanumeric.Replace(folded, " ").Trim();

        // "The Promised Neverland" and "Promised Neverland" are the same show, and the two
        // sites disagree about the article often enough to be worth handling.
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (cleaned.StartsWith(article, StringComparison.Ordinal))
            {
                cleaned = cleaned[article.Length..];
                break;
            }
        }

        return cleaned;
    }

    /// <summary>
    /// The keys built from the title itself, with a year in parentheses 
    /// kept as a disambiguator rather than an alternate name. A bare year is never an alternate name.
    /// </summary>
    public static IEnumerable<string> PrimaryVariants(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            yield break;
        }

        var bare = Normalize(ParentheticalGroup.Replace(title, " "));
        if (bare.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // "Hunter x Hunter (2011)" and "Hunter x Hunter" are different shows with different
        // filler lists, so a year in the title is a distinguishing part of the key and gets
        // tried before the bare name.
        foreach (Match match in ParentheticalGroup.Matches(title))
        {
            var inner = match.Groups[1].Value.Trim();
            if (YearOnly.IsMatch(inner) && seen.Add(bare + " " + inner))
            {
                yield return bare + " " + inner;
            }
        }

        if (seen.Add(bare))
        {
            yield return bare;
        }

        var withoutSeason = SeasonSuffix.Replace(bare, string.Empty).Trim();
        if (withoutSeason.Length > 0 && seen.Add(withoutSeason))
        {
            yield return withoutSeason;
        }
    }

    public static IEnumerable<string> AlternateVariants(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in ParentheticalGroup.Matches(title))
        {
            var inner = match.Groups[1].Value.Trim();
            if (inner.Length == 0 || YearOnly.IsMatch(inner))
            {
                continue;
            }

            var normalized = Normalize(inner);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                yield return normalized;
            }

            var withoutSeason = SeasonSuffix.Replace(normalized, string.Empty).Trim();
            if (withoutSeason.Length > 0 && seen.Add(withoutSeason))
            {
                yield return withoutSeason;
            }
        }
    }

    /// <summary>
    /// Every key a title should be findable under, most specific first.
    /// </summary>
    public static IEnumerable<string> Variants(string? title) =>
        PrimaryVariants(title).Concat(AlternateVariants(title)).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Builds a lookup from every key to the show that owns it. Duplicate keys are ignored, keeping the first show seen.
    /// </summary>
    public static Dictionary<string, AnimeFillerShow> BuildIndex(IEnumerable<AnimeFillerShow> shows)
    {
        var index = new Dictionary<string, AnimeFillerShow>(StringComparer.Ordinal);
        var materialised = shows as IReadOnlyCollection<AnimeFillerShow> ?? shows.ToList();

        foreach (var show in materialised)
        {
            foreach (var key in PrimaryVariants(show.Title))
            {
                index.TryAdd(key, show);
            }
        }

        foreach (var show in materialised)
        {
            foreach (var key in AlternateVariants(show.Title))
            {
                index.TryAdd(key, show);
            }
        }

        return index;
    }

    /// <summary>
    /// Finds the first show in the index that matches any of the candidate titles, 
    /// or null if none do. The index is expected to be built from <see cref="BuildIndex"/>.
    /// </summary>
    public static AnimeFillerShow? Match(
        IReadOnlyDictionary<string, AnimeFillerShow> index,
        params string?[] candidateTitles)
    {
        foreach (var title in candidateTitles)
        {
            foreach (var key in Variants(title))
            {
                if (index.TryGetValue(key, out var show))
                {
                    return show;
                }
            }
        }

        return null;
    }

    private static string FoldDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
