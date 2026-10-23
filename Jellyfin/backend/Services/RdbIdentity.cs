using System.Text;
using System.Text.RegularExpressions;

namespace Moonfin.Server.Services;

/// <summary>Decides whether two RDB names are the same title and edition. Never throws on malformed names.</summary>
internal static class RdbIdentity
{
    private static readonly HashSet<string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        "USA", "Europe", "Japan", "World", "Asia", "Australia", "Brazil", "Canada", "China", "Denmark",
        "Finland", "France", "Germany", "Greece", "Hong Kong", "Ireland", "Israel", "Italy", "Korea",
        "Mexico", "Netherlands", "New Zealand", "Norway", "Poland", "Portugal", "Russia", "Scandinavia",
        "Spain", "Sweden", "Taiwan", "UK", "Unknown", "Latin America", "South Africa", "India",
    };

    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "En", "Ja", "Fr", "De", "Es", "It", "Nl", "Pt", "Sv", "No", "Da", "Fi", "Zh", "Ko", "Pl", "Ru",
        "Cs", "El", "Hu", "Tr", "Ca", "Ar", "He", "Hr", "Sk", "Sl", "Uk", "Ro", "Bg", "Th", "Id",
    };

    private static readonly Regex RevPattern = new(@"^Rev\s+[A-Za-z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] RegionPreference = { "USA", "World", "Europe", "Japan" };

    // Exact name -> exact name pairs known to be the same game despite differing spellings.
    private static readonly Dictionary<string, string> Equivalents = new(StringComparer.Ordinal);

    internal static string TitleKey(string? name) => KeyOf(Parse(name).Title);

    /// <summary>Key for a name already sanitised for the CDN; `_` is an ordinary character in both forms.</summary>
    internal static string CdnTitleKey(string? sanitizedName) => TitleKey(sanitizedName);

    /// <summary>The trailing `(...)`/`[...]` groups, lower-cased with whitespace collapsed; empty when there are none.</summary>
    internal static string TrailingGroupText(string? name)
    {
        var text = name ?? string.Empty;
        var suffix = text.TrimEnd()[Parse(text).Title.Length..];
        string folded;
        try
        {
            folded = suffix.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            folded = suffix.ToLowerInvariant();
        }

        return string.Join(' ', folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static ISet<string> EditionTags(string? name)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Parse(name).Groups)
        {
            if (!IsMetadata(group))
            {
                tags.Add(group);
            }
        }

        return tags;
    }

    internal static ISet<string> RegionsOf(string? name)
    {
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Parse(name).Groups)
        {
            var parts = Split(group);
            if (parts.All(Regions.Contains))
            {
                regions.UnionWith(parts);
            }
        }

        return regions;
    }

    /// <summary>Lower is better: shares a region with <paramref name="primaryRegions"/>, then USA, World, Europe, Japan.</summary>
    internal static int RegionRank(ISet<string> candidateRegions, ISet<string> primaryRegions)
    {
        if (candidateRegions.Overlaps(primaryRegions))
        {
            return 0;
        }

        for (var i = 0; i < RegionPreference.Length; i++)
        {
            if (candidateRegions.Contains(RegionPreference[i]))
            {
                return i + 1;
            }
        }

        return RegionPreference.Length + 1;
    }

    internal static bool SameGame(string? a, string? b) => a != null && Matches(Of(a), b);

    /// <summary>Parses <paramref name="name"/> once so a scan can compare it against many names.</summary>
    internal static Identity Of(string name) => new(name, TitleKey(name), EditionTags(name));

    internal static bool Matches(Identity a, string? b)
    {
        if (b == null)
        {
            return false;
        }

        if ((Equivalents.TryGetValue(a.Name, out var ea) && string.Equals(ea, b, StringComparison.Ordinal)) ||
            (Equivalents.TryGetValue(b, out var eb) && string.Equals(eb, a.Name, StringComparison.Ordinal)))
        {
            return true;
        }

        return a.TitleKey.Length > 0 && a.TitleKey == TitleKey(b) && a.EditionTags.SetEquals(EditionTags(b));
    }

    internal sealed record Identity(string Name, string TitleKey, ISet<string> EditionTags);

    private static string KeyOf(string title)
    {
        string folded;
        try
        {
            folded = title.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates cannot be normalised; compare them as-is rather than fail the scan.
            folded = title.ToLowerInvariant();
        }

        var tokens = folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(title.Length);
        foreach (var token in tokens)
        {
            if (token is "-" or "." or "," or ":")
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(token);
        }

        return sb.ToString();
    }

    private static bool IsMetadata(string group)
    {
        var parts = Split(group);
        return parts.All(Regions.Contains) || parts.All(IsLanguage) || RevPattern.IsMatch(group);
    }

    private static bool IsLanguage(string part)
    {
        if (part.Length == 2)
        {
            return Languages.Contains(part);
        }

        return part.Length == 5 && part[2] == '-' && Languages.Contains(part[..2]) && char.IsLetter(part[3]) && char.IsLetter(part[4]);
    }

    private static string[] Split(string group) =>
        group.Split(',', StringSplitOptions.TrimEntries);

    private static (string Title, List<string> Groups) Parse(string? name)
    {
        var groups = new List<string>();
        var rest = (name ?? string.Empty).TrimEnd();
        while (rest.Length > 0)
        {
            var close = rest[^1];
            var open = close == ')' ? '(' : close == ']' ? '[' : '\0';
            if (open == '\0')
            {
                break;
            }

            var openIndex = rest.LastIndexOf(open);
            if (openIndex < 0)
            {
                break;
            }

            groups.Add(rest.Substring(openIndex + 1, rest.Length - openIndex - 2).Trim());
            rest = rest[..openIndex].TrimEnd();
        }

        return (rest, groups);
    }
}
