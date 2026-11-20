using System.Collections.Generic;
using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class CustomRowLocalizationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("fr", "fr")]
    [InlineData("fr_FR", "fr-FR")]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData(" pt-BR ", "pt-BR")]
    [InlineData("zh_CN", "zh-CN")]
    [InlineData("fr_fr", "fr-FR")]
    [InlineData("EN", "en")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-Hant-TW", "zh-TW")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("es-419", "es-MX")]
    [InlineData("sr-Latn", "sr")]
    [InlineData("fr&apikey=x", null)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", null)]
    public void NormalizeLanguage_NormalizesTags(string? input, string? expected)
    {
        Assert.Equal(expected, CustomRowFetchService.NormalizeLanguage(input));
    }

    [Fact]
    public void ComputeCacheKey_PartitionsByLanguageCode()
    {
        var parameters = new Dictionary<string, string> { { "username", "alice" }, { "listname", "favorites" } };

        var defaultKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters);
        var nullLangKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, null);
        var emptyLangKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, "   ");
        var frenchKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, "fr-FR");
        var frenchAltKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, "FR_fr");
        var germanKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, "de");

        Assert.Equal(defaultKey, nullLangKey);
        Assert.Equal(defaultKey, emptyLangKey);
        Assert.NotEqual(defaultKey, frenchKey);
        Assert.NotEqual(frenchKey, germanKey);
        Assert.Equal(frenchKey, frenchAltKey);
        Assert.EndsWith(":fr-fr", frenchKey);

        var colonKey = CustomRowFetchService.ComputeCacheKey("mdblist", "user_list", parameters, "fr:user123");
        Assert.Equal(defaultKey, colonKey);
    }

    [Fact]
    public void BuildMdbListUrl_AppendsLangWhenProvided()
    {
        var withoutLang = CustomRowFetchService.BuildMdbListUrl("alice", "top", "apikey123", null);
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster", withoutLang);
        Assert.DoesNotContain("lang=", withoutLang);

        var withLang = CustomRowFetchService.BuildMdbListUrl("alice", "top", "apikey123", "fr-FR");
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster&lang=fr-FR", withLang);

        var withCursorAndLang = CustomRowFetchService.BuildMdbListUrl("alice", "top", "apikey123", "fr-FR", "nextcursor456");
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster&lang=fr-FR&cursor=nextcursor456", withCursorAndLang);
    }

    [Fact]
    public void BuildTmdbUrls_AppendLanguageQueryParam()
    {
        var collNoLang = CustomRowFetchService.BuildTmdbCollectionUrl("10", null);
        var collWithLang = CustomRowFetchService.BuildTmdbCollectionUrl("10", "fr-FR");
        Assert.Equal("https://api.themoviedb.org/3/collection/10", collNoLang);
        Assert.Equal("https://api.themoviedb.org/3/collection/10?language=fr-FR", collWithLang);

        var listNoLang = CustomRowFetchService.BuildTmdbListUrl("123", null);
        var listWithLang = CustomRowFetchService.BuildTmdbListUrl("123", "de");
        Assert.Equal("https://api.themoviedb.org/3/list/123", listNoLang);
        Assert.Equal("https://api.themoviedb.org/3/list/123?language=de", listWithLang);
        Assert.Equal("https://api.themoviedb.org/3/list/123?page=2", CustomRowFetchService.BuildTmdbListUrl("123", null, 2));
        Assert.Equal("https://api.themoviedb.org/3/list/123?language=de&page=2", CustomRowFetchService.BuildTmdbListUrl("123", "de", 2));

        var chartNoLang = CustomRowFetchService.BuildTmdbChartUrl("movie/popular", null);
        var chartWithLang = CustomRowFetchService.BuildTmdbChartUrl("movie/popular", "es-ES");
        Assert.Equal("https://api.themoviedb.org/3/movie/popular", chartNoLang);
        Assert.Equal("https://api.themoviedb.org/3/movie/popular?language=es-ES", chartWithLang);

        var chartWithExistingQuery = CustomRowFetchService.BuildTmdbChartUrl("discover/movie?with_genres=28", "pt-BR");
        Assert.Equal("https://api.themoviedb.org/3/discover/movie?with_genres=28&language=pt-BR", chartWithExistingQuery);

        var movieDetail = CustomRowFetchService.BuildTmdbMovieDetailUrl("550", "fr");
        Assert.Equal("https://api.themoviedb.org/3/movie/550?language=fr", movieDetail);

        var tvDetail = CustomRowFetchService.BuildTmdbTvDetailUrl("1399", "it");
        Assert.Equal("https://api.themoviedb.org/3/tv/1399?language=it", tvDetail);
    }

    [Fact]
    public void MdbListTitleTakesTheOverviewTmdbSentInTheRowsLanguage()
    {
        var item = new CustomRowItem { Id = 603, Name = "Matrix", PosterUrl = "/mdblist.jpg" };
        using var details = JsonDocument.Parse("""
            { "overview": "Haker Neo odkrywa prawdę o rzeczywistości.", "genres": [{ "id": 28, "name": "Akcja" }], "runtime": 136,
               "poster_path": "/tmdb.jpg", "backdrop_path": "/backdrop.jpg" }
            """);

        CustomRowFetchService.ApplyMdbListTmdbDetails(item, details.RootElement);

        Assert.Equal("Haker Neo odkrywa prawdę o rzeczywistości.", item.Overview);
        Assert.Equal(["Akcja"], item.Genres);
        Assert.Equal(136 * TimeSpan.TicksPerMinute, item.RunTimeTicks);
        Assert.Equal("/mdblist.jpg", item.PosterUrl);
        Assert.Equal("/backdrop.jpg", item.BackdropUrl);
    }
}
