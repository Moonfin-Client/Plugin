using System.Collections.Generic;
using Emby.Plugins.Moonfin.Api;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

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
        Assert.Equal(expected, CustomRowService.NormalizeLanguage(input));
    }

    [Fact]
    public void BuildMdbListUrl_AppendsLangWhenProvided()
    {
        var withoutLang = CustomRowService.BuildMdbListUrl("alice", "top", "apikey123", null);
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster", withoutLang);
        Assert.DoesNotContain("lang=", withoutLang);

        var withLang = CustomRowService.BuildMdbListUrl("alice", "top", "apikey123", "fr-FR");
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster&lang=fr-FR", withLang);

        var withCursorAndLang = CustomRowService.BuildMdbListUrl("alice", "top", "apikey123", "fr-FR", "nextcursor456");
        Assert.Equal("https://api.mdblist.com/lists/alice/top/items?apikey=apikey123&limit=100&append_to_response=poster&lang=fr-FR&cursor=nextcursor456", withCursorAndLang);
    }

    [Fact]
    public void BuildTmdbUrls_AppendLanguageQueryParam()
    {
        var collNoLang = CustomRowService.BuildTmdbCollectionUrl("10", null);
        var collWithLang = CustomRowService.BuildTmdbCollectionUrl("10", "fr-FR");
        Assert.Equal("https://api.themoviedb.org/3/collection/10", collNoLang);
        Assert.Equal("https://api.themoviedb.org/3/collection/10?language=fr-FR", collWithLang);

        var listNoLang = CustomRowService.BuildTmdbListUrl("123", null);
        var listWithLang = CustomRowService.BuildTmdbListUrl("123", "de");
        Assert.Equal("https://api.themoviedb.org/3/list/123", listNoLang);
        Assert.Equal("https://api.themoviedb.org/3/list/123?language=de", listWithLang);

        var chartNoLang = CustomRowService.BuildTmdbChartUrl("movie/popular", null);
        var chartWithLang = CustomRowService.BuildTmdbChartUrl("movie/popular", "es-ES");
        Assert.Equal("https://api.themoviedb.org/3/movie/popular", chartNoLang);
        Assert.Equal("https://api.themoviedb.org/3/movie/popular?language=es-ES", chartWithLang);

        var chartWithExistingQuery = CustomRowService.BuildTmdbChartUrl("discover/movie?with_genres=28", "pt-BR");
        Assert.Equal("https://api.themoviedb.org/3/discover/movie?with_genres=28&language=pt-BR", chartWithExistingQuery);

        var movieDetail = CustomRowService.BuildTmdbMovieDetailUrl("550", "fr");
        Assert.Equal("https://api.themoviedb.org/3/movie/550?language=fr", movieDetail);

        var tvDetail = CustomRowService.BuildTmdbTvDetailUrl("1399", "it");
        Assert.Equal("https://api.themoviedb.org/3/tv/1399?language=it", tvDetail);
    }
}
