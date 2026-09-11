using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Tests the logic that assigns absolute episode numbers to a series, flattening per-season numbering.
/// </summary>
public class AnimeFillerListParserTests
{
    private const string IndexHtml = """
        <div class="Group"><h2>A</h2><ul>
        <li><a href="/shows/certain-magical-index">A Certain Magical Index (Toaru Majutsu No Index)</a></li>
        <li><a href="/shows/sh%C5%8Dnan-pure-love-gang">A Certain Scientific Accelerator (Toaru Kagaku no Accelerator)</a></li>
        <li><a href="/shows/attack-titan">Attack on Titan (Shingeki no Kyojin)</a></li>
        <li><a href="/shows/akame-ga-kill">Akame ga Kill!</a></li>
        </ul></div>
        """;

    private const string EpisodeHtml = """
        <table class="EpisodeList"><tbody>
        <tr class="manga_canon odd" id="eps-1"><td class="Number">1</td><td class="Type"><span>Manga Canon</span></td></tr>
        <tr class="filler even" id="eps-2"><td class="Number">2</td><td class="Type"><span>Filler</span></td></tr>
        <tr class="mixed_canon/filler odd" id="eps-3"><td class="Number">3</td><td class="Type"><span>Mixed Canon/Filler</span></td></tr>
        <tr class="anime_canon even" id="eps-4"><td class="Number">4</td><td class="Type"><span>Anime Canon</span></td></tr>
        </tbody></table>
        """;

    [Fact]
    public void ParseShowIndex_ReadsSlugAndTitle()
    {
        var shows = AnimeFillerListParser.ParseShowIndex(IndexHtml);

        Assert.Equal(4, shows.Count);
        Assert.Equal("attack-titan", shows[2].Slug);
        Assert.Equal("Attack on Titan (Shingeki no Kyojin)", shows[2].Title);
    }

    [Fact]
    public void ParseShowIndex_KeepsPercentEncodedSlugVerbatim()
    {
        var shows = AnimeFillerListParser.ParseShowIndex(IndexHtml);

        // The slug is the URL path segment, so re-encoding or decoding it would 404.
        Assert.Contains(shows, show => show.Slug == "sh%C5%8Dnan-pure-love-gang");
    }

    [Fact]
    public void ParseEpisodes_ReadsEveryCategory()
    {
        var episodes = AnimeFillerListParser.ParseEpisodes(EpisodeHtml);

        Assert.Equal(
            new[]
            {
                AnimeEpisodeKind.MangaCanon,
                AnimeEpisodeKind.Filler,
                AnimeEpisodeKind.Mixed,
                AnimeEpisodeKind.AnimeCanon
            },
            episodes.Select(episode => episode.Kind));
    }

    [Fact]
    public void ParseEpisodes_SortsByNumber()
    {
        const string outOfOrder = """
            <tr class="filler odd" id="eps-12"><td class="Number">12</td></tr>
            <tr class="manga_canon even" id="eps-3"><td class="Number">3</td></tr>
            """;

        var episodes = AnimeFillerListParser.ParseEpisodes(outOfOrder);

        Assert.Equal(new[] { 3, 12 }, episodes.Select(episode => episode.Number));
    }

    [Fact]
    public void ParseEpisodes_SkipsUnknownCategories()
    {
        // A category the site adds later must not be silently recorded as canon.
        const string unknown = """
            <tr class="brand_new_category odd" id="eps-1"><td class="Number">1</td></tr>
            <tr class="filler even" id="eps-2"><td class="Number">2</td></tr>
            """;

        var episodes = AnimeFillerListParser.ParseEpisodes(unknown);

        Assert.Equal(2, Assert.Single(episodes).Number);
    }

    [Fact]
    public void ParseEpisodes_ReturnsEmptyForUnrelatedMarkup()
    {
        Assert.Empty(AnimeFillerListParser.ParseEpisodes("<html><body>Not found</body></html>"));
    }
}
