using System.Net;
using System.Text.RegularExpressions;

namespace Moonfin.Server.Services;

/// <summary>
/// Parses the HTML of animefillerlist.com into a form that can be used to mark episodes as filler, recap or canon.
/// </summary>
public static class AnimeFillerListParser
{
    private static readonly Regex ShowLink = new(
        @"<a\s+href=""/shows/(?<slug>[^""/]+)""\s*>(?<title>[^<]+)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The row carries its classification as the first token of its own class attribute,
    /// with the site's striping token appended: <c>class="mixed_canon/filler even"</c>.
    /// </summary>
    private static readonly Regex EpisodeRow = new(
        @"<tr\s+class=""(?<kind>[^""]+?)(?:\s+(?:odd|even))?""\s+id=""eps-(?<number>\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads the show list out of /shows. Duplicate slugs are collapsed, keeping the first title seen.
    /// </summary>
    public static List<AnimeFillerShow> ParseShowIndex(string html)
    {
        var shows = new List<AnimeFillerShow>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ShowLink.Matches(html))
        {
            var slug = match.Groups["slug"].Value;
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();

            if (slug.Length == 0 || title.Length == 0 || !seenSlugs.Add(slug))
            {
                continue;
            }

            shows.Add(new AnimeFillerShow { Slug = slug, Title = title });
        }

        return shows;
    }

    /// <summary>
    /// Reads one show's episode table. Episodes come back sorted by number, with duplicates
    /// dropped, so the caller can index into them positionally.
    /// </summary>
    public static List<AnimeMarkerEpisode> ParseEpisodes(string html)
    {
        var byNumber = new Dictionary<int, AnimeMarkerEpisode>();

        foreach (Match match in EpisodeRow.Matches(html))
        {
            if (!int.TryParse(match.Groups["number"].Value, out var number) || number <= 0)
            {
                continue;
            }

            var kind = ParseKind(match.Groups["kind"].Value);
            if (kind == null)
            {
                continue;
            }

            byNumber[number] = new AnimeMarkerEpisode { Number = number, Kind = kind.Value };
        }

        return byNumber.Values.OrderBy(episode => episode.Number).ToList();
    }

    /// <summary>
    /// Parses the episode row's class attribute into a kind, or null if the class is unrecognized.
    /// </summary>
    private static AnimeEpisodeKind? ParseKind(string cssClass) => cssClass.Trim().ToLowerInvariant() switch
    {
        "manga_canon" => AnimeEpisodeKind.MangaCanon,
        "anime_canon" => AnimeEpisodeKind.AnimeCanon,
        "mixed_canon/filler" => AnimeEpisodeKind.Mixed,
        "filler" => AnimeEpisodeKind.Filler,
        _ => null
    };
}
