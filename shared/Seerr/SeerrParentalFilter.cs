using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Seerr;

/// <summary>
/// Holds what the Seerr proxy hands a user with a parental limit to that limit. Pure: the proxy
/// gives it the path, the bytes and a verdict per title, and gets the bytes back rewritten. A
/// title the verdict cannot place is dropped, as an unrated title is in the custom rows.
/// </summary>
public static class SeerrParentalFilter
{
    /// <summary>Whether the movie or tv title with this TMDB id may be shown to the user.</summary>
    public delegate Task<bool> Verdict(string mediaType, long tmdbId);

    private const int Parallelism = 6;

    // Answers that carry a list of titles. Every discover page answers with results, the studio
    // and network pages included; a discover answer that is no object, like the genre sliders,
    // is left alone further down.
    private static readonly Regex ListPath = new Regex(
        @"^(discover/.+|search|media|(movie|tv)/\d+/(similar|recommendations)|person/\d+/combined_credits|collection/\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DetailsPath = new Regex(@"^(movie|tv)/(tvdb/)?\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A collection's parts are movies by contract, and Seerr does not promise the mediaType
    // field on each of them.
    private static readonly Regex CollectionPath = new Regex(@"^collection/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] TitleArrays = { "results", "parts", "cast", "crew" };

    /// <summary>The path as the proxy receives it, without a leading slash or a query.</summary>
    public static string Normalize(string? path)
    {
        var p = (path ?? string.Empty).Trim().TrimStart('/');
        var q = p.IndexOf('?');
        if (q >= 0)
        {
            p = p.Substring(0, q);
        }

        return p.TrimEnd('/');
    }

    /// <summary>Whether a GET on this path answers with a list of titles.</summary>
    public static bool IsListPath(string? path) => ListPath.IsMatch(Normalize(path));

    /// <summary>"movie" or "tv" for a title's details page, null for any other path.</summary>
    public static string? DetailsMediaType(string? path)
    {
        var match = DetailsPath.Match(Normalize(path));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>Whether a POST on this path names a title to request or to watch.</summary>
    public static bool IsRequestPath(string? path)
    {
        var p = Normalize(path).ToLowerInvariant();
        return p == "request" || p == "watchlist";
    }

    /// <summary>
    /// Whether a call on this method and path is one the limit applies to: a GET of a list or
    /// of a details page, or a POST that names a title. The proxy resolves a user's limit for
    /// these calls only.
    /// </summary>
    public static bool IsJudged(string? method, string? path)
    {
        var m = (method ?? string.Empty).Trim().ToUpperInvariant();
        if (m == "POST")
        {
            return IsRequestPath(path);
        }

        return m == "GET" && (IsListPath(path) || DetailsMediaType(path) != null);
    }

    /// <summary>
    /// The title a request or watchlist body names, or null when it cannot be read. A request
    /// says mediaId, a watchlist entry tmdbId.
    /// </summary>
    public static (string MediaType, long TmdbId)? RequestTarget(byte[]? body)
    {
        var node = ParseObject(body);
        if (node == null)
        {
            return null;
        }

        var mediaType = MediaTypeOf(node);
        var id = IdOf(node, "mediaId") ?? IdOf(node, "tmdbId");
        return mediaType != null && id.HasValue ? (mediaType, id.Value) : ((string, long)?)null;
    }

    /// <summary>The TMDB id a details answer carries, null when it has none.</summary>
    public static long? DetailsTmdbId(byte[]? body)
    {
        var node = ParseObject(body);
        return node == null ? null : IdOf(node, "id");
    }

    /// <summary>
    /// The list answer with every title the verdict refuses removed, or null when the path or the
    /// body is not a list of titles. Each distinct title is judged once, a few at a time. The page
    /// counts stay as Seerr sent them, so a client keeps paging the way it did.
    /// </summary>
    public static async Task<byte[]?> FilterListAsync(string? path, byte[]? body, Verdict verdict, CancellationToken cancellationToken)
    {
        if (!IsListPath(path))
        {
            return null;
        }

        var root = ParseObject(body);
        if (root == null)
        {
            return null;
        }

        var arrays = TitleArrays.Select(name => root[name] as JsonArray).Where(a => a != null).Select(a => a!).ToList();
        if (arrays.Count == 0)
        {
            return null;
        }

        var fallback = CollectionPath.IsMatch(Normalize(path)) ? "movie" : null;
        var titles = new HashSet<(string, long)>();
        foreach (var array in arrays)
        {
            foreach (var element in array)
            {
                var item = element as JsonObject;
                Add(titles, TitleOf(item, fallback));
                foreach (var known in KnownFor(item))
                {
                    Add(titles, TitleOf(known));
                }
            }
        }

        var allowed = await JudgeAsync(titles, verdict, cancellationToken).ConfigureAwait(false);
        foreach (var array in arrays)
        {
            for (var i = array.Count - 1; i >= 0; i--)
            {
                var item = array[i] as JsonObject;
                if (!Keeps(item, allowed, fallback))
                {
                    array.RemoveAt(i);
                    continue;
                }

                // A person stays, and the titles they are known for are held to the limit like
                // any other list.
                if (item?["knownFor"] is JsonArray knownFor)
                {
                    for (var j = knownFor.Count - 1; j >= 0; j--)
                    {
                        if (!Keeps(knownFor[j] as JsonObject, allowed, null))
                        {
                            knownFor.RemoveAt(j);
                        }
                    }
                }
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    // A person, a collection or anything else that is not a title passes; a movie or show
    // passes on its verdict; one that names no id, or nothing at all, cannot be judged and goes.
    // [fallback] is the type an element that declares none is taken for, when the path says.
    private static bool Keeps(JsonObject? element, Dictionary<(string, long), bool> allowed, string? fallback)
    {
        if (element == null)
        {
            return false;
        }

        var mediaType = MediaTypeOf(element, fallback);
        if (mediaType == null)
        {
            return element["mediaType"] is JsonValue;
        }

        var title = TitleOf(element, fallback);
        return title.HasValue && allowed.TryGetValue(title.Value, out var ok) && ok;
    }

    private static async Task<Dictionary<(string, long), bool>> JudgeAsync(HashSet<(string, long)> titles, Verdict verdict, CancellationToken cancellationToken)
    {
        var keys = titles.ToList();
        using var gate = new SemaphoreSlim(Parallelism);
        var verdicts = await Task.WhenAll(keys.Select(async key =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await verdict(key.Item1, key.Item2).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var result = new Dictionary<(string, long), bool>();
        for (var i = 0; i < keys.Count; i++)
        {
            result[keys[i]] = verdicts[i];
        }

        return result;
    }

    private static void Add(HashSet<(string, long)> titles, (string, long)? title)
    {
        if (title.HasValue)
        {
            titles.Add(title.Value);
        }
    }

    private static IEnumerable<JsonObject> KnownFor(JsonObject? element) =>
        (element?["knownFor"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static (string, long)? TitleOf(JsonObject? element, string? fallback = null)
    {
        if (element == null)
        {
            return null;
        }

        var mediaType = MediaTypeOf(element, fallback);
        // Seerr's own media rows and the watchlist carry the TMDB id as tmdbId beside a row id.
        var id = IdOf(element, "tmdbId") ?? IdOf(element, "id");
        return mediaType != null && id.HasValue ? (mediaType, id.Value) : ((string, long)?)null;
    }

    private static string? MediaTypeOf(JsonObject element, string? fallback = null)
    {
        if (!(element["mediaType"] is JsonValue declared))
        {
            return fallback;
        }

        var value = declared.ToString().Trim().ToLowerInvariant();
        return value == "movie" || value == "tv" ? value : null;
    }

    private static long? IdOf(JsonObject element, string name)
    {
        if (!(element[name] is JsonValue value))
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<double>(out var real) && Math.Floor(real) == real)
        {
            return (long)real;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed) ? parsed : (long?)null;
    }

    private static JsonObject? ParseObject(byte[]? body)
    {
        if (body == null || body.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
