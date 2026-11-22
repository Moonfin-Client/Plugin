using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Moonfin.Calendar.Tests;

public class ArrQueueTests
{
    private const string BlockedMovie = """
        {"id":7,"movieId":3,"title":"Some.Movie.2026.1080p-GRP","status":"completed","trackedDownloadState":"importBlocked",
         "trackedDownloadStatus":"warning","statusMessages":[{"title":"Some.Movie.2026.1080p-GRP","messages":["Caution: Found executable file with extension: '.exe'"]}],
         "size":1000,"sizeleft":0,"timeleft":"00:00:00","estimatedCompletionTime":"2026-11-10T22:20:08Z","added":"2026-11-10T20:00:00Z",
         "downloadId":"ABC","protocol":"torrent","downloadClient":"qBittorrent","indexer":"Some Indexer",
         "quality":{"quality":{"id":7,"name":"Bluray-1080p"}},"outputPath":"/downloads/complete/Some.Movie.2026.1080p-GRP",
         "movie":{"path":"/movies/Some Movie (2026)"},"customFormats":[]}
        """;

    [Fact]
    public async Task AMovieInTheQueueComesBackWithItsStateAndMessages()
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[" + BlockedMovie + "]");

        var answer = await ArrQueue.ReadAsync(arr.Send, isSonarr: false, "550", isAdmin: false);

        Assert.Equal(200, answer.StatusCode);
        var body = Json(answer);
        Assert.Equal("radarr", (string?)body["service"]);
        Assert.True((bool?)body["inArr"]);
        Assert.True((bool?)body["monitored"]);
        Assert.False((bool?)body["hasFile"]);
        Assert.True((bool?)body["isAvailable"]);
        Assert.False((bool?)body["canRemove"]);
        var item = Assert.Single(body["items"]!.AsArray())!.AsObject();
        Assert.Equal(7, (int?)item["id"]);
        Assert.Equal("importBlocked", (string?)item["trackedDownloadState"]);
        Assert.Equal("warning", (string?)item["trackedDownloadStatus"]);
        Assert.Equal("completed", (string?)item["status"]);
        Assert.Equal("00:00:00", (string?)item["timeleft"]);
        Assert.Equal("ABC", (string?)item["downloadId"]);
        Assert.Equal("Bluray-1080p", (string?)item["quality"]);
        Assert.Equal("Caution: Found executable file with extension: '.exe'", (string?)item["statusMessages"]![0]!["messages"]![0]);
        Assert.Null(item["movie"]);
        Assert.Null(item["customFormats"]);
    }

    [Fact]
    public async Task OnlyAdminsGetTheDownloadFolder()
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[" + BlockedMovie + "]");

        var user = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false))["items"]![0]!;
        var admin = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: true));

        Assert.Null(user["outputPath"]);
        Assert.True((bool?)admin["canRemove"]);
        Assert.Equal("/downloads/complete/Some.Movie.2026.1080p-GRP", (string?)admin["items"]![0]!["outputPath"]);
    }

    [Fact]
    public async Task PathsInsideMessagesAreCutToTheirLastNameForUsers()
    {
        const string Stuck = """
            [{"id":9,"seasonNumber":1,"episodeId":58,"statusMessages":[{"title":"/downloads/tv/Show.S01E01.mkv","messages":["No files found are eligible for import in /downloads/tv/Show.S01"]}],
              "errorMessage":"Import failed, path does not exist: C:\\downloads\\Show.S01","downloadId":"X","episode":{"seasonNumber":1,"episodeNumber":1}}]
            """;
        var arr = new FakeArr()
            .On("api/v3/series?tvdbId=280619", """[{"id":1,"monitored":true}]""")
            .On("api/v3/queue/details?seriesId=1&includeEpisode=true", Stuck);

        var user = Json(await ArrQueue.ReadAsync(arr.Send, true, "280619", isAdmin: false))["items"]![0]!;
        var admin = Json(await ArrQueue.ReadAsync(arr.Send, true, "280619", isAdmin: true))["items"]![0]!;

        Assert.Equal("Show.S01E01.mkv", (string?)user["statusMessages"]![0]!["title"]);
        Assert.Equal("No files found are eligible for import in Show.S01", (string?)user["statusMessages"]![0]!["messages"]![0]);
        Assert.Equal("Import failed, path does not exist: Show.S01", (string?)user["errorMessage"]);
        Assert.Equal("No files found are eligible for import in /downloads/tv/Show.S01", (string?)admin["statusMessages"]![0]!["messages"]![0]);
    }

    [Theory]
    [InlineData("Import failed, path does not exist or is not accessible by Sonarr: /mnt/Media Downloads/complete/Show S01", "Import failed, path does not exist or is not accessible by Sonarr: Show S01")]
    [InlineData(@"Import failed: C:\Users\John Smith\Downloads\Show.S01", "Import failed: Show.S01")]
    [InlineData(@"Import failed: \\NAS\media\downloads\Show.S01", "Import failed: Show.S01")]
    [InlineData("Folder '/downloads/My Show (2024)/' is empty", "Folder 'My Show (2024)/' is empty")]
    [InlineData("/data/Big Buck Bunny (2008)/movie.mkv is not accessible", "movie.mkv is not accessible")]
    [InlineData("No files found (/downloads/x)", "No files found (x)")]
    public void PathsWithSpacesAndNetworkSharesLeaveNoFolderBehind(string message, string redacted) =>
        Assert.Equal(redacted, ArrQueue.RedactPaths(message));

    [Theory]
    [InlineData("The download is stalled with no connections")]
    [InlineData("Caution: Found executable file with extension: '.exe'")]
    [InlineData("Not a Custom Format upgrade for existing episode file(s). New: [] do not improve on Existing: [x265]")]
    [InlineData("Release title S01/E02 is ambiguous")]
    public void MessagesWithoutAPathAreLeftAlone(string message) => Assert.Equal(message, ArrQueue.RedactPaths(message));

    [Fact]
    public async Task ASonarrEntryNamesItsSeasonAndEpisode()
    {
        var arr = new FakeArr()
            .On("api/v3/series?tvdbId=280619", """[{"id":1,"monitored":true}]""")
            .On("api/v3/queue/details?seriesId=1&includeEpisode=true",
                """[{"id":9,"seriesId":1,"episodeId":58,"seasonNumber":1,"status":"warning","trackedDownloadState":"downloading","errorMessage":"The download is stalled with no connections","episode":{"seasonNumber":1,"episodeNumber":4,"title":"Back to the Butcher"}}]""");

        var body = Json(await ArrQueue.ReadAsync(arr.Send, true, "280619", isAdmin: false));
        var item = body["items"]![0]!;

        Assert.Equal("sonarr", (string?)body["service"]);
        Assert.Null(body["hasFile"]);
        Assert.Null(body["isAvailable"]);
        Assert.Equal(58, (int?)item["episodeId"]);
        Assert.Equal(1, (int?)item["seasonNumber"]);
        Assert.Equal(4, (int?)item["episodeNumber"]);
        Assert.Equal("The download is stalled with no connections", (string?)item["errorMessage"]);
        Assert.Null(item["timeleft"]);
    }

    [Fact]
    public async Task AnUnreleasedMovieCarriesItsReleaseDateAndLastSearch()
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """
                [{"id":3,"monitored":true,"hasFile":false,"isAvailable":false,"status":"inCinemas","inCinemas":"2026-11-01T00:00:00Z",
                  "releaseDate":"2027-01-15T00:00:00Z","lastSearchTime":"2026-11-12T08:00:00Z","path":"/movies/Some Movie (2026)"}]
                """)
            .On("api/v3/queue/details?movieId=3", "[]");

        var body = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false));

        Assert.False((bool?)body["isAvailable"]);
        Assert.Equal("2027-01-15T00:00:00Z", (string?)body["releaseDate"]);
        Assert.Equal("2026-11-12T08:00:00Z", (string?)body["lastSearchTime"]);
        Assert.Null(body["status"]);
        Assert.Null(body["inCinemas"]);
        Assert.Null(body["path"]);
        Assert.Null(body["seasons"]);
    }

    [Fact]
    public async Task ASeriesCarriesEachSeasonsAiredAndDownloadedCounts()
    {
        var arr = new FakeArr()
            .On("api/v3/series?tvdbId=280619", """
                [{"id":1,"monitored":true,"status":"continuing","nextAiring":"2026-11-20T02:00:00Z","seasons":[
                  {"seasonNumber":1,"monitored":true,"statistics":{"episodeFileCount":10,"episodeCount":10,"totalEpisodeCount":10,"previousAiring":"2025-05-01T02:00:00Z","sizeOnDisk":1}},
                  {"seasonNumber":2,"monitored":true,"statistics":{"episodeFileCount":2,"episodeCount":3,"totalEpisodeCount":8,"nextAiring":"2026-11-20T02:00:00Z"}},
                  {"monitored":true}]}]
                """)
            .On("api/v3/queue/details?seriesId=1&includeEpisode=true", "[]");

        var body = Json(await ArrQueue.ReadAsync(arr.Send, true, "280619", isAdmin: false));

        Assert.Null(body["status"]);
        Assert.Null(body["nextAiring"]);
        var seasons = body["seasons"]!.AsArray();
        Assert.Equal(2, seasons.Count);
        Assert.Equal(10, (int?)seasons[0]!["episodeFileCount"]);
        Assert.Null(seasons[0]!["previousAiring"]);
        Assert.Null(seasons[0]!["sizeOnDisk"]);
        Assert.Equal(2, (int?)seasons[1]!["seasonNumber"]);
        Assert.True((bool?)seasons[1]!["monitored"]);
        Assert.Equal(3, (int?)seasons[1]!["episodeCount"]);
        Assert.Null(seasons[1]!["totalEpisodeCount"]);
        Assert.Equal("2026-11-20T02:00:00Z", (string?)seasons[1]!["nextAiring"]);
        Assert.Null(body["hasFile"]);
    }

    [Theory]
    [InlineData("""[{"name":"MoviesSearch","status":"started","body":{"movieIds":[9,3]}}]""", true)]
    [InlineData("""[{"name":"MoviesSearch","status":"queued","body":{"movieIds":[3]}}]""", true)]
    [InlineData("""[{"name":"MoviesSearch","status":"completed","body":{"movieIds":[3]}}]""", false)]
    [InlineData("""[{"name":"MoviesSearch","status":"started","body":{"movieIds":[9]}}]""", false)]
    [InlineData("""[{"name":"MissingMoviesSearch","status":"started","body":{}}]""", false)]
    [InlineData("[]", false)]
    public async Task AMovieSearchInProgressIsReported(string commands, bool searching)
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[]")
            .On("api/v3/command", commands);

        var body = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false));

        Assert.Equal(searching, (bool?)body["searching"]);
    }

    [Theory]
    [InlineData("""[{"name":"SeriesSearch","status":"started","body":{"seriesId":1}}]""", true)]
    [InlineData("""[{"name":"SeasonSearch","status":"queued","body":{"seriesId":1,"seasonNumber":2}}]""", true)]
    [InlineData("""[{"name":"SeriesSearch","status":"started","body":{"seriesId":4}}]""", false)]
    [InlineData("""[{"name":"SeriesSearch","status":"failed","body":{"seriesId":1}}]""", false)]
    [InlineData("""[{"name":"MissingEpisodeSearch","status":"started","body":{"seriesId":1,"seriesIds":[]}}]""", true)]
    [InlineData("""[{"name":"MissingEpisodeSearch","status":"queued","body":{"seriesIds":[4,1]}}]""", true)]
    [InlineData("""[{"name":"MissingEpisodeSearch","status":"started","body":{"seriesIds":[]}}]""", false)]
    [InlineData("""[{"name":"EpisodeSearch","status":"started","body":{"episodeIds":[58]}}]""", false)]
    public async Task ASeriesSearchInProgressIsReported(string commands, bool searching)
    {
        var arr = new FakeArr()
            .On("api/v3/series?tvdbId=280619", """[{"id":1,"monitored":true}]""")
            .On("api/v3/queue/details?seriesId=1&includeEpisode=true", "[]")
            .On("api/v3/command", commands);

        var body = Json(await ArrQueue.ReadAsync(arr.Send, true, "280619", isAdmin: false));

        Assert.Equal(searching, (bool?)body["searching"]);
    }

    [Theory]
    [InlineData(500, "[]")]
    [InlineData(200, "not json")]
    public async Task ACommandListThatCantBeReadLeavesSearchingOutAndSaysWhy(int status, string commands)
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[]")
            .On("api/v3/command", commands, status);
        var logged = new List<string>();

        var answer = await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false, logged.Add);

        Assert.Equal(200, answer.StatusCode);
        var body = Json(answer);
        Assert.Null(body["searching"]);
        Assert.Empty(body["items"]!.AsArray());
        var line = Assert.Single(logged);
        Assert.Contains("radarr command list (status " + status, line);
    }

    [Fact]
    public async Task TheArrIsntAskedAboutSearchesWhenSomethingIsQueuedOrOnDiskOrMissing()
    {
        var queued = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[" + BlockedMovie + "]");
        var onDisk = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":true,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[]");
        var missing = new FakeArr().On("api/v3/movie?tmdbId=550", "[]");

        foreach (var arr in new[] { queued, onDisk, missing })
        {
            var body = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false));
            Assert.Null(body["searching"]);
            Assert.DoesNotContain("api/v3/command", arr.Requested);
        }
    }

    [Fact]
    public async Task ATitleTheArrDoesntHaveIsAnEmptyAnswerNotAnError()
    {
        var arr = new FakeArr().On("api/v3/movie?tmdbId=550", "[]");

        var answer = await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false);

        Assert.Equal(200, answer.StatusCode);
        var body = Json(answer);
        Assert.False((bool?)body["inArr"]);
        Assert.Empty(body["items"]!.AsArray());
        Assert.Equal(["api/v3/movie?tmdbId=550"], arr.Requested);
    }

    [Fact]
    public async Task AnEmptyQueueIsAnEmptyListForATitleTheArrHas()
    {
        var arr = new FakeArr()
            .On("api/v3/movie?tmdbId=550", """[{"id":3,"monitored":true,"hasFile":false,"isAvailable":true}]""")
            .On("api/v3/queue/details?movieId=3", "[]");

        var body = Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false));

        Assert.True((bool?)body["inArr"]);
        Assert.Empty(body["items"]!.AsArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("12abc")]
    [InlineData("1&movieId=2")]
    [InlineData("+5")]
    public async Task AMissingOrBadIdIsA400WithoutAskingTheArr(string? id)
    {
        var arr = new FakeArr();

        var answer = await ArrQueue.ReadAsync(arr.Send, false, id, isAdmin: false);

        Assert.Equal(400, answer.StatusCode);
        Assert.Empty(arr.Requested);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task AnArrErrorIsA502ThatNamesTheArrStatus(int status)
    {
        var arr = new FakeArr().On("api/v3/movie?tmdbId=550", """{"message":"nope"}""", status);

        var answer = await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false);

        Assert.Equal(502, answer.StatusCode);
        Assert.Equal(status, (int?)Json(answer)["arrStatus"]);
    }

    [Fact]
    public async Task AnArrThatCantBeReachedSaysSoWithoutAnArrStatus()
    {
        var answer = await ArrQueue.ReadAsync((_, _, _) => Task.FromResult(ArrAnswer.Unreachable), false, "550", isAdmin: false);

        Assert.Equal(502, answer.StatusCode);
        Assert.Equal("Could not reach the radarr server", (string?)Json(answer)["error"]);
        Assert.Null(Json(answer)["arrStatus"]);
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{\"records\":[]}")]
    [InlineData("null")]
    public async Task AnUnreadableLookupIsA502(string body)
    {
        var arr = new FakeArr().On("api/v3/movie?tmdbId=550", body);

        Assert.Equal(502, (await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false)).StatusCode);
    }

    [Fact]
    public async Task RemovingOneDownloadBlocklistsItAndLetsTheArrSearchAgain()
    {
        var arr = new FakeArr()
            .On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":7,"downloadId":"ABC"},{"id":8,"downloadId":"DEF"}]}""")
            .On("api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", "", method: HttpMethod.Delete);

        var answer = await ArrQueue.RemoveAsync(arr.Send, isSonarr: false, 7);

        Assert.Equal(200, answer.StatusCode);
        Assert.Equal([7], Json(answer)["removed"]!.AsArray().Select(n => (int)n!).ToArray());
        Assert.Equal("DELETE api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", arr.Requested.Last());
    }

    [Fact]
    public async Task ASeasonPackGoesAsOneBulkRemoval()
    {
        var arr = new FakeArr()
            .On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":11,"downloadId":"pack"},{"id":10,"downloadId":"PACK"},{"id":12,"downloadId":"other"},{"id":13,"downloadId":"pack"}]}""")
            .On("api/v3/queue/bulk?removeFromClient=true&blocklist=true&skipRedownload=false", "", method: HttpMethod.Delete);

        var answer = await ArrQueue.RemoveAsync(arr.Send, isSonarr: true, 11);

        Assert.Equal(200, answer.StatusCode);
        Assert.Equal([10, 11, 13], Json(answer)["removed"]!.AsArray().Select(n => (int)n!).ToArray());
        Assert.Equal("""{"ids":[10,11,13]}""", arr.Bodies.Last());
    }

    [Fact]
    public async Task AnEntryTheArrAlreadyDroppedCountsAsRemoved()
    {
        var arr = new FakeArr()
            .On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":7,"downloadId":"ABC"}]}""")
            .On("api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", """{"message":"Not Found"}""", 404, HttpMethod.Delete);

        Assert.Equal(200, (await ArrQueue.RemoveAsync(arr.Send, false, 7)).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[{"id":7,"downloadId":"ABC"}]""")]
    [InlineData("null")]
    public async Task AQueuePageWithoutRecordsIsA502AndNothingIsDeleted(string page)
    {
        var arr = new FakeArr().On("api/v3/queue?page=1&pageSize=1000", page);

        Assert.Equal(502, (await ArrQueue.RemoveAsync(arr.Send, false, 7)).StatusCode);
        Assert.Single(arr.Requested);
    }

    [Fact]
    public async Task ATitleWithoutAPositiveIdCountsAsNotInTheArr()
    {
        var arr = new FakeArr().On("api/v3/movie?tmdbId=550", """[{"id":0,"monitored":true}]""");

        Assert.False((bool?)Json(await ArrQueue.ReadAsync(arr.Send, false, "550", isAdmin: false))["inArr"]);
    }

    [Fact]
    public async Task AnEntryWithoutADownloadIdGoesAlone()
    {
        var arr = new FakeArr()
            .On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":7},{"id":8}]}""")
            .On("api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", "", method: HttpMethod.Delete);

        Assert.Equal(200, (await ArrQueue.RemoveAsync(arr.Send, false, 7)).StatusCode);
    }

    [Fact]
    public async Task ADownloadThatAlreadyLeftTheQueueIsA404AndNothingIsDeleted()
    {
        var arr = new FakeArr().On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":8,"downloadId":"DEF"}]}""");

        var answer = await ArrQueue.RemoveAsync(arr.Send, false, 7);

        Assert.Equal(404, answer.StatusCode);
        Assert.DoesNotContain(arr.Requested, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedRemovalIsA502()
    {
        var arr = new FakeArr()
            .On("api/v3/queue?page=1&pageSize=1000", """{"records":[{"id":7,"downloadId":"ABC"}]}""")
            .On("api/v3/queue/7?removeFromClient=true&blocklist=true&skipRedownload=false", """{"message":"boom"}""", 500, HttpMethod.Delete);

        var answer = await ArrQueue.RemoveAsync(arr.Send, false, 7);

        Assert.Equal(502, answer.StatusCode);
        Assert.Equal(500, (int?)Json(answer)["arrStatus"]);
    }

    private static JsonObject Json(ArrAnswer answer) => JsonNode.Parse(answer.Body!)!.AsObject();

    /// <summary>Answers the requests it was told about and fails any other with a 404.</summary>
    private sealed class FakeArr
    {
        private readonly Dictionary<string, (int Status, string Body)> _answers = new Dictionary<string, (int, string)>();

        public List<string> Requested { get; } = new List<string>();

        public List<string> Bodies { get; } = new List<string>();

        public FakeArr On(string path, string body, int status = 200, HttpMethod? method = null)
        {
            _answers[(method ?? HttpMethod.Get).Method + " " + path] = (status, body);
            return this;
        }

        public Task<ArrAnswer> Send(HttpMethod method, string path, byte[]? body)
        {
            var key = method.Method + " " + path;
            Requested.Add(method == HttpMethod.Get ? path : key);
            if (body != null) Bodies.Add(Encoding.UTF8.GetString(body));
            return Task.FromResult(_answers.TryGetValue(key, out var answer)
                ? new ArrAnswer(answer.Status, Encoding.UTF8.GetBytes(answer.Body))
                : new ArrAnswer(404, Encoding.UTF8.GetBytes("{}")));
        }
    }
}
