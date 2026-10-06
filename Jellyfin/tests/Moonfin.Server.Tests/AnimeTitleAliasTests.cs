using MediaBrowser.Controller.Entities.TV;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Tests the fallback that matches a series with a localized library name through the
/// English and romaji titles AniList holds for its provider ids.
/// </summary>
public class AnimeTitleAliasTests
{
    private const string AniListResponse = """
        {"data":{"Page":{"media":[
          {"id":223,"idMal":223,"title":{"romaji":"Dragon Ball","english":"Dragon Ball"},
           "synonyms":["Dragonball","Bola de Drac","דרגון בול ","DB"]},
          {"id":235,"idMal":235,"title":{"romaji":"Meitantei Conan","english":"Detective Conan"},
           "synonyms":["Case Closed","Detectiu Conan","名侦探柯南","Détective Conan"]},
          {"id":2171,"idMal":2171,
           "title":{"romaji":"Meitantei Conan: Konpeki no Hitsugi (Jolly Roger)","english":"Detective Conan: Jolly Roger in the Deep Azure"},
           "synonyms":["Detective Conan 2007","Detective Conan Movie 11"]}
        ]}}}
        """;

    private static readonly AnimeFillerShow DetectiveConan = new() { Slug = "detective-conan", Title = "Detective Conan" };
    private static readonly AnimeFillerShow DragonBall = new() { Slug = "dragon-ball", Title = "Dragon Ball" };
    private static readonly AnimeFillerShow JollyRoger = new() { Slug = "jolly-roger", Title = "Jolly Roger" };

    private static Dictionary<string, AnimeFillerShow> Index() =>
        AnimeTitleMatcher.BuildIndex(new[] { DetectiveConan, DragonBall, JollyRoger });

    private static Dictionary<int, AnimeTitleAliasService.AniListMedia> Parsed() =>
        AnimeTitleAliasService.ParseMedia(AniListResponse)!.ToDictionary(media => media.Id!.Value);

    [Fact]
    public void ParseMedia_ListsEnglishThenRomajiThenSynonyms_AndDropsNonLatinTitles()
    {
        var conan = Parsed()[235];

        Assert.Equal(235, conan.MalId);
        Assert.Equal(
            new[] { "Detective Conan", "Meitantei Conan", "Case Closed", "Detectiu Conan", "Détective Conan" },
            conan.Titles);
    }

    [Fact]
    public void ParseMedia_CollapsesAnEnglishTitleThatRepeatsTheRomaji()
    {
        Assert.Equal(new[] { "Dragon Ball", "Dragonball", "Bola de Drac", "DB" }, Parsed()[223].Titles);
    }

    [Theory]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"errors\":[{\"message\":\"Too Many Requests.\"}],\"data\":null}")]
    public void ParseMedia_ReturnsNullForAnAnswerThatIsntAPage(string json)
    {
        Assert.Null(AnimeTitleAliasService.ParseMedia(json));
    }

    [Theory]
    [InlineData(235, "detective-conan")]
    [InlineData(223, "dragon-ball")]
    public void TheTitlesOfTheRightEntryFindTheShow(int aniListId, string slug)
    {
        Assert.Equal(slug, AnimeTitleMatcher.MatchPrimary(Index(), Parsed()[aniListId].Titles)?.Slug);
    }

    [Fact]
    public void AMovieEntrysSubtitleInParenthesesDoesntPassForAShow()
    {
        // The plain matcher would read "(Jolly Roger)" as another name for the show.
        Assert.Null(AnimeTitleMatcher.MatchPrimary(Index(), Parsed()[2171].Titles));
    }

    [Theory]
    [InlineData("imdb", "tt0131179")]
    [InlineData("tmdb", "30983")]
    [InlineData("tvdb", "72454")]
    [InlineData("anidb", "266")]
    public void TheMappingTableResolvesTheIdsAStockJellyfinLibraryHas(string provider, string value)
    {
        // The real table's entry for Detective Conan. IMDb ids come as an array there.
        const string Table = """
            [{"type":"TV","anidb_id":266,"anilist_id":235,"imdb_id":["tt0131179"],"kitsu_id":210,
              "mal_id":235,"themoviedb_id":{"tv":30983},"tvdb_id":72454}]
            """;

        var path = Path.Combine(Path.GetTempPath(), "moonfin-mapping-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Table);
        try
        {
            var mapping = new AnimeIdMappingService(
                new ThrowingHttpClientFactory(), new NoOpLogger<AnimeIdMappingService>());
            mapping.LoadFrom(path);

            Assert.Equal(235, mapping.ResolveProvider(provider, value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProviderKeys_PutsTheAnimeDatabasesBeforeTheGeneralOnes()
    {
        var series = new Series
        {
            Name = "El detectiu Conan",
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Imdb"] = "tt0131179",
                ["Tvdb"] = "72454",
                ["Tmdb"] = "30983",
                ["AniList"] = "2171",
                ["AniDB"] = "266",
                ["Zap2It"] = "SH03296957",
                ["TvdbSlug"] = "detective-conan"
            }
        };

        Assert.Equal(
            new[] { "anilist:2171", "anidb:266", "tvdb:72454", "tmdb:30983", "imdb:tt0131179" },
            AnimeTitleAliasService.ProviderKeys(series).Select(key => key.Key));
    }
}
