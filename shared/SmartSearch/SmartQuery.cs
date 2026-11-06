using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Moonfin.SmartSearch;

/// <summary>
/// The structured hints a plain sentence carries, read with rules rather than a model: a year
/// range ("90s", "late 2000s", "before 1980"), the kinds of item it asks for ("movies", "band",
/// "actor") and the people it names, matched against names the index knows, typos included.
/// </summary>
public sealed class SmartQuery
{
    public const string Movie = "Movie";
    public const string Series = "Series";
    public const string MusicArtist = "MusicArtist";
    public const string MusicAlbum = "MusicAlbum";
    public const string Person = "Person";
    public const string Book = "Book";

    private static readonly Dictionary<string, string[]> KindWords = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["movie"] = new[] { Movie }, ["movies"] = new[] { Movie }, ["film"] = new[] { Movie }, ["films"] = new[] { Movie },
        ["show"] = new[] { Series }, ["shows"] = new[] { Series }, ["series"] = new[] { Series }, ["sitcom"] = new[] { Series },
        ["sitcoms"] = new[] { Series }, ["miniseries"] = new[] { Series },
        ["band"] = new[] { MusicArtist }, ["bands"] = new[] { MusicArtist }, ["singer"] = new[] { MusicArtist },
        ["singers"] = new[] { MusicArtist }, ["rapper"] = new[] { MusicArtist }, ["rappers"] = new[] { MusicArtist },
        ["musician"] = new[] { MusicArtist }, ["musicians"] = new[] { MusicArtist }, ["duo"] = new[] { MusicArtist },
        ["artist"] = new[] { MusicArtist }, ["artists"] = new[] { MusicArtist }, ["dj"] = new[] { MusicArtist },
        ["music"] = new[] { MusicArtist, MusicAlbum }, ["song"] = new[] { MusicArtist, MusicAlbum },
        ["songs"] = new[] { MusicArtist, MusicAlbum }, ["album"] = new[] { MusicAlbum }, ["albums"] = new[] { MusicAlbum },
        ["actor"] = new[] { Person }, ["actors"] = new[] { Person }, ["actress"] = new[] { Person },
        ["actresses"] = new[] { Person }, ["director"] = new[] { Person }, ["directors"] = new[] { Person },
        ["book"] = new[] { Book }, ["books"] = new[] { Book }, ["novel"] = new[] { Book }, ["novels"] = new[] { Book },
        ["audiobook"] = new[] { Book }, ["audiobooks"] = new[] { Book },
    };

    private static readonly Regex Decade = new Regex(
        @"\b(?:(early|mid|late)[\s-]+)?(?:the\s+)?(?:'|’)?((?:19|20)?\d)0s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlayedBy = new Regex(@"\bwho (played|plays|voiced|voices)\b|\b(played|voiced) by\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Year = new Regex(
        @"\b(?:(before|after|since|until|pre|post)\s+)?((?:19|20)\d\d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private SmartQuery(string text)
    {
        Text = text;
    }

    public string Text { get; }

    /// <summary>First year asked for, 0 when the query names none.</summary>
    public int YearMin { get; private set; }

    /// <summary>Last year asked for, 0 when the query names none.</summary>
    public int YearMax { get; private set; }

    /// <summary>Item kinds the query asks for. Empty means any.</summary>
    public HashSet<string> Kinds { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Normalized names of the people the query mentions.</summary>
    public HashSet<string> People { get; } = new HashSet<string>(StringComparer.Ordinal);

    public bool HasYears => YearMin > 0;

    /// <summary>True when the query is little more than the names in it, like "tom hanks".</summary>
    public bool NameOnly { get; private set; }

    public static SmartQuery Parse(string text, PeopleDirectory people, int currentYear)
    {
        var query = new SmartQuery(text.Trim());
        query.ReadYears(currentYear);

        var words = Words(query.Text);
        foreach (var word in words)
        {
            if (KindWords.TryGetValue(word, out var kinds))
            {
                query.Kinds.UnionWith(kinds);
            }
        }

        if (PlayedBy.IsMatch(query.Text))
        {
            query.Kinds.Add(Person);
        }

        foreach (var name in people.Find(words))
        {
            query.People.Add(name);
        }

        var nameWords = query.People.Sum(n => n.Split(' ').Length);
        query.NameOnly = query.People.Count > 0 && words.Count - nameWords <= 1;
        return query;
    }

    /// <summary>Lowercase words with accents and punctuation removed, the form names are matched in.</summary>
    private static List<string> Words(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c != '\'' && c != '’' && c != '.')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>A name in the form the directory and queries compare.</summary>
    public static string NormalizeName(string name) => string.Join(" ", Words(name));

    private void ReadYears(int currentYear)
    {
        var decade = Decade.Match(Text);
        if (decade.Success)
        {
            var digits = decade.Groups[2].Value;
            int start;
            if (digits.Length == 1)
            {
                // Two digits mean this century once that decade has started, so "20s" is the 2020s and "30s" the 1930s.
                start = int.Parse(digits, CultureInfo.InvariantCulture) * 10;
                start += start <= currentYear % 100 ? currentYear / 100 * 100 : (currentYear / 100 - 1) * 100;
            }
            else
            {
                start = int.Parse(digits, CultureInfo.InvariantCulture) * 10;
            }

            (YearMin, YearMax) = decade.Groups[1].Value.ToLowerInvariant() switch
            {
                "early" => (start, start + 3),
                "mid" => (start + 3, start + 6),
                "late" => (start + 6, start + 9),
                _ => (start, start + 9),
            };
            return;
        }

        var year = Year.Match(Text);
        if (year.Success)
        {
            var value = int.Parse(year.Groups[2].Value, CultureInfo.InvariantCulture);
            (YearMin, YearMax) = year.Groups[1].Value.ToLowerInvariant() switch
            {
                "before" or "pre" or "until" => (1800, value - 1),
                "after" or "post" => (value + 1, currentYear + 1),
                "since" => (value, currentYear + 1),
                _ => (value, value),
            };
        }
    }
}

/// <summary>
/// Every person name the index knows, for spotting names inside a query. Full names match as two
/// or three word runs, allowing one typo in longer names ("jim carey"). A lone surname matches
/// only when it's long and belongs to one well known person ("dicaprio").
/// </summary>
public sealed class PeopleDirectory
{
    private const int MinSurnameLength = 6;
    private const int MinSurnameAppearances = 3;
    private const int MinTypoNameLength = 8;

    private readonly Dictionary<string, int> _appearances = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly HashSet<string> _titleWords = new HashSet<string>(StringComparer.Ordinal);
    private Dictionary<string, string>? _surnames;
    private Dictionary<int, List<string>>? _byLength;

    public void Add(string normalizedName)
    {
        if (normalizedName.Length == 0)
        {
            return;
        }

        _appearances.TryGetValue(normalizedName, out var count);
        _appearances[normalizedName] = count + 1;
        _surnames = null;
        _byLength = null;
    }

    /// <summary>
    /// Words used in titles. A surname that is also one ("christmas", "island") is too common to
    /// stand for a person on its own.
    /// </summary>
    public void AddTitleWords(string normalizedTitle)
    {
        foreach (var word in normalizedTitle.Split(' '))
        {
            _titleWords.Add(word);
        }

        _surnames = null;
    }

    public IEnumerable<string> Find(IReadOnlyList<string> words)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var used = new bool[words.Count];
        for (var size = 3; size >= 2; size--)
        {
            for (var i = 0; i + size <= words.Count; i++)
            {
                if (Enumerable.Range(i, size).Any(j => used[j]))
                {
                    continue;
                }

                var phrase = string.Join(" ", words.Skip(i).Take(size));
                var match = _appearances.ContainsKey(phrase) ? phrase : Fuzzy(phrase);
                if (match != null)
                {
                    found.Add(match);
                    for (var j = i; j < i + size; j++)
                    {
                        used[j] = true;
                    }
                }
            }
        }

        var surnames = _surnames ??= BuildSurnames();
        for (var i = 0; i < words.Count; i++)
        {
            if (!used[i] && words[i].Length >= MinSurnameLength && surnames.TryGetValue(words[i], out var name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    private string? Fuzzy(string phrase)
    {
        if (phrase.Length < MinTypoNameLength)
        {
            return null;
        }

        var byLength = _byLength ??= _appearances.Keys.GroupBy(k => k.Length).ToDictionary(g => g.Key, g => g.ToList());
        string? best = null;
        var bestCount = 0;
        for (var length = phrase.Length - 1; length <= phrase.Length + 1; length++)
        {
            if (!byLength.TryGetValue(length, out var names))
            {
                continue;
            }

            foreach (var name in names)
            {
                if (name[0] == phrase[0] && WithinOneEdit(phrase, name) && _appearances[name] > bestCount)
                {
                    best = name;
                    bestCount = _appearances[name];
                }
            }
        }

        return best;
    }

    private Dictionary<string, string> BuildSurnames()
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in _appearances)
        {
            if (pair.Value < MinSurnameAppearances)
            {
                continue;
            }

            var space = pair.Key.LastIndexOf(' ');
            if (space < 0)
            {
                continue;
            }

            var surname = pair.Key.Substring(space + 1);
            if (surname.Length < MinSurnameLength || _titleWords.Contains(surname))
            {
                continue;
            }

            if (!owners.TryGetValue(surname, out var list))
            {
                owners[surname] = list = new List<string>();
            }

            list.Add(pair.Key);
        }

        return owners.Where(o => o.Value.Count == 1).ToDictionary(o => o.Key, o => o.Value[0], StringComparer.Ordinal);
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        var i = 0;
        while (i < a.Length && a[i] == b[i])
        {
            i++;
        }

        if (a.Length == b.Length)
        {
            return i == a.Length || a.Substring(i + 1) == b.Substring(i + 1);
        }

        return a.Substring(i) == b.Substring(i + 1);
    }
}
