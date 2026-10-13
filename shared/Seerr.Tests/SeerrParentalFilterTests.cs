using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moonfin.Seerr;
using Xunit;

namespace Moonfin.Seerr.Tests;

/// <summary>
/// The Seerr proxy holds a limited user's lists, details and requests to their rating. Titles
/// 10 and 20 are allowed here, 30 is not, and a title the verdict never sees is gone.
/// </summary>
public sealed class SeerrParentalFilterTests
{
    private static readonly HashSet<long> Allowed = new HashSet<long> { 10, 20 };

    // xUnit builds a new instance per test, and the verdict runs a few titles at a time.
    private readonly ConcurrentBag<(string, long)> _asked = new ConcurrentBag<(string, long)>();

    private Task<bool> Verdict(string mediaType, long tmdbId)
    {
        _asked.Add((mediaType, tmdbId));
        return Task.FromResult(Allowed.Contains(tmdbId));
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private async Task<JsonElement> Filtered(string path, string json)
    {
        var filtered = await SeerrParentalFilter.FilterListAsync(path, Bytes(json), Verdict, CancellationToken.None);
        Assert.NotNull(filtered);
        return JsonDocument.Parse(filtered!).RootElement.Clone();
    }

    [Theory]
    [InlineData("discover/trending", true)]
    [InlineData("/discover/movies?genre=16&page=2", true)]
    [InlineData("discover/tv/network/213", true)]
    [InlineData("search", true)]
    [InlineData("media", true)]
    [InlineData("movie/278/similar", true)]
    [InlineData("tv/1399/recommendations", true)]
    [InlineData("person/287/combined_credits", true)]
    [InlineData("collection/10", true)]
    [InlineData("movie/278", false)]
    [InlineData("request", false)]
    [InlineData("auth/me", false)]
    [InlineData("settings/discover", false)]
    public void ListPathsAreTheOnesThatAnswerWithTitles(string path, bool isList)
    {
        Assert.Equal(isList, SeerrParentalFilter.IsListPath(path));
    }

    [Theory]
    [InlineData("movie/278", "movie")]
    [InlineData("/tv/1399", "tv")]
    [InlineData("tv/tvdb/121361", "tv")]
    [InlineData("movie/278/similar", null)]
    [InlineData("discover/movies", null)]
    public void DetailsPagesNameTheirMediaType(string path, string? mediaType)
    {
        Assert.Equal(mediaType, SeerrParentalFilter.DetailsMediaType(path));
    }

    [Theory]
    [InlineData("request", true)]
    [InlineData("/watchlist", true)]
    [InlineData("request/5", false)]
    [InlineData("search", false)]
    public void RequestPathsAreRequestAndWatchlist(string path, bool isRequest)
    {
        Assert.Equal(isRequest, SeerrParentalFilter.IsRequestPath(path));
    }

    [Fact]
    public void ARequestBodyNamesItsTitle()
    {
        Assert.Equal(("movie", 30L), SeerrParentalFilter.RequestTarget(Bytes("""{"mediaType":"movie","mediaId":30,"is4k":false}""")));
        Assert.Equal(("tv", 20L), SeerrParentalFilter.RequestTarget(Bytes("""{"mediaType":"tv","mediaId":"20","seasons":"all"}""")));
        Assert.Equal(("movie", 10L), SeerrParentalFilter.RequestTarget(Bytes("""{"tmdbId":10,"mediaType":"movie"}""")));
        Assert.Null(SeerrParentalFilter.RequestTarget(Bytes("""{"mediaType":"movie"}""")));
        Assert.Null(SeerrParentalFilter.RequestTarget(Bytes("""{"mediaType":"person","mediaId":30}""")));
        Assert.Null(SeerrParentalFilter.RequestTarget(Bytes("not json")));
        Assert.Null(SeerrParentalFilter.RequestTarget(null));
    }

    [Fact]
    public void ADetailsAnswerCarriesItsTmdbId()
    {
        Assert.Equal(278L, SeerrParentalFilter.DetailsTmdbId(Bytes("""{"id":278,"title":"The Shawshank Redemption"}""")));
        Assert.Null(SeerrParentalFilter.DetailsTmdbId(Bytes("""{"title":"no id"}""")));
        Assert.Null(SeerrParentalFilter.DetailsTmdbId(Bytes("[]")));
    }

    [Fact]
    public async Task APageKeepsAllowedTitlesAndPeopleAndItsCounts()
    {
        var page = await Filtered("search", """
            {"page":1,"totalPages":9,"totalResults":180,"results":[
              {"id":10,"mediaType":"movie","title":"ok"},
              {"id":30,"mediaType":"tv","name":"too old"},
              {"id":287,"mediaType":"person","name":"Brad Pitt"},
              {"id":20,"mediaType":"tv","name":"ok too"},
              {"id":99,"mediaType":"movie","title":"never rated"}]}
            """);

        Assert.Equal(9, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(180, page.GetProperty("totalResults").GetInt32());
        var kept = new List<long>();
        foreach (var item in page.GetProperty("results").EnumerateArray())
        {
            kept.Add(item.GetProperty("id").GetInt64());
        }

        Assert.Equal(new List<long> { 10, 287, 20 }, kept);
    }

    [Fact]
    public async Task EachTitleIsJudgedOnceAndByItsTmdbId()
    {
        await Filtered("media", """
            {"pageInfo":{"pages":1},"results":[
              {"id":479,"tmdbId":10,"mediaType":"movie"},
              {"id":480,"tmdbId":10,"mediaType":"movie"},
              {"id":481,"tmdbId":30,"mediaType":"tv"}]}
            """);

        Assert.Equal(new[] { ("movie", 10L), ("tv", 30L) }.OrderBy(t => t.Item2), _asked.OrderBy(t => t.Item2));
    }

    [Fact]
    public async Task APersonStaysAndTheirKnownForTitlesAreHeldToTheLimit()
    {
        var search = await Filtered("search?query=carrie", """
            {"page":1,"totalPages":1,"totalResults":2,"results":[
              {"id":4,"mediaType":"person","name":"Carrie Fisher","knownFor":[{"id":30,"mediaType":"movie"},{"id":10,"mediaType":"movie"},{"id":20,"mediaType":"tv"}]},
              {"id":30,"mediaType":"movie","title":"Carrie"}]}
            """);

        var person = Assert.Single(search.GetProperty("results").EnumerateArray());
        Assert.Equal("Carrie Fisher", person.GetProperty("name").GetString());
        Assert.Equal(new[] { 10L, 20L }, person.GetProperty("knownFor").EnumerateArray().Select(k => k.GetProperty("id").GetInt64()));
        Assert.Contains(("tv", 20L), _asked);
    }

    [Fact]
    public async Task CollectionPartsThatDeclareNoTypeAreMovies()
    {
        var collection = await Filtered("collection/10", """
            {"id":10,"name":"Star Wars","parts":[{"id":10},{"id":30},{"id":20,"mediaType":"movie"}]}
            """);

        Assert.Equal(new[] { 10L, 20L }, collection.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("id").GetInt64()));
        Assert.Contains(("movie", 30L), _asked);
    }

    [Theory]
    [InlineData("GET", "discover/trending", true)]
    [InlineData("GET", "movie/7340", true)]
    [InlineData("GET", "tv/tvdb/81189", true)]
    [InlineData("POST", "request", true)]
    [InlineData("POST", "watchlist", true)]
    [InlineData("GET", "settings/public", false)]
    [InlineData("GET", "request?filter=all", false)]
    [InlineData("POST", "auth/local", false)]
    [InlineData("PUT", "request/5", false)]
    [InlineData("DELETE", "request/5", false)]
    public void OnlyListsDetailsAndRequestsAreJudged(string method, string path, bool judged)
    {
        Assert.Equal(judged, SeerrParentalFilter.IsJudged(method, path));
    }

    [Fact]
    public async Task CreditsAndCollectionsAreFilteredInTheirOwnArrays()
    {
        var credits = await Filtered("person/287/combined_credits", """
            {"id":287,"cast":[{"id":10,"mediaType":"movie"},{"id":30,"mediaType":"movie"}],"crew":[{"id":30,"mediaType":"tv"},{"id":20,"mediaType":"tv"}]}
            """);
        Assert.Single(credits.GetProperty("cast").EnumerateArray());
        Assert.Equal(20, credits.GetProperty("crew")[0].GetProperty("id").GetInt64());

        var collection = await Filtered("collection/10", """
            {"id":10,"name":"Star Wars","parts":[{"id":30,"mediaType":"movie"},{"id":10,"mediaType":"movie"}]}
            """);
        Assert.Equal(10, collection.GetProperty("parts")[0].GetProperty("id").GetInt64());
        Assert.Equal("Star Wars", collection.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ATitleWithoutAnIdOrAMediaTypeIsNotShown()
    {
        var page = await Filtered("discover/trending", """
            {"results":[{"mediaType":"movie","title":"no id"},{"id":10,"title":"no type"},{"id":10,"mediaType":"movie"}]}
            """);
        Assert.Single(page.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task OtherAnswersPassUntouched()
    {
        Assert.Null(await SeerrParentalFilter.FilterListAsync("auth/me", Bytes("""{"id":1}"""), Verdict, CancellationToken.None));
        Assert.Null(await SeerrParentalFilter.FilterListAsync("discover/genreslider/movie", Bytes("""[{"id":16,"name":"Animation"}]"""), Verdict, CancellationToken.None));
        Assert.Null(await SeerrParentalFilter.FilterListAsync("discover/studio/2", Bytes("""{"id":2,"name":"Disney"}"""), Verdict, CancellationToken.None));
        Assert.Null(await SeerrParentalFilter.FilterListAsync("search", Bytes("not json"), Verdict, CancellationToken.None));
    }
}
