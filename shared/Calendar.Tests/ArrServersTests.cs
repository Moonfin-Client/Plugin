using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Moonfin.Calendar.Tests;

/// <summary>
/// Several Sonarr or Radarr servers set up in Seerr: which are read, how their calendars become one
/// and how one title's queue is joined.
/// </summary>
public class ArrServersTests
{
    private static JsonElement Servers(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> Names(IEnumerable<ArrServerInfo> servers) => servers.Select(s => s.Name).ToList();

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static JsonArray Array(byte[] body) => JsonNode.Parse(body)!.AsArray();

    private const string Mixed = """
        [
          { "id": 4, "name": "4k-default", "is4k": true, "isDefault": true },
          { "id": 1, "name": "spare", "is4k": false, "isDefault": false },
          { "id": 5, "name": "4k-other", "is4k": true, "isDefault": false },
          { "id": 2, "name": "main", "is4k": false, "isDefault": true }
        ]
        """;

    [Fact]
    public void TheRegularDefaultComesFirstThenRegularThen4kInSeerrOrder()
    {
        Assert.Equal(new[] { "main", "spare", "4k-default", "4k-other" }, Names(ArrServers.Order(Servers(Mixed), include4k: true)));
    }

    [Fact]
    public void Leaving4kOutKeepsOnlyTheRegularServers()
    {
        Assert.Equal(new[] { "main", "spare" }, Names(ArrServers.Order(Servers(Mixed), include4k: false)));
    }

    [Fact]
    public void OnlyThe4kServersAreReadEvenWhenLeftOut()
    {
        var servers = Servers("""[{ "id": 7, "name": "4k-a", "is4k": true }, { "id": 8, "name": "4k-b", "is4k": true, "isDefault": true }]""");

        Assert.Equal(new[] { "4k-a", "4k-b" }, Names(ArrServers.Order(servers, include4k: false)));
    }

    [Fact]
    public void ServerIdsComeFromSeerrOrFromTheirPlace()
    {
        var servers = ArrServers.Order(Servers("""[{ "id": 3, "name": "a" }, { "name": "b" }, "not a server"]"""), include4k: true);

        Assert.Equal(new[] { "3", "idx1" }, servers.Select(s => s.Id));
    }

    [Fact]
    public void ANonArrayListsNoServers()
    {
        Assert.Empty(ArrServers.Order(Servers("{}"), include4k: true));
    }

    [Fact]
    public void PickFindsTheNamedServerOrThePrimary()
    {
        var servers = ArrServers.Order(Servers(Mixed), include4k: true);

        Assert.Equal("main", ArrServers.Pick(servers, s => s.Id, null)!.Name);
        Assert.Equal("main", ArrServers.Pick(servers, s => s.Id, " ")!.Name);
        Assert.Equal("4k-other", ArrServers.Pick(servers, s => s.Id, "5")!.Name);
        Assert.Null(ArrServers.Pick(servers, s => s.Id, "99"));
    }

    [Fact]
    public void ASingleServersCalendarIsHandedBackAsItIs()
    {
        var combined = ArrCalendarMerge.Combine(new[] { new ArrCalendarPart("1", 200, Bytes("""[{"tmdbId":1}]""")) });

        Assert.Equal(0, combined.PassThrough);
        Assert.False(combined.Partial);
    }

    [Fact]
    public void CalendarsAreJoinedAndEachElementNamesItsServer()
    {
        var combined = ArrCalendarMerge.Combine(new[]
        {
            new ArrCalendarPart("1", 200, Bytes("""[{"tmdbId":1}]""")),
            new ArrCalendarPart("2", 200, Bytes("""[{"tmdbId":2},{"tmdbId":3}]""")),
        });

        Assert.Null(combined.PassThrough);
        Assert.False(combined.Partial);
        var merged = Array(combined.Body!);
        Assert.Equal(new[] { "1", "2", "2" }, merged.Select(e => (string?)e![ArrServers.ServerProperty]));
    }

    [Fact]
    public void AServerThatFailsIsLeftOutAndTheCalendarIsPartial()
    {
        var combined = ArrCalendarMerge.Combine(new[]
        {
            new ArrCalendarPart("1", 502, Bytes("""{"error":"Could not reach the radarr server"}""")),
            new ArrCalendarPart("2", 200, Bytes("""[{"tmdbId":2}]""")),
            new ArrCalendarPart("3", 200, Bytes("not json")),
        });

        Assert.True(combined.Partial);
        Assert.Equal(new[] { "1", "3" }, combined.Failed);
        Assert.Single(Array(combined.Body!));
    }

    [Fact]
    public void WhenEveryServerFailsThePrimarysErrorIsHandedBack()
    {
        var combined = ArrCalendarMerge.Combine(new[]
        {
            new ArrCalendarPart("1", 404, null),
            new ArrCalendarPart("2", 502, null),
        });

        Assert.Equal(0, combined.PassThrough);
        Assert.False(combined.Partial);
    }

    [Fact]
    public void ACalendarFromOneServerIsNotFolded()
    {
        var body = Bytes("""[{"tmdbId":1},{"tmdbId":1}]""");

        Assert.Same(body, ArrCalendarMerge.Dedupe(body, isSonarr: false));
    }

    [Fact]
    public void AMovieOnTwoServersShowsOnceAsDownloadedWhenEitherCopyIs()
    {
        var body = Bytes("""
            [
              {"tmdbId":550,"title":"regular","hasFile":false,"moonfinItemId":null,"moonfinLibraryIds":["a"],"moonfinArrServerId":"1"},
              {"tmdbId":551,"title":"other","moonfinArrServerId":"1"},
              {"tmdbId":550,"title":"4k","hasFile":true,"moonfinItemId":"jf-1","moonfinLibraryIds":["A","b"],"moonfinArrServerId":"2"}
            ]
            """);

        var result = Array(ArrCalendarMerge.Dedupe(body, isSonarr: false));

        Assert.Equal(2, result.Count);
        var movie = result[0]!;
        Assert.Equal("regular", (string?)movie["title"]);
        Assert.True((bool?)movie["hasFile"]);
        Assert.Equal("jf-1", (string?)movie["moonfinItemId"]);
        Assert.Equal(new[] { "a", "b" }, movie["moonfinLibraryIds"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void AMovieWithoutATmdbIdIsMatchedByItsImdbId()
    {
        var body = Bytes("""[{"tmdbId":0,"imdbId":"tt1","moonfinArrServerId":"1"},{"imdbId":"TT1","moonfinArrServerId":"2"}]""");

        Assert.Single(Array(ArrCalendarMerge.Dedupe(body, isSonarr: false)));
    }

    [Fact]
    public void AnEpisodeOnTwoServersShowsOnceButOtherEpisodesStay()
    {
        var body = Bytes("""
            [
              {"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":100},"moonfinArrServerId":"1"},
              {"seasonNumber":1,"episodeNumber":2,"series":{"tvdbId":100},"moonfinArrServerId":"1"},
              {"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":100},"moonfinArrServerId":"2"},
              {"seasonNumber":1,"episodeNumber":1,"series":{"tvdbId":200},"moonfinArrServerId":"2"}
            ]
            """);

        var result = Array(ArrCalendarMerge.Dedupe(body, isSonarr: true));

        Assert.Equal(3, result.Count);
        Assert.Equal("1", (string?)result[0]![ArrServers.ServerProperty]);
    }

    [Fact]
    public void ElementsWithoutIdsAreAllKept()
    {
        var body = Bytes("""[{"title":"x","moonfinArrServerId":"1"},{"title":"x","moonfinArrServerId":"2"},{"seasonNumber":1,"episodeNumber":1,"moonfinArrServerId":"2"}]""");

        Assert.Equal(3, Array(ArrCalendarMerge.Dedupe(body, isSonarr: false)).Count);
        Assert.Equal(3, Array(ArrCalendarMerge.Dedupe(body, isSonarr: true)).Count);
    }

    private static readonly ArrServerInfo[] TwoServers = ArrServers.Order(
        Servers("""[{ "id": 1, "name": "main", "isDefault": true }, { "id": 2, "name": "4k", "is4k": true }]"""),
        include4k: true).ToArray();

    private static ArrAnswer Queue(string json, int status = 200) => new ArrAnswer(status, Bytes(json));

    private static JsonObject Json(ArrAnswer answer) => JsonNode.Parse(answer.Body!)!.AsObject();

    [Fact]
    public void ASingleServersQueueIsHandedBackAsItIs()
    {
        var answer = Queue("""{"inArr":true,"items":[]}""");

        Assert.Same(answer, ArrQueueMerge.Combine(TwoServers.Take(1).ToList(), new[] { answer }, isAdmin: true));
    }

    [Fact]
    public void ItemsFromBothServersNameTheirServerAndAdminsCanRemoveThem()
    {
        var merged = Json(ArrQueueMerge.Combine(TwoServers, new[]
        {
            Queue("""{"service":"radarr","inArr":true,"monitored":true,"hasFile":false,"canRemove":true,"items":[{"id":7}]}"""),
            Queue("""{"service":"radarr","inArr":true,"monitored":false,"hasFile":true,"canRemove":true,"items":[{"id":7}]}"""),
        }, isAdmin: true));

        Assert.True((bool?)merged["inArr"]);
        Assert.True((bool?)merged["monitored"]);
        Assert.True((bool?)merged["hasFile"]);
        Assert.True((bool?)merged["canRemove"]);
        Assert.Null(merged["partial"]);
        var items = merged["items"]!.AsArray();
        Assert.Equal(new[] { "1", "2" }, items.Select(i => (string?)i!["server"]));
        Assert.Equal(new[] { "main", "4k" }, items.Select(i => (string?)i!["serverName"]));
        Assert.All(items, i => Assert.True((bool?)i!["canRemove"]));
    }

    [Fact]
    public void UsersNeverGetRemoval()
    {
        var merged = Json(ArrQueueMerge.Combine(TwoServers, new[]
        {
            Queue("""{"inArr":true,"items":[{"id":7}]}"""),
            Queue("""{"inArr":true,"items":[]}"""),
        }, isAdmin: false));

        Assert.False((bool?)merged["canRemove"]);
        Assert.False((bool?)merged["items"]![0]!["canRemove"]);
    }

    [Fact]
    public void ATitleOnlyOnTheSecondServerTakesItsDetails()
    {
        var merged = Json(ArrQueueMerge.Combine(TwoServers, new[]
        {
            Queue("""{"inArr":false,"items":[]}"""),
            Queue("""{"inArr":true,"monitored":true,"releaseDate":"2026-12-01","items":[{"id":3}]}"""),
        }, isAdmin: true));

        Assert.True((bool?)merged["inArr"]);
        Assert.Equal("2026-12-01", (string?)merged["releaseDate"]);
        Assert.True((bool?)merged["canRemove"]);
        Assert.Equal("2", (string?)merged["items"]![0]!["server"]);
    }

    [Fact]
    public void AFoundTitleWithAServerDownIsPartial()
    {
        var merged = Json(ArrQueueMerge.Combine(TwoServers, new[]
        {
            Queue("""{"error":"Could not reach the radarr server"}""", 502),
            Queue("""{"inArr":true,"items":[]}"""),
        }, isAdmin: true));

        Assert.True((bool?)merged["inArr"]);
        Assert.True((bool?)merged["partial"]);
    }

    [Fact]
    public void ATitleFoundNowhereWithAServerDownIsThatServersError()
    {
        var down = Queue("""{"error":"Could not reach the radarr server"}""", 502);

        var answer = ArrQueueMerge.Combine(TwoServers, new[] { Queue("""{"inArr":false,"items":[]}"""), down }, isAdmin: true);

        Assert.Same(down, answer);
    }

    [Fact]
    public void ATitleNoServerHasIsAnEmptyQueue()
    {
        var merged = Json(ArrQueueMerge.Combine(TwoServers, new[]
        {
            Queue("""{"service":"radarr","inArr":false,"canRemove":true,"items":[]}"""),
            Queue("""{"service":"radarr","inArr":false,"canRemove":true,"items":[]}"""),
        }, isAdmin: true));

        Assert.False((bool?)merged["inArr"]);
        Assert.Empty(merged["items"]!.AsArray());
        Assert.True((bool?)merged["canRemove"]);
    }

    [Fact]
    public void WhenEveryServerFailsTheFirstErrorIsHandedBack()
    {
        var first = Queue("""{"error":"a"}""", 502);

        Assert.Same(first, ArrQueueMerge.Combine(TwoServers, new[] { first, Queue("""{"error":"b"}""", 400) }, isAdmin: true));
    }

    private static ArrSend QueueHolding(params int[] ids) => (method, path, body) =>
        Task.FromResult(new ArrAnswer(200, Bytes("{\"records\":[" + string.Join(",", ids.Select(i => "{\"id\":" + i + "}")) + "]}")));

    private static ArrSend Down() => (method, path, body) => Task.FromResult(ArrAnswer.Unreachable);

    private static string Error(ArrAnswer answer) => (string)JsonNode.Parse(answer.Body!)!["error"]!;

    [Fact]
    public async Task ARemovalWithoutAServerGoesToTheServerHoldingTheId()
    {
        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(1, 2), QueueHolding(7) }, isSonarr: false, queueId: 7);

        Assert.Equal(1, index);
        Assert.Null(error);
    }

    [Fact]
    public async Task OneServerNeedsNoLookup()
    {
        var (index, _) = await ArrQueue.LocateAsync(new[] { Down() }, isSonarr: false, queueId: 7);

        Assert.Equal(0, index);
    }

    [Fact]
    public async Task AnIdOnTwoServersIsRefusedRatherThanGuessed()
    {
        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(7), QueueHolding(7) }, isSonarr: true, queueId: 7);

        Assert.Null(index);
        Assert.Equal(409, error!.StatusCode);
    }

    [Fact]
    public async Task AnIdOnNoServerIsTheUsualGoneAnswer()
    {
        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(1), QueueHolding(2) }, isSonarr: false, queueId: 7);

        Assert.Null(index);
        Assert.Equal(404, error!.StatusCode);
        // Clients take this wording for a download that's already gone.
        Assert.Contains("no longer in the", Error(error));
    }

    [Fact]
    public async Task AnIdNotFoundWhileAServerIsDownIsThatServersErrorNotGone()
    {
        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(1), Down() }, isSonarr: false, queueId: 7);

        Assert.Null(index);
        Assert.Equal(502, error!.StatusCode);
        Assert.DoesNotContain("no longer in the", Error(error));
    }

    [Fact]
    public async Task AnIdFoundWhileAnotherServerIsDownIsNotRemoved()
    {
        // The download meant may be the one on the server that couldn't be read.
        var (index, error) = await ArrQueue.LocateAsync(new[] { Down(), QueueHolding(7) }, isSonarr: false, queueId: 7);

        Assert.Null(index);
        Assert.Equal(502, error!.StatusCode);
        Assert.DoesNotContain("no longer in the", Error(error));
    }

    [Fact]
    public async Task AnIdFoundOnAnUnreadableQueueIsNotRemovedEither()
    {
        ArrSend unreadable = (method, path, body) => Task.FromResult(new ArrAnswer(200, Bytes("<html>login</html>")));

        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(7), unreadable }, isSonarr: true, queueId: 7);

        Assert.Null(index);
        Assert.Equal(502, error!.StatusCode);
    }

    [Fact]
    public async Task AnIdOnTwoServersIsRefusedEvenWithAThirdDown()
    {
        var (index, error) = await ArrQueue.LocateAsync(new[] { QueueHolding(7), Down(), QueueHolding(7) }, isSonarr: false, queueId: 7);

        Assert.Null(index);
        Assert.Equal(409, error!.StatusCode);
    }

    [Fact]
    public void QueueIdsFromOtherServersGetTheirOwnStuckKey()
    {
        var primary = JsonNode.Parse("""{"id":7}""")!.AsObject();
        var other = JsonNode.Parse("""{"id":7,"moonfinArrServerId":"2"}""")!.AsObject();
        var withDownload = JsonNode.Parse("""{"id":7,"downloadId":"ABC","moonfinArrServerId":"2"}""")!.AsObject();

        Assert.Equal("radarr:queue-7", StuckDownloads.Key(primary, isSonarr: false));
        Assert.Equal("radarr:queue-2-7", StuckDownloads.Key(other, isSonarr: false));
        Assert.Equal("radarr:abc", StuckDownloads.Key(withDownload, isSonarr: false));
    }

    private static readonly DateTime CheckTime = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static JsonObject BlockedMovie(string downloadId, int tmdbId = 10378) => new JsonObject
    {
        ["id"] = 7,
        ["downloadId"] = downloadId,
        ["trackedDownloadState"] = "importBlocked",
        ["trackedDownloadStatus"] = "warning",
        ["movie"] = new JsonObject { ["title"] = "Big Buck Bunny", ["tmdbId"] = tmdbId },
    };

    private static ArrSend Holding(params JsonObject[] records) => (method, path, body) =>
        Task.FromResult(new ArrAnswer(200, Bytes(new JsonObject
        {
            ["records"] = new JsonArray(records.Select(r => (JsonNode)r.DeepClone()).ToArray()),
            ["totalRecords"] = records.Length,
        }.ToJsonString())));

    [Fact]
    public async Task StuckQueuesMarkTheServerOfNonPrimaryRecordsAndThe4kOnes()
    {
        var queue = await StuckDownloads.ReadServerQueuesAsync(
            new[] { (TwoServers[0], Holding(BlockedMovie("A"))), (TwoServers[1], Holding(BlockedMovie("B"))) },
            isSonarr: false);

        Assert.NotNull(queue);
        Assert.Empty(queue!.Unread);
        var records = queue.Records.OfType<JsonObject>().ToList();
        Assert.Equal(2, records.Count);
        Assert.False(records[0].ContainsKey(ArrServers.ServerProperty));
        Assert.False(records[0].ContainsKey(ArrServers.Is4kProperty));
        Assert.Equal("2", (string)records[1][ArrServers.ServerProperty]!);
        Assert.True((bool)records[1][ArrServers.Is4kProperty]!);
    }

    [Fact]
    public async Task AServerThatCannotBeReadIsListedAndTheOthersAreStillRead()
    {
        var queue = await StuckDownloads.ReadServerQueuesAsync(
            new[] { (TwoServers[0], Holding(BlockedMovie("A"))), (TwoServers[1], Down()) },
            isSonarr: false);
        var primaryDown = await StuckDownloads.ReadServerQueuesAsync(
            new[] { (TwoServers[0], Down()), (TwoServers[1], Holding(BlockedMovie("B"))) },
            isSonarr: false);
        var allDown = await StuckDownloads.ReadServerQueuesAsync(
            new[] { (TwoServers[0], Down()), (TwoServers[1], Down()) },
            isSonarr: false);

        Assert.Equal(new string?[] { "2" }, queue!.Unread);
        Assert.Single(queue.Records);
        Assert.Equal(new string?[] { null }, primaryDown!.Unread);
        Assert.Null(allDown);
    }

    [Fact]
    public void AnUnreadServerKeepsItsOwnRecordsOnlyAndTheReadOnesAreScanned()
    {
        var previous = new Dictionary<string, StuckDownloadRecord>
        {
            ["radarr:main-gone"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1) },
            ["radarr:4k-kept"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1), Server = "2" },
            ["radarr:main-still"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1) },
        };
        var fourKDown = new StuckQueue(new JsonArray(BlockedMovie("MAIN-STILL")), new string?[] { "2" });

        var (next, due) = StuckDownloads.Scan(previous, fourKDown, null, CheckTime, TimeSpan.FromHours(6));

        Assert.False(next.ContainsKey("radarr:main-gone"));
        Assert.Equal("2", next["radarr:4k-kept"].Server);
        Assert.Null(next["radarr:main-still"].Server);
        Assert.Single(due);
    }

    [Fact]
    public void AnUnreadPrimaryKeepsTheRecordsStoredBeforeSeveralServersWereRead()
    {
        var previous = new Dictionary<string, StuckDownloadRecord>
        {
            ["radarr:old"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1) },
            ["radarr:4k-gone"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1), Server = "2" },
        };
        var primaryDown = new StuckQueue(new JsonArray(), new string?[] { null });

        var (next, _) = StuckDownloads.Scan(previous, primaryDown, null, CheckTime, TimeSpan.FromHours(6));

        Assert.True(next.ContainsKey("radarr:old"));
        Assert.False(next.ContainsKey("radarr:4k-gone"));
    }

    [Fact]
    public void ADownloadOnA4kServerIsToldToThe4kRequesterOnly()
    {
        var regularUser = Guid.NewGuid();
        var fourKUser = Guid.NewGuid();
        var requests = JsonNode.Parse($$$"""
            [
              {"is4k":false,"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{regularUser:N}}}"}},
              {"is4k":true,"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{fourKUser:N}}}"}}
            ]
            """)!.AsArray();
        StuckDownload Download(bool? is4k) => new StuckDownload("radarr:x", false, "Big Buck Bunny", 10378, null, System.Array.Empty<int>(), "import blocked", null, is4k);

        Assert.Equal(new[] { fourKUser }, StuckDownloads.Requesters(requests, Download(true)).Users);
        Assert.Equal(new[] { regularUser }, StuckDownloads.Requesters(requests, Download(false)).Users);
        Assert.Equal(new[] { regularUser, fourKUser }, StuckDownloads.Requesters(requests, Download(null)).Users);
    }

    [Fact]
    public void AStuckDownloadKnowsWhetherItIsOnA4kServer()
    {
        var previous = new Dictionary<string, StuckDownloadRecord>
        {
            ["radarr:4k"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1), Server = "2" },
            ["radarr:both"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1) },
            ["radarr:main"] = new StuckDownloadRecord { StuckSince = CheckTime.AddHours(-1) },
        };
        var on4k = BlockedMovie("4K");
        on4k[ArrServers.ServerProperty] = "2";
        on4k[ArrServers.Is4kProperty] = true;
        var both4k = BlockedMovie("BOTH");
        both4k[ArrServers.ServerProperty] = "2";
        both4k[ArrServers.Is4kProperty] = true;
        var queue = new StuckQueue(new JsonArray(on4k, both4k, BlockedMovie("BOTH"), BlockedMovie("MAIN")), System.Array.Empty<string?>());

        var due = StuckDownloads.Scan(previous, queue, null, CheckTime, TimeSpan.FromHours(6)).Due.ToDictionary(d => d.Key);

        Assert.True(due["radarr:4k"].Is4k);
        Assert.Null(due["radarr:both"].Is4k);
        Assert.False(due["radarr:main"].Is4k);
    }

    [Fact]
    public void Leaving4kOutIsItsOwnCacheEntry()
    {
        var today = new DateTime(2026, 11, 25);

        Assert.NotEqual(
            ArrCalendarWindow.CacheKey(true, today, today, false, include4k: true),
            ArrCalendarWindow.CacheKey(true, today, today, false, include4k: false));
        Assert.Equal(
            ArrCalendarWindow.CacheKey(true, today, today, false),
            ArrCalendarWindow.CacheKey(true, today, today, false, include4k: true));
    }
}
