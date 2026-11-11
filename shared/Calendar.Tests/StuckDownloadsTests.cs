using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Calendar.Tests;

public class StuckDownloadsTests
{
    private static readonly DateTime Start = new DateTime(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(6);
    private static readonly Guid Admin = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid Viewer = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private static JsonObject Movie(
        string downloadId = "ABC",
        string state = "importBlocked",
        string? status = "warning",
        string? message = "Caution: Found executable file with extension: '.exe'",
        string? error = null,
        long sizeLeft = 0) =>
        new JsonObject
        {
            ["id"] = 7,
            ["title"] = "Big.Buck.Bunny.2008.1080p-GRP",
            ["downloadId"] = downloadId,
            ["trackedDownloadState"] = state,
            ["trackedDownloadStatus"] = status,
            ["errorMessage"] = error,
            ["sizeleft"] = sizeLeft,
            ["statusMessages"] = message == null
                ? new JsonArray()
                : new JsonArray(new JsonObject { ["title"] = "Big.Buck.Bunny.2008.1080p-GRP", ["messages"] = new JsonArray(message) }),
            ["movie"] = new JsonObject { ["title"] = "Big Buck Bunny", ["tmdbId"] = 10378 },
        };

    private static JsonObject Episode(int id, int season, int episode, string downloadId = "PACK") =>
        new JsonObject
        {
            ["id"] = id,
            ["downloadId"] = downloadId,
            ["trackedDownloadState"] = "importBlocked",
            ["trackedDownloadStatus"] = "warning",
            ["seasonNumber"] = season,
            ["statusMessages"] = new JsonArray(new JsonObject { ["messages"] = new JsonArray("Found matching series via grab history, but release was matched to series by ID") }),
            ["series"] = new JsonObject { ["title"] = "Sintel Show", ["tvdbId"] = 81189, ["tmdbId"] = 1396 },
            ["episode"] = new JsonObject { ["seasonNumber"] = season, ["episodeNumber"] = episode },
        };

    private static JsonArray Queue(params JsonObject[] records) => new JsonArray(records.Select(r => (JsonNode)r.DeepClone()).ToArray());

    private static (Dictionary<string, StuckDownloadRecord> Next, List<StuckDownload> Due) Scan(
        Dictionary<string, StuckDownloadRecord> previous, JsonArray? radarr, JsonArray? sonarr, DateTime now) =>
        StuckDownloads.Scan(previous, radarr, sonarr, now, Threshold);

    [Fact]
    public void AnImportBlockedDownloadIsOnlyToldAboutWhenTheNextCheckStillFindsItBlocked()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(), Start);
        Assert.Empty(first.Due);
        Assert.Equal(Start, first.Next["radarr:abc"].StuckSince);

        var second = Scan(first.Next, Queue(Movie()), Queue(), Start.AddMinutes(15));
        var due = Assert.Single(second.Due);
        Assert.Equal("Big Buck Bunny", due.Title);
        Assert.Equal(10378, due.TmdbId);
        Assert.Equal("import blocked", due.Reason);
        Assert.Equal("Big Buck Bunny is stuck: import blocked, Caution: Found executable file with extension: '.exe'", StuckDownloads.Body(due, redact: false));
    }

    [Fact]
    public void ACheckRunByHandRightAfterDoesNotCountAsTheNextCheck()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(), Start);
        var rerun = Scan(first.Next, Queue(Movie()), Queue(), Start.AddMinutes(2));

        Assert.Empty(rerun.Due);
        Assert.Equal(Start, rerun.Next["radarr:abc"].StuckSince);
    }

    [Fact]
    public void ADownloadAlreadyToldAboutIsNotDueAgain()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(), Start);
        var second = Scan(first.Next, Queue(Movie()), Queue(), Start.AddMinutes(15));
        second.Next["radarr:abc"].NotifiedAt = Start.AddMinutes(15);
        var third = Scan(second.Next, Queue(Movie()), Queue(), Start.AddMinutes(30));

        Assert.Single(second.Due);
        Assert.Empty(third.Due);
        Assert.Equal(Start.AddMinutes(15), third.Next["radarr:abc"].NotifiedAt);
    }

    [Fact]
    public void AnImportThatTheArrRetriesAndGetsGoingIsNeverToldAbout()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(), Start);
        var second = Scan(first.Next, Queue(Movie(state: "importPending", status: "ok", message: null)), Queue(), Start.AddMinutes(15));

        Assert.Empty(second.Due);
        Assert.False(second.Next.ContainsKey("radarr:abc"));
    }

    [Fact]
    public void AnImportTheArrKeepsRetryingWithAWarningIsBlocked()
    {
        // What Radarr 6 shows for a release whose files it rejects: importPending, retried every minute.
        JsonObject Rejected() => Movie(state: "importPending", message: "Caution: Found executable file");

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Rejected()), Queue(), Start);
        var second = Scan(first.Next, Queue(Rejected()), Queue(), Start.AddMinutes(15));

        Assert.Equal("Big Buck Bunny is stuck: import blocked, Caution: Found executable file", StuckDownloads.Body(Assert.Single(second.Due), redact: false));
    }

    [Fact]
    public void AnImportThatIsJustWaitingIsNotBlocked()
    {
        var scan = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie(state: "importPending", status: "ok", message: null)), Queue(), Start);

        Assert.Empty(scan.Next);
    }

    [Theory]
    [InlineData("failedPending")]
    [InlineData("failed")]
    public void AFailedDownloadIsStuck(string state)
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie(state: state, message: null, error: "Download failed")), Queue(), Start);
        var second = Scan(first.Next, Queue(Movie(state: state, message: null, error: "Download failed")), Queue(), Start.AddMinutes(15));

        var due = Assert.Single(second.Due);
        Assert.Equal("download failed", due.Reason);
        Assert.Equal("Download failed", due.Detail);
    }

    [Fact]
    public void ANewReleaseThatGetsStuckIsToldAboutAgain()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(), Start);
        var second = Scan(first.Next, Queue(Movie()), Queue(), Start.AddMinutes(15));
        second.Next["radarr:abc"].NotifiedAt = Start.AddMinutes(15);
        var swapped = Scan(second.Next, Queue(Movie(downloadId: "DEF")), Queue(), Start.AddMinutes(30));
        var again = Scan(swapped.Next, Queue(Movie(downloadId: "DEF")), Queue(), Start.AddMinutes(45));

        Assert.False(swapped.Next.ContainsKey("radarr:abc"));
        Assert.Equal("radarr:def", Assert.Single(again.Due).Key);
    }

    [Fact]
    public void TheEpisodesOfASeasonPackAreOneDownload()
    {
        var pack = Queue(Episode(1, 2, 1), Episode(2, 2, 2), Episode(3, 2, 3));
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(), pack, Start);
        var second = Scan(first.Next, Queue(), pack, Start.AddMinutes(15));

        var due = Assert.Single(second.Due);
        Assert.Equal("sonarr:pack", due.Key);
        Assert.Equal(new[] { 2 }, due.Seasons);
        Assert.Equal(81189, due.TvdbId);
        Assert.StartsWith("Sintel Show season 2 is stuck: import blocked, ", StuckDownloads.Body(due, redact: false));
    }

    [Fact]
    public void AStalledDownloadIsStuckOnceItHasNotMovedForTheThreshold()
    {
        const string Stalled = "The download is stalled with no connections";
        JsonObject Download() => Movie(state: "downloading", message: null, error: Stalled, sizeLeft: 500);

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Download()), Queue(), Start);
        var early = Scan(first.Next, Queue(Download()), Queue(), Start.AddHours(5).AddMinutes(45));
        var late = Scan(early.Next, Queue(Download()), Queue(), Start.AddHours(6));

        Assert.Empty(first.Due);
        Assert.Empty(early.Due);
        var due = Assert.Single(late.Due);
        Assert.Equal("no progress for 6 hours", due.Reason);
        Assert.Equal("Big Buck Bunny is stuck: no progress for 6 hours, " + Stalled, StuckDownloads.Body(due, redact: false));
    }

    [Fact]
    public void AWarningOnADownloadThatKeepsMovingStartsTheClockAgain()
    {
        JsonObject Slow(long left) => Movie(state: "downloading", message: "Slow tracker", sizeLeft: left);

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Slow(1000)), Queue(), Start);
        var moved = Scan(first.Next, Queue(Slow(900)), Queue(), Start.AddHours(5));
        var later = Scan(moved.Next, Queue(Slow(900)), Queue(), Start.AddHours(7));

        Assert.Equal(Start.AddHours(5), moved.Next["radarr:abc"].StalledSince);
        Assert.Empty(later.Due);
        Assert.Single(Scan(later.Next, Queue(Slow(900)), Queue(), Start.AddHours(11)).Due);
    }

    [Fact]
    public void ATorrentTheClientWarnsAboutCountsAsStalled()
    {
        // What Radarr shows for a magnet qBittorrent can't fetch: the arr itself says ok.
        var download = Movie(state: "downloading", status: "ok", message: null, error: "qBittorrent cannot resolve magnet link with DHT disabled");
        download["status"] = "warning";

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(download), Queue(), Start);
        var late = Scan(first.Next, Queue(download), Queue(), Start.AddHours(6));

        Assert.Equal("Big Buck Bunny is stuck: no progress for 6 hours, qBittorrent cannot resolve magnet link with DHT disabled", StuckDownloads.Body(Assert.Single(late.Due), redact: false));
    }

    [Fact]
    public void AStalledTorrentThatFlipsToDownloadingWithoutAByteKeepsItsClock()
    {
        JsonObject Stalled() => Movie(state: "downloading", status: "ok", message: null, error: "The download is stalled with no connections", sizeLeft: 500);
        JsonObject Peer() => Movie(state: "downloading", status: "ok", message: null, sizeLeft: 500);

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Stalled()), Queue(), Start);
        var flipped = Scan(first.Next, Queue(Peer()), Queue(), Start.AddHours(3));
        var back = Scan(flipped.Next, Queue(Stalled()), Queue(), Start.AddHours(6));

        Assert.Equal(Start, flipped.Next["radarr:abc"].StalledSince);
        Assert.Empty(flipped.Due);
        Assert.Single(back.Due);
    }

    [Fact]
    public void AStalledTorrentThatStaysDownloadingWithoutAByteIsStillToldAbout()
    {
        JsonObject Stalled() => Movie(state: "downloading", status: "ok", message: null, error: "The download is stalled with no connections", sizeLeft: 500);
        JsonObject Peer() => Movie(state: "downloading", status: "ok", message: null, sizeLeft: 500);

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Stalled()), Queue(), Start);
        var flipped = Scan(first.Next, Queue(Peer()), Queue(), Start.AddHours(3));
        var late = Scan(flipped.Next, Queue(Peer()), Queue(), Start.AddHours(6));

        Assert.Empty(flipped.Due);
        Assert.Equal("Big Buck Bunny is stuck: no progress for 6 hours", StuckDownloads.Body(Assert.Single(late.Due), redact: false));
    }

    [Fact]
    public void AStalledTorrentTheClientThenPausesIsNotToldAbout()
    {
        JsonObject Stalled() => Movie(state: "downloading", status: "ok", message: null, error: "The download is stalled with no connections", sizeLeft: 500);
        var paused = Movie(state: "downloading", status: "ok", message: null, sizeLeft: 500);
        paused["status"] = "paused";

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Stalled()), Queue(), Start);
        var late = Scan(first.Next, Queue(paused), Queue(), Start.AddHours(7));

        Assert.Empty(late.Due);
    }

    [Fact]
    public void ADownloadThatGetsGoingAgainIsDropped()
    {
        JsonObject Stalled() => Movie(state: "downloading", status: "ok", message: null, error: "The download is stalled with no connections", sizeLeft: 500);

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Stalled()), Queue(), Start);
        var moving = Scan(first.Next, Queue(Movie(state: "downloading", status: "ok", message: null, sizeLeft: 100)), Queue(), Start.AddHours(1));

        Assert.Empty(moving.Next);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("paused")]
    [InlineData("delay")]
    [InlineData("downloadClientUnavailable")]
    public void ADownloadTheClientIsHoldingBackIsNotStalled(string status)
    {
        var held = Movie(state: "downloading", message: "Found matching movie via grab history, but release was matched to movie by ID", sizeLeft: 500);
        held["status"] = status;

        Assert.Empty(Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(held), Queue(), Start).Next);
    }

    [Fact]
    public void AnEntryWithoutADownloadIdIsKeptByItsQueueId()
    {
        var entry = Movie();
        entry.Remove("downloadId");

        var scan = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(entry), Queue(), Start);

        Assert.True(scan.Next.ContainsKey("radarr:queue-7"));
    }

    [Fact]
    public void ASonarrV3SeasonUnderTheEpisodeAndSizesSentAsDecimalsAreRead()
    {
        var entry = Episode(1, 4, 1);
        entry.Remove("seasonNumber");
        entry["trackedDownloadState"] = "downloading";
        entry["errorMessage"] = "The download is stalled with no connections";
        entry["sizeleft"] = 1234.0;

        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(), Queue(entry), Start);
        var late = Scan(first.Next, Queue(), Queue(entry), Start.AddHours(6));

        Assert.Equal(1234, first.Next["sonarr:pack"].SizeLeft);
        Assert.Equal(new[] { 4 }, Assert.Single(late.Due).Seasons);
    }

    [Fact]
    public void AnEntryWithNoStateAtAllIsNotBlocked()
    {
        var entry = Movie(status: "ok", message: null);
        entry.Remove("trackedDownloadState");

        Assert.Empty(Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(entry), Queue(), Start).Next);
    }

    [Fact]
    public void ADownloadThatIsJustDownloadingIsNotKept()
    {
        var scan = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie(state: "downloading", status: "ok", message: null)), Queue(), Start);

        Assert.Empty(scan.Next);
    }

    [Fact]
    public void AQueueThatCouldNotBeReadKeepsWhatWasKnownAboutIt()
    {
        var first = Scan(new Dictionary<string, StuckDownloadRecord>(), Queue(Movie()), Queue(Episode(1, 1, 1)), Start);
        var second = Scan(first.Next, null, Queue(), Start.AddMinutes(15));

        Assert.True(second.Next.ContainsKey("radarr:abc"));
        Assert.False(second.Next.ContainsKey("sonarr:pack"));
    }

    [Fact]
    public void ARequesterIsFoundByTheTitleIdAnd4kAndOtherSeasonsAreLeftOut()
    {
        var requests = JsonNode.Parse($$$"""
            [
              {"is4k":false,"media":{"mediaType":"tv","tmdbId":1396,"tvdbId":81189},"seasons":[{"seasonNumber":2}],"requestedBy":{"jellyfinUserId":"{{{Viewer:N}}}"}},
              {"is4k":true,"media":{"mediaType":"tv","tmdbId":1396,"tvdbId":81189},"requestedBy":{"jellyfinUserId":"{{{Guid.NewGuid():N}}}"}},
              {"is4k":false,"media":{"mediaType":"tv","tmdbId":1396,"tvdbId":81189},"seasons":[{"seasonNumber":3}],"requestedBy":{"jellyfinUserId":"{{{Guid.NewGuid():N}}}"}},
              {"is4k":false,"media":{"mediaType":"movie","tmdbId":81189},"requestedBy":{"jellyfinUserId":"{{{Guid.NewGuid():N}}}"}},
              {"is4k":false,"media":{"mediaType":"tv","tvdbId":81189},"requestedBy":{}},
              {"is4k":false,"media":{"mediaType":"tv","tvdbId":81189},"requestedBy":{"jellyfinUserId":"{{{Admin:D}}}"}}
            ]
            """)!.AsArray();
        var download = new StuckDownload("sonarr:pack", true, "Sintel Show", null, 81189, new[] { 2 }, "import blocked", null);

        var (users, tmdbId) = StuckDownloads.Requesters(requests, download);

        Assert.Equal(new[] { Viewer, Admin }, users);
        Assert.Equal(1396, tmdbId);
        Assert.Equal("/seerr/media/1396?mediaType=tv", StuckDownloads.Route(download, tmdbId));
    }

    [Fact]
    public void WithoutATmdbIdTheRouteIsTheRequestsList()
    {
        var download = new StuckDownload("sonarr:x", true, "Show", null, 1, Array.Empty<int>(), "import blocked", null);

        Assert.Equal("/seerr/requests", StuckDownloads.Route(download, null));
    }

    [Fact]
    public void ServerPathsAreCutForNonAdminsAndALongMessageIsShortenedForThemOnly()
    {
        var download = new StuckDownload("radarr:x", false, "Movie", 1, null, Array.Empty<int>(), "import blocked", "No files found are eligible for import in /downloads/complete/Movie.2008");
        var longOne = new StuckDownload("radarr:y", false, "Movie", 1, null, Array.Empty<int>(), "import blocked", new string('x', 400));

        Assert.EndsWith("eligible for import in /downloads/complete/Movie.2008", StuckDownloads.Body(download, redact: false));
        Assert.EndsWith("eligible for import in Movie.2008", StuckDownloads.Body(download, redact: true));
        Assert.EndsWith("...", StuckDownloads.Body(longOne, redact: true));
        Assert.True(StuckDownloads.Body(longOne, redact: true).Length < 220);
        Assert.EndsWith(new string('x', 400), StuckDownloads.Body(longOne, redact: false));
    }

    [Fact]
    public async Task ARunTellsTheAdminsInFullAndTheRequesterWithoutPathsOnce()
    {
        var files = new MemoryStateFile();
        var store = new StuckDownloadStore("seerr-stuck-downloads.json", files);
        var blocked = Movie(message: "No files found are eligible for import in /downloads/complete/Big.Buck.Bunny");
        var sent = new List<(Guid User, string Title, string Body, string PushBody, string Route)>();
        var requests = $$$"""{"pageInfo":{"pages":1},"results":[{"is4k":false,"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{Viewer:N}}}"}},{"is4k":false,"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{Admin:N}}}"}}]}""";
        var seerrCalls = 0;

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? Queue() : Queue(blocked)),
            SeerrGet = path =>
            {
                seerrCalls++;
                return Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes(requests)));
            },
            Admins = () => new[] { Admin },
            Deliver = (user, title, body, pushBody, route) => sent.Add((user, title, body, pushBody, route)),
            NowUtc = now,
        };

        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start), store));
        Assert.Equal(0, seerrCalls);
        Assert.Equal(1, await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store));
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(30)), store));

        Assert.Equal(2, sent.Count);
        var toAdmin = sent.Single(s => s.User == Admin);
        var toViewer = sent.Single(s => s.User == Viewer);
        Assert.Equal("Download stuck", toAdmin.Title);
        Assert.EndsWith("in /downloads/complete/Big.Buck.Bunny", toAdmin.Body);
        Assert.EndsWith("in Big.Buck.Bunny", toAdmin.PushBody);
        Assert.EndsWith("in Big.Buck.Bunny", toViewer.Body);
        Assert.EndsWith("in Big.Buck.Bunny", toViewer.PushBody);
        Assert.Equal("/seerr/media/10378?mediaType=movie", toViewer.Route);
    }

    [Fact]
    public async Task ARequesterWhoTurnedRequestUpdatesOffIsLeftOut()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        var sent = new List<Guid>();
        var requests = $$$"""{"results":[{"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{Viewer:N}}}"}}]}""";

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? null : Queue(Movie())),
            SeerrGet = _ => Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes(requests))),
            Admins = () => new[] { Admin },
            RequesterWantsIt = _ => false,
            Deliver = (user, _, _, _, _) => sent.Add(user),
            NowUtc = now,
        };

        await StuckDownloads.RunAsync(Run(Start), store);
        await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store);

        Assert.Equal(new[] { Admin }, sent);
    }

    [Fact]
    public async Task WhenSeerrCannotBeReadNobodyIsToldUntilTheNextCheck()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        var sent = new List<Guid>();
        var seerrUp = false;
        var requests = $$$"""{"results":[{"media":{"mediaType":"movie","tmdbId":10378},"requestedBy":{"jellyfinUserId":"{{{Viewer:N}}}"}}]}""";

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? Queue() : Queue(Movie())),
            SeerrGet = _ => Task.FromResult(seerrUp ? new ArrAnswer(200, Encoding.UTF8.GetBytes(requests)) : ArrAnswer.Unreachable),
            Admins = () => new[] { Admin },
            Deliver = (user, _, _, _, _) => sent.Add(user),
            NowUtc = now,
        };

        await StuckDownloads.RunAsync(Run(Start), store);
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store));
        Assert.Empty(sent);

        seerrUp = true;
        Assert.Equal(1, await StuckDownloads.RunAsync(Run(Start.AddMinutes(30)), store));
        Assert.Equal(new[] { Admin, Viewer }, sent);
    }

    [Fact]
    public async Task ADownloadNobodyCouldBeToldAboutIsTriedAgain()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        var admins = Array.Empty<Guid>();
        var sent = new List<Guid>();

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? Queue() : Queue(Movie())),
            SeerrGet = _ => Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("""{"results":[]}"""))),
            Admins = () => admins,
            Deliver = (user, _, _, _, _) => sent.Add(user),
            NowUtc = now,
        };

        await StuckDownloads.RunAsync(Run(Start), store);
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store));

        admins = new[] { Admin };
        Assert.Equal(1, await StuckDownloads.RunAsync(Run(Start.AddMinutes(30)), store));
        Assert.Equal(new[] { Admin }, sent);
    }

    [Fact]
    public async Task OneRecipientThatFailsIsTriedAgainWithoutTellingTheOthersTwice()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        var other = Guid.NewGuid();
        var sent = new List<Guid>();
        var adminFails = true;

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? Queue() : Queue(Movie())),
            SeerrGet = _ => Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("""{"results":[]}"""))),
            Admins = () => new[] { Admin, other },
            Deliver = (user, _, _, _, _) =>
            {
                if (user == Admin && adminFails) throw new InvalidOperationException("socket closed");
                sent.Add(user);
            },
            NowUtc = now,
        };

        await StuckDownloads.RunAsync(Run(Start), store);
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store));
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(30)), store));
        Assert.Equal(new[] { other }, sent);
        Assert.Null(store.Load()["radarr:abc"].NotifiedAt);

        adminFails = false;
        Assert.Equal(1, await StuckDownloads.RunAsync(Run(Start.AddMinutes(45)), store));
        Assert.Equal(0, await StuckDownloads.RunAsync(Run(Start.AddMinutes(60)), store));

        Assert.Equal(new[] { other, Admin }, sent);
        var record = store.Load()["radarr:abc"];
        Assert.Equal(Start.AddMinutes(45), record.NotifiedAt);
        Assert.Null(record.Told);
    }

    [Fact]
    public async Task ARunCancelledMidwayKeepsWhatItAlreadySent()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        var other = Guid.NewGuid();
        var sent = new List<Guid>();
        using var cancel = new System.Threading.CancellationTokenSource();

        StuckDownloadRun Run(DateTime now) => new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(isSonarr ? Queue() : Queue(Movie())),
            SeerrGet = _ => Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("""{"results":[]}"""))),
            Admins = () => new[] { Admin, other },
            Deliver = (user, _, _, _, _) =>
            {
                sent.Add(user);
                cancel.Cancel();
            },
            NowUtc = now,
            Cancellation = cancel.Token,
        };

        await StuckDownloads.RunAsync(Run(Start), store);
        await StuckDownloads.RunAsync(Run(Start.AddMinutes(15)), store);

        Assert.Equal(new[] { Admin }, sent);
        Assert.Equal(new[] { Admin }, store.Load()["radarr:abc"].Told);
    }

    [Fact]
    public async Task ACancelledRunSavesNothing()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        using var cancel = new System.Threading.CancellationTokenSource();
        cancel.Cancel();

        await StuckDownloads.RunAsync(new StuckDownloadRun
        {
            ReadQueue = isSonarr => Task.FromResult<JsonArray?>(Queue(Movie())),
            NowUtc = Start,
            Cancellation = cancel.Token,
        }, store);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void TheStoreKeepsItsTimesAcrossAReload()
    {
        var store = new StuckDownloadStore("s.json", new MemoryStateFile());
        store.Save(new Dictionary<string, StuckDownloadRecord> { ["radarr:abc"] = new StuckDownloadRecord { StuckSince = Start, SizeLeft = 5 } });

        var back = store.Load()["radarr:abc"];

        Assert.Equal(Start, back.StuckSince);
        Assert.Equal(DateTimeKind.Utc, back.StuckSince!.Value.Kind);
        Assert.Equal(5, back.SizeLeft);
    }

    [Fact]
    public void AnUnreadableStoreStartsEmpty()
    {
        var files = new MemoryStateFile();
        files.Write("s.json", "{not json");

        Assert.Empty(new StuckDownloadStore("s.json", files).Load());
    }

    [Fact]
    public async Task SeerrRequestsAreReadPageByPage()
    {
        var asked = new List<string>();
        Task<ArrAnswer> Get(string path)
        {
            asked.Add(path);
            var count = path.Contains("skip=0") ? 100 : 3;
            var results = string.Join(",", Enumerable.Range(0, count).Select(_ => "{}"));
            return Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("{\"results\":[" + results + "]}")));
        }

        var all = await StuckDownloads.ReadProcessingRequestsAsync(Get);

        Assert.Equal(103, all!.Count);
        Assert.Equal(new[] { "request?take=100&skip=0&filter=processing&sort=added", "request?take=100&skip=100&filter=processing&sort=added" }, asked);
    }

    [Fact]
    public async Task SeerrRequestsCountAsUnreadWhenAnyPageFailsOrThereAreTooMany()
    {
        var fullPage = new ArrAnswer(200, Encoding.UTF8.GetBytes("{\"results\":[" + string.Join(",", Enumerable.Repeat("{}", 100)) + "]}"));
        Task<ArrAnswer> Get(string path) => Task.FromResult(path.Contains("skip=0") ? fullPage : ArrAnswer.Unreachable);

        Assert.Null(await StuckDownloads.ReadProcessingRequestsAsync(Get));
        Assert.Null(await StuckDownloads.ReadProcessingRequestsAsync(_ => Task.FromResult(ArrAnswer.Unreachable)));
        Assert.Null(await StuckDownloads.ReadProcessingRequestsAsync(_ => Task.FromResult(fullPage)));
    }

    [Fact]
    public async Task ALargeQueueIsReadPageByPageAndAPartialReadCountsAsUnread()
    {
        var asked = new List<string>();
        var failPageTwo = false;
        Task<ArrAnswer> Send(HttpMethod method, string path, byte[]? body)
        {
            asked.Add(path);
            var page = path.Contains("page=1&") ? 1000 : 5;
            if (failPageTwo && page == 5) return Task.FromResult(ArrAnswer.Unreachable);
            var records = string.Join(",", Enumerable.Range(0, page).Select(i => "{\"id\":" + i + "}"));
            return Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("{\"totalRecords\":1005,\"records\":[" + records + "]}")));
        }

        Assert.Equal(1005, (await StuckDownloads.ReadQueueAsync(Send, isSonarr: true))!.Count);
        Assert.Contains("api/v3/queue?page=2&pageSize=1000&includeSeries=true&includeEpisode=true", asked);

        failPageTwo = true;
        Assert.Null(await StuckDownloads.ReadQueueAsync(Send, isSonarr: true));

        using var cancel = new System.Threading.CancellationTokenSource();
        cancel.Cancel();
        asked.Clear();
        Assert.Null(await StuckDownloads.ReadQueueAsync(Send, isSonarr: true, cancel.Token));
        Assert.Empty(asked);
    }

    [Fact]
    public async Task TheWholeQueueIsReadWithTheTitleOnEachEntry()
    {
        var asked = new List<string>();
        Task<ArrAnswer> Send(HttpMethod method, string path, byte[]? body)
        {
            asked.Add(path);
            return Task.FromResult(new ArrAnswer(200, Encoding.UTF8.GetBytes("{\"records\":[{\"id\":1}]}")));
        }

        Assert.Single((await StuckDownloads.ReadQueueAsync(Send, isSonarr: false))!);
        Assert.Single((await StuckDownloads.ReadQueueAsync(Send, isSonarr: true))!);
        Assert.Equal(new[]
        {
            "api/v3/queue?page=1&pageSize=1000&includeMovie=true",
            "api/v3/queue?page=1&pageSize=1000&includeSeries=true&includeEpisode=true",
        }, asked);
        Assert.Null(await StuckDownloads.ReadQueueAsync((_, _, _) => Task.FromResult(ArrAnswer.Unreachable), isSonarr: false));
    }

    private sealed class MemoryStateFile : IStateFile
    {
        private readonly Dictionary<string, string> _files = new Dictionary<string, string>();

        public T? Read<T>(string path, Func<string, T?> parse)
            where T : class => _files.TryGetValue(path, out var text) ? parse(text) : null;

        public void Write(string path, string contents) => _files[path] = contents;
    }
}
