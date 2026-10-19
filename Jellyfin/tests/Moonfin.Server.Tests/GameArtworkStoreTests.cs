using System.Diagnostics;
using System.Net;
using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

// ---- GameArtworkStore's download path: size cap, temp-file isolation, in-flight lifetime -------
//
// These drive the store directly (rather than through GameThumbService) so the byte cap can be
// dialed down to a few hundred bytes via the constructor's test seam, and so a single candidate
// name is probed per lookup: no RdbService config is supplied, so ResolveArtworkNameAsync
// short-circuits to null and the only candidate is the ROM's own file name.
public class GameArtworkStoreTests : IDisposable
{
    private const string Platform = "Sega - Master System - Mark III";
    private const string RomFileName = "Great Golf.sms";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-artwork-store-tests-{Guid.NewGuid():N}");

    private const string RommCoverUrl = "http://romm.test:8080/assets/romm/resources/roms/9/11/cover/big.png?ts=1";

    [Fact]
    public async Task ARommCoverIsUsedBeforeAnyLibretroName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(RommCoverUrl, HttpStatusCode.OK, new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        var store = CreateStore(handler);

        var result = await LookupRommAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        Assert.Equal(new[] { RommCoverUrl }, handler.Requested);
    }

    [Fact]
    public async Task AMissingRommCoverFallsBackToLibretro()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(RommCoverUrl, HttpStatusCode.NotFound, new ByteArrayContent(Array.Empty<byte>()));
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.OK, new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        var store = CreateStore(handler);

        var result = await LookupRommAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        Assert.Equal(new[] { RommCoverUrl, ThumbUrl(RomFileName) }, handler.Requested);
    }

    [Fact]
    public async Task ARommOutageIsRetriedRatherThanRecordedMissing()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(RommCoverUrl, HttpStatusCode.ServiceUnavailable, new ByteArrayContent(Array.Empty<byte>()));
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.NotFound, new ByteArrayContent(Array.Empty<byte>()));
        var store = CreateStore(handler);

        var result = await LookupRommAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.TransientFailure, result.Outcome);
    }

    // A RomM game passes its served file name where a folder game passes a path, since nothing
    // of it is on this server's disk.
    private Task<GameArtworkLookupResult> LookupRommAsync(GameArtworkStore store) =>
        store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "sms",
            romPath: RomFileName,
            title: null,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None,
            remoteUrl: RommCoverUrl);

    [Fact]
    public async Task Download_RejectsABodyWhoseDeclaredLengthExceedsTheCap()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.OK, new ByteArrayContent(new byte[4096]));
        var store = CreateStore(handler, maxArtworkBytes: 1024);

        var result = await LookupAsync(store);

        // Transient, not Missing: an oversized body means a broken or hostile origin, not "this
        // game has no art" -- a Missing would be negatively cached by the client forever.
        Assert.Equal(GameArtworkLookupOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.Path);
        AssertNothingCached();
    }

    [Fact]
    public async Task Download_RejectsABodyThatExceedsTheCapWithNoDeclaredLength()
    {
        // Content-Length can be absent or simply lie, so the cap has to be enforced against the
        // bytes actually delivered. StreamContent over a non-seekable stream declares no length.
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(
            ThumbUrl(RomFileName),
            HttpStatusCode.OK,
            new StreamContent(new UnseekableStream(new byte[4096])));
        var store = CreateStore(handler, maxArtworkBytes: 1024);

        var result = await LookupAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.TransientFailure, result.Outcome);
        AssertNothingCached();
    }

    [Fact]
    public async Task Download_WritesToAUniquePerAttemptTempPath()
    {
        // Observed mid-transfer: whatever partial file is on disk while the body is still
        // streaming must not be the shared "<localPath>.tmp" that a concurrent attempt at the same
        // artwork would also open.
        string[] tempFilesDuringTransfer = Array.Empty<string>();
        var body = new UnseekableStream(
            new byte[64],
            afterFirstRead: () => tempFilesDuringTransfer = Directory.GetFiles(_root, "*.tmp"));
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.OK, new StreamContent(body));
        var store = CreateStore(handler);

        var result = await LookupAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        var temp = Assert.Single(tempFilesDuringTransfer);
        Assert.NotEqual(result.Path + ".tmp", temp);
        Assert.Matches(@"\.[0-9a-f]{32}\.tmp$", temp);
        Assert.StartsWith(result.Path + ".", temp, StringComparison.Ordinal);

        // The promotion is atomic and leaves nothing behind.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task Download_IsNotRestartedWhileTheFirstAttemptIsStillRunning()
    {
        // The awaiter giving up (here by cancellation; in production by the 3s overall budget) must
        // not evict the in-flight entry, or the retry starts a second concurrent download of the
        // same URL -- two writers racing to promote the same cache file.
        var gate = new SemaphoreSlim(0);
        var body = new UnseekableStream(new byte[64], blockAfterFirstReadOn: gate);
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.OK, new StreamContent(body));
        var store = CreateStore(handler);

        using var abandoned = new CancellationTokenSource();
        var first = LookupAsync(store, abandoned.Token);
        await WaitUntil(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // Download still running (the body is parked on the gate), so the retry must attach to it.
        Assert.Empty(Directory.GetFiles(_root, "*.png"));
        var second = LookupAsync(store);
        await Task.Delay(100);
        Assert.Equal(1, handler.RequestCount);

        gate.Release();
        var result = await second;
        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Lookup_ColdFbneoIndexWithRawShortnameMissIsRetryable()
    {
        const string fbneoPlatform = "FBNeo - Arcade Games";
        const string shortName = "gauntlet2p";
        var romPath = Path.Combine(_root, shortName + ".zip");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(romPath, [2, 3, 5, 7]);

        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(fbneoPlatform, shortName), HttpStatusCode.NotFound, new ByteArrayContent([]));
        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root)
        {
            // No local RDB: the resolver starts acquisition and reports IndexPending. The raw
            // shortname really is absent upstream, but that cannot become a durable catalog miss.
            ConfigOverrideForTests = new global::Moonfin.Server.PluginConfiguration
            {
                GamesMetadataDbUrlBase = string.Empty,
            },
        };
        var store = new GameArtworkStore(
            new FakeHttpClientFactory(handler),
            new NoOpLogger<GameArtworkStore>(),
            rdb,
            _root);

        var result = await store.LookupThumbAsync(
            core: "arcade",
            coreWasDefaulted: false,
            systemName: "Arcade",
            romPath: romPath,
            title: shortName,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None);

        Assert.Equal(GameArtworkLookupOutcome.MetadataPending, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    // ---- Candidate budget: the fallbacks must survive a maximum-sized RDB match ---------------
    //
    // MatchWithSiblings returns the primary plus up to MaxSiblingArtworkNamesPerRequest siblings,
    // so a densely-catalogued title contributes 7 asserted names -- and with the 2 fallbacks that
    // is exactly the 9-probe budget. This guards that equality: lowering the budget, or adding a
    // candidate pass in front of the fallbacks (a `~` -> `-` spelling guess was tried and removed),
    // starves the fallbacks and records a terminal Missing for artwork that exists.
    [Fact]
    public async Task Lookup_MaximumSiblingSet_StillProbesTheFilenameAndTitleFallbacks()
    {
        // The ROM is identified by CRC, so neither its file name nor its title collides with any
        // RDB name: 7 asserted names + 2 distinct fallbacks is the full 9-candidate worst case.
        var romBytes = new byte[] { 4, 8, 15, 16, 23, 42 };
        var records = new List<(string Name, uint Crc)> { ("Maze Craze (USA)", Crc32(romBytes)) };
        var siblingRegions = new[] { "USA, Europe", "Europe", "Japan", "World", "Brazil", "Korea", "Italy", "Taiwan", "France", "Germany" };
        for (var i = 0; i < siblingRegions.Length; i++)
        {
            records.Add(($"Maze Craze ({siblingRegions[i]})", (uint)(0x22222222u + i)));
        }

        var handler = new FakeHttpMessageHandler();

        // Every asserted name and the filename fallback 404. The handler throws on any unregistered
        // URL, so a candidate pass inserted ahead of the fallbacks fails loudly here.
        foreach (var (name, _) in records)
        {
            handler.SetResponse(ThumbUrl(Platform, name), HttpStatusCode.NotFound, new ByteArrayContent([]));
        }

        handler.SetResponse(ThumbUrl(Platform, "Unmatched Dump 01"), HttpStatusCode.NotFound, new ByteArrayContent([]));
        handler.SetResponse(ThumbUrl(Platform, "Unmatched Dump Title"), HttpStatusCode.OK, new ByteArrayContent(new byte[64]));

        var store = CreateRdbBackedStore(handler, records, romFileName: "Unmatched Dump 01.sms", romBytes: romBytes);

        var result = await store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, "Unmatched Dump 01.sms"),
            title: "Unmatched Dump Title",
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None);

        // The title fallback is the 9th and last candidate: it must still be probed.
        Assert.Equal(9, handler.RequestCount);
        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
    }

    // ---- Provider listing: spelling discovery for checksum-identified games -------------------

    private const string DriftedRdbName = "Ariel - The Little Mermaid (USA, Europe)";
    private const string ListedName = "Ariel the Little Mermaid (USA, Europe)";
    private static readonly byte[] CrcRomBytes = [4, 8, 15, 16, 23, 42];

    [Fact]
    public async Task Listing_DriftedSpelling_FindsArtThroughTheListedAlias()
    {
        var handler = ListingHandler(ListedName, "Another Game (USA)");
        handler.SetResponse(ThumbUrl(Platform, DriftedRdbName), HttpStatusCode.NotFound, new ByteArrayContent([]));
        handler.SetResponse(ThumbUrl(Platform, "Unmatched Dump 01"), HttpStatusCode.NotFound, new ByteArrayContent([]));
        handler.SetResponse(ThumbUrl(Platform, ListedName), HttpStatusCode.OK, new ByteArrayContent(new byte[64]));
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);
        await PrimeListingAsync(store);

        var result = await LookupRdbAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        Assert.Equal(
            new[] { DriftedRdbName, "Unmatched Dump 01", ListedName },
            ProbedNames(handler));
    }

    [Fact]
    public async Task Listing_NameAbsentFromAStaleListing_IsStillProbedDirectly()
    {
        var handler = ListingHandler("Another Game (USA)");
        handler.SetResponse(ThumbUrl(Platform, "Fresh Game (USA)"), HttpStatusCode.OK, new ByteArrayContent(new byte[64]));
        var store = CreateRdbBackedStore(handler, [("Fresh Game (USA)", Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);
        await PrimeListingAsync(store);

        var result = await LookupRdbAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
    }

    [Fact]
    public async Task Listing_AmbiguousKey_AddsNoAlias()
    {
        var handler = ListingHandler(ListedName, "Ariel the  Little Mermaid (USA, Europe)");
        RegisterMisses(handler, DriftedRdbName, "Unmatched Dump 01");
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);
        await PrimeListingAsync(store);

        var result = await LookupRdbAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Missing, result.Outcome);
        Assert.Equal(new[] { DriftedRdbName, "Unmatched Dump 01" }, ProbedNames(handler));
    }

    [Fact]
    public async Task Listing_NonCrcPrimary_GetsNoAliasAndNeverFetchesTheListing()
    {
        // The ROM is found by file name, not checksum, so a differently spelled listed name is not provably the same game.
        var handler = ListingHandler(ListedName);
        RegisterMisses(handler, DriftedRdbName);
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, 0x1234u)], DriftedRdbName + ".sms", CrcRomBytes);

        var result = await LookupRdbAsync(store, DriftedRdbName + ".sms");

        Assert.Equal(GameArtworkLookupOutcome.Missing, result.Outcome);
        Assert.Equal(new[] { DriftedRdbName }, ProbedNames(handler));
        Assert.DoesNotContain(handler.Requested, u => u.EndsWith("/Named_Boxarts/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listing_FilenameAndTitleFallbacks_GetNoAlias()
    {
        // No RDB record: the candidates are only the ROM file name and the title, each with a
        // same-key spelling in the listing that must not be probed.
        var handler = ListingHandler("Great  Golf", "Golf  Great");
        RegisterMisses(handler, "Great Golf", "Golf Great");
        var store = CreateStore(handler);
        await PrimeListingAsync(store);

        var result = await store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, RomFileName),
            title: "Golf Great",
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None);

        Assert.Equal(GameArtworkLookupOutcome.Missing, result.Outcome);
        Assert.Equal(new[] { "Great Golf", "Golf Great" }, ProbedNames(handler));
    }

    [Fact]
    public async Task Listing_Absent_AllowsMissing()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.NotFound, new ByteArrayContent([]));
        RegisterMisses(handler, DriftedRdbName, "Unmatched Dump 01");
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);
        await PrimeListingAsync(store);

        Assert.Equal(ListingState.Absent, store.ListingsForTests.Get(Platform, GameThumbService.ThumbKind.Boxart).State);
        var result = await LookupRdbAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task Listing_NotYetAvailable_DefersMissingToTheMetadataRetry()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.InternalServerError, new ByteArrayContent([]));
        RegisterMisses(handler, DriftedRdbName, "Unmatched Dump 01");
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);

        var result = await LookupRdbAsync(store);

        Assert.Equal(GameArtworkLookupOutcome.MetadataPending, result.Outcome);
    }

    [Fact]
    public async Task Listing_SlowFetch_IsNeverAwaitedByTheLookup()
    {
        var release = new TaskCompletionSource();
        var handler = ListingHandler(ListedName);
        handler.SetGate(ListingUrl, release.Task);
        RegisterMisses(handler, DriftedRdbName, "Unmatched Dump 01");
        var store = CreateRdbBackedStore(handler, [(DriftedRdbName, Crc32(CrcRomBytes))], "Unmatched Dump 01.sms", CrcRomBytes);

        var elapsed = Stopwatch.StartNew();
        var result = await LookupRdbAsync(store);
        elapsed.Stop();

        Assert.Equal(GameArtworkLookupOutcome.MetadataPending, result.Outcome);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Lookup took {elapsed.Elapsed}");

        release.SetResult();
        await PrimeListingAsync(store);
        Assert.Equal(ListingState.Available, store.ListingsForTests.Get(Platform, GameThumbService.ThumbKind.Boxart).State);
    }

    [Fact]
    public async Task Listing_SevenAssertedNamesAndAliasAll404_StillFindsTheTitleFallback()
    {
        // 7 RDB names + the filename fallback leave the title fallback as the ninth probe; the alias
        // is only reachable after every original, so it never displaces the title.
        const string primary = "Maze - Craze (USA)";
        var records = new List<(string Name, uint Crc)> { (primary, Crc32(CrcRomBytes)) };
        var regions = new[] { "Europe", "Japan", "World", "Brazil", "Korea", "Italy" };
        for (var i = 0; i < regions.Length; i++)
        {
            records.Add(($"Maze - Craze ({regions[i]})", 0x22222222u + (uint)i));
        }

        var handler = ListingHandler("Maze Craze (USA)");
        RegisterMisses(handler, records.Select(r => r.Name).Append("Unmatched Dump 01").Append("Maze Craze (USA)").ToArray());
        handler.SetResponse(ThumbUrl(Platform, "Unmatched Dump Title"), HttpStatusCode.OK, new ByteArrayContent(new byte[64]));
        var store = CreateRdbBackedStore(handler, records, "Unmatched Dump 01.sms", CrcRomBytes);
        await PrimeListingAsync(store);

        var result = await LookupRdbAsync(store, title: "Unmatched Dump Title");

        Assert.Equal(GameArtworkLookupOutcome.Found, result.Outcome);
        var probed = ProbedNames(handler);
        Assert.Equal(9, probed.Length);
        Assert.Equal("Unmatched Dump Title", probed[^1]);
        Assert.DoesNotContain("Maze Craze (USA)", probed);
    }

    [Fact]
    public async Task ListingStore_IncompleteFirstFetch_IsNotUsedOrStored()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(["A (USA)", "B (USA)"], complete: false)));
        var listings = NewListingStore(handler, _root);

        Assert.Equal(ListingState.Unavailable, listings.Get(Platform, GameThumbService.ThumbKind.Boxart).State);
        await InFlight(listings);

        Assert.Equal(ListingState.Unavailable, listings.Get(Platform, GameThumbService.ThumbKind.Boxart).State);
        Assert.False(Directory.Exists(Path.Combine(_root, "gamemeta", "listings")));
        // A failed fetch backs off instead of retrying on every lookup.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ListingStore_Digest_IsStableWhenTheHtmlOrderChanges()
    {
        var names = new[] { "Zeta (USA)", "Alpha (USA)", "Mid (USA)" };
        var firstDir = Path.Combine(_root, "a");
        var secondDir = Path.Combine(_root, "b");
        foreach (var (dir, order) in new[] { (firstDir, names), (secondDir, names.Reverse().ToArray()) })
        {
            var handler = new FakeHttpMessageHandler();
            handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(order)));
            var listings = NewListingStore(handler, dir);
            listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
            await InFlight(listings);
        }

        string Stored(string dir, string extension) => File.ReadAllText(Path.Combine(
            dir, "gamemeta", "listings", Uri.EscapeDataString(Platform) + "__Named_Boxarts" + extension));

        string Sha(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("sha256").GetString()!;

        Assert.Equal(Sha(Stored(firstDir, ".json")), Sha(Stored(secondDir, ".json")));
        Assert.Equal(Stored(firstDir, ".txt"), Stored(secondDir, ".txt"));
        Assert.Equal("Alpha (USA)\nMid (USA)\nZeta (USA)", Stored(firstDir, ".txt"));
        Assert.Contains("\"count\":3", Stored(firstDir, ".json"));
    }

    [Fact]
    public async Task ListingStore_ConcurrentLookups_ShareOneFetchAndNeverRefetchAfterSuccess()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(["A (USA)"])));
        handler.SetGate(ListingUrl, release.Task);
        var listings = NewListingStore(handler, _root);

        Parallel.For(0, 20, _ => listings.Get(Platform, GameThumbService.ThumbKind.Boxart));
        await WaitUntil(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(1, handler.RequestCount);

        release.SetResult();
        await InFlight(listings);
        Assert.Equal(ListingState.Available, listings.Get(Platform, GameThumbService.ThumbKind.Boxart).State);
        listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(listings);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ListingStore_Invalidate_RefetchesAnAbsentFolder()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.NotFound, new ByteArrayContent([]));
        var listings = NewListingStore(handler, _root);
        listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(listings);
        Assert.Equal(ListingState.Absent, listings.Get(Platform, GameThumbService.ThumbKind.Boxart).State);

        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml([ListedName])));
        listings.Invalidate();
        var during = listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(listings);
        var after = listings.Get(Platform, GameThumbService.ThumbKind.Boxart);

        Assert.Equal(ListingState.Unavailable, during.State);
        Assert.Equal(ListingState.Available, after.State);
        Assert.Equal(new[] { ListedName }, after.Listing!.Names);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ListingStore_FetchStartedBeforeInvalidate_ServesItsCopyButFetchesAgain()
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(["Old (USA)"])));
        handler.SetGate(ListingUrl, release.Task);
        var listings = NewListingStore(handler, _root);
        listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await WaitUntil(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        listings.Invalidate();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(["New (USA)"])));
        release.SetResult();
        await InFlight(listings);
        var stale = listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(listings);
        var fresh = listings.Get(Platform, GameThumbService.ThumbKind.Boxart);

        Assert.Equal(new[] { "Old (USA)" }, stale.Listing!.Names);
        Assert.Equal(new[] { "New (USA)" }, fresh.Listing!.Names);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ListingStore_FetchStartedBeforeInvalidateFails_DoesNotDelayTheRefetch(HttpStatusCode staleStatus)
    {
        var release = new TaskCompletionSource();
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, staleStatus, new ByteArrayContent([]));
        handler.SetGate(ListingUrl, release.Task);
        var listings = NewListingStore(handler, _root);
        listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await WaitUntil(() => handler.RequestCount == 1, TimeSpan.FromSeconds(5));

        listings.Invalidate();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml([ListedName])));
        release.SetResult();
        await InFlight(listings);
        listings.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(listings);
        var after = listings.Get(Platform, GameThumbService.ThumbKind.Boxart);

        Assert.Equal(ListingState.Available, after.State);
        Assert.Equal(new[] { ListedName }, after.Listing!.Names);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ListingStore_FailedRefetch_FallsBackToTheStoredListing()
    {
        var first = new FakeHttpMessageHandler();
        first.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml([ListedName])));
        var firstStore = NewListingStore(first, _root);
        firstStore.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(firstStore);

        var second = new FakeHttpMessageHandler();
        second.SetResponse(ListingUrl, HttpStatusCode.InternalServerError, new ByteArrayContent([]));
        var secondStore = NewListingStore(second, _root);

        var immediate = secondStore.Get(Platform, GameThumbService.ThumbKind.Boxart);
        await InFlight(secondStore);
        var after = secondStore.Get(Platform, GameThumbService.ThumbKind.Boxart);

        Assert.Equal(ListingState.Available, immediate.State);
        Assert.Equal(ListingState.Available, after.State);
        Assert.Equal(new[] { ListedName }, after.Listing!.Names);
        Assert.Equal(1, second.RequestCount);
    }

    [Fact]
    public void ListingStore_Parse_DecodesNamesAndSkipsEverythingButPngHrefs()
    {
        var html = "<html><a href=\"../\">up</a><a href=\"?C=N;O=D\">sort</a>"
            + "<a href=\"Mario%20%26%20Luigi%20%28USA%29.PNG\">x</a><a href='Single_Quoted.png'>y</a>"
            + "<a href=\"readme.txt\">z</a><a href=\"sub/Deep.png\">w</a></html>";

        var names = ThumbnailListingStore.ParseComplete(html);

        Assert.Equal(new[] { "Mario & Luigi (USA)", "Single_Quoted" }, names);
        Assert.Null(ThumbnailListingStore.ParseComplete("<html><a href=\"A.png\">"));
        Assert.Null(ThumbnailListingStore.ParseComplete("<html></html>"));
    }

    private static string ListingUrl =>
        new Uri("https://thumbnails.libretro.com/" + Uri.EscapeDataString(Platform) + "/Named_Boxarts/").ToString();

    private static string ListingHtml(IEnumerable<string> names, bool complete = true) =>
        "<html><head><title>Index</title></head><body><a href=\"../\">../</a>\n"
        + string.Join("\n", names.Select(n => $"<a href=\"{Uri.EscapeDataString(n)}.png\">{n}.png</a>"))
        + (complete ? "\n</body></html>" : string.Empty);

    private static StringContent Html(string html) => new(html, Encoding.UTF8, "text/html");

    private static FakeHttpMessageHandler ListingHandler(params string[] names)
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ListingUrl, HttpStatusCode.OK, Html(ListingHtml(names)));
        return handler;
    }

    private static void RegisterMisses(FakeHttpMessageHandler handler, params string[] names)
    {
        foreach (var name in names)
        {
            handler.SetResponse(ThumbUrl(Platform, name), HttpStatusCode.NotFound, new ByteArrayContent([]));
        }
    }

    private static ThumbnailListingStore NewListingStore(FakeHttpMessageHandler handler, string dataFolder) =>
        new(new FakeHttpClientFactory(handler), new NoOpLogger<GameArtworkStore>(), () => dataFolder);

    private static Task InFlight(ThumbnailListingStore listings) =>
        listings.InFlightForTests(Platform, GameThumbService.ThumbKind.Boxart) ?? Task.CompletedTask;

    private static Task PrimeListingAsync(GameArtworkStore store)
    {
        store.ListingsForTests.Get(Platform, GameThumbService.ThumbKind.Boxart);
        return InFlight(store.ListingsForTests);
    }

    // Artwork names probed so far, in request order (listing fetches excluded).
    private static string[] ProbedNames(FakeHttpMessageHandler handler)
    {
        lock (handler.Requested)
        {
            return handler.Requested
                .Where(u => u.EndsWith(".png", StringComparison.Ordinal))
                .Select(u => Uri.UnescapeDataString(u[(u.LastIndexOf('/') + 1)..^4]))
                .ToArray();
        }
    }

    private Task<GameArtworkLookupResult> LookupRdbAsync(
        GameArtworkStore store,
        string romFileName = "Unmatched Dump 01.sms",
        string? title = null) =>
        store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, romFileName),
            title: title,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private GameArtworkStore CreateStore(FakeHttpMessageHandler handler, long? maxArtworkBytes = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, RomFileName), new byte[] { 9, 9, 9, 9 });

        // No ConfigOverrideForTests: ResolveArtworkNameAsync then returns null without touching
        // disk or the network, leaving the ROM filename stem as the sole artwork candidate.
        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        return new GameArtworkStore(
            new FakeHttpClientFactory(handler),
            new NoOpLogger<GameArtworkStore>(),
            rdb,
            _root,
            maxArtworkBytes ?? GameArtworkStore.MaxArtworkBytes);
    }

    private Task<GameArtworkLookupResult> LookupAsync(
        GameArtworkStore store,
        CancellationToken cancellationToken = default,
        TimeSpan? budget = null) =>
        store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, RomFileName),
            title: null,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: cancellationToken,
            budget: budget);

    // A prewarm has no client waiting on it and must be able to outlast the interactive budget,
    // which is itself the ceiling on how long a /Thumb/ response may hang for an old client and so
    // must not move.
    [Fact]
    public async Task Lookup_HonoursAnExplicitBudgetWithoutMovingTheInteractiveDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), GameArtworkStore.InteractiveRequestBudget);
        Assert.True(GameArtworkStore.PrewarmRequestBudget > GameArtworkStore.InteractiveRequestBudget);

        var gate = new SemaphoreSlim(0);
        var body = new UnseekableStream(new byte[64], blockAfterFirstReadOn: gate);
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(RomFileName), HttpStatusCode.OK, new StreamContent(body));
        var store = CreateStore(handler);

        var elapsed = Stopwatch.StartNew();
        var result = await LookupAsync(store, budget: TimeSpan.FromMilliseconds(250));
        elapsed.Stop();

        // An ignored budget would instead park on the gate for the 3s default.
        Assert.True(result.TimedOut);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Lookup took {elapsed.Elapsed}");

        gate.Release();
        await WaitUntil(() => Directory.GetFiles(_root, "*.tmp").Length == 0, TimeSpan.FromSeconds(5));
    }

    private void AssertNothingCached()
    {
        Assert.Empty(Directory.GetFiles(_root, "*.png"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    // Must match GameArtworkStore.BuildUrl's escaping and the Uri.ToString() normalization the
    // fake handler observes, or comparisons silently mismatch.
    private static string ThumbUrl(string thumbName)
        => ThumbUrl(Platform, Path.GetFileNameWithoutExtension(thumbName));

    private static string ThumbUrl(string platform, string thumbName)
    {
        var raw = "https://thumbnails.libretro.com/" + Uri.EscapeDataString(platform)
            + "/Named_Boxarts/" + Uri.EscapeDataString(thumbName) + ".png";
        return new Uri(raw).ToString();
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(5);
        }
    }

    // A response body the test can inspect or suspend partway through, so the temp-file and
    // in-flight assertions can be made while the transfer is genuinely mid-flight. Non-seekable so
    // StreamContent reports no Content-Length.
    private sealed class UnseekableStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly Action? _afterFirstRead;
        private readonly SemaphoreSlim? _blockAfterFirstReadOn;
        private bool _firstReadDone;

        public UnseekableStream(byte[] data, Action? afterFirstRead = null, SemaphoreSlim? blockAfterFirstReadOn = null)
        {
            _inner = new MemoryStream(data);
            _afterFirstRead = afterFirstRead;
            _blockAfterFirstReadOn = blockAfterFirstReadOn;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (!_firstReadDone)
            {
                _firstReadDone = true;
                _afterFirstRead?.Invoke();
                _blockAfterFirstReadOn?.Wait();
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Stands in for thumbnails.libretro.com. Throws on any unregistered URL so an unexpected extra
    // probe fails loudly instead of silently 404ing.
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, HttpContent Content)> _responses = new(StringComparer.Ordinal);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public List<string> Requested { get; } = new();

        public void SetResponse(string url, HttpStatusCode status, HttpContent content) =>
            _responses[url] = (status, content);

        // A request to this URL waits for the task before it is answered.
        public void SetGate(string url, Task gate) => _gates[url] = gate;

        private readonly Dictionary<string, Task> _gates = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Interlocked.Increment(ref _requestCount);
            lock (Requested)
            {
                Requested.Add(url);
            }

            if (!_responses.TryGetValue(url, out var response))
            {
                throw new InvalidOperationException($"Unexpected thumbnail request to unregistered URL: {url}");
            }

            if (_gates.TryGetValue(url, out var gate))
            {
                await gate.ConfigureAwait(false);
            }

            return new HttpResponseMessage(response.Status) { Content = response.Content };
        }
    }


    // Builds a store over a real, locally-present .rdb so the full RDB name-resolution path runs
    // (MatchWithSiblings included) rather than the short-circuit the other tests here rely on.
    // GamesMetadataDbUrlBase is empty: the file is already on disk, so nothing downloads.
    private GameArtworkStore CreateRdbBackedStore(
        FakeHttpMessageHandler handler,
        IReadOnlyList<(string Name, uint Crc)> records,
        string romFileName,
        byte[]? romBytes = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, romFileName), romBytes ?? [9, 9, 9, 9]);
        WriteMinimalRdb(Path.Combine(_root, "gamemeta", Platform + ".rdb"), records);

        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root)
        {
            ConfigOverrideForTests = new global::Moonfin.Server.PluginConfiguration
            {
                GamesMetadataDbUrlBase = string.Empty,
            },
        };

        return new GameArtworkStore(
            new FakeHttpClientFactory(handler),
            new NoOpLogger<GameArtworkStore>(),
            rdb,
            _root);
    }

    // Minimal libretro .rdb writer: 8-byte magic, 8-byte metadata offset, then one MessagePack
    // fixmap per game with "name" and "crc". Mirrors RdbServiceTests' fixture writer.
    private static void WriteMinimalRdb(string path, IReadOnlyList<(string Name, uint Crc)> games)
    {
        using var ms = new MemoryStream();
        ms.Write("RARCHDB"u8.ToArray());
        ms.WriteByte(0); // trailing NUL of the 8-byte magic
        ms.Write(new byte[8]);

        foreach (var (name, crc) in games)
        {
            ms.WriteByte(0x82); // fixmap, 2 entries
            WriteMsgPackString(ms, "name");
            WriteMsgPackString(ms, name);
            WriteMsgPackString(ms, "crc");
            ms.WriteByte(0xc4); // bin8
            ms.WriteByte(4);
            var crcBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            ms.Write(crcBytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteMsgPackString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= 31)
        {
            ms.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else
        {
            ms.WriteByte(0xd9);
            ms.WriteByte((byte)bytes.Length);
        }

        ms.Write(bytes);
    }

    // CRC32 (IEEE 802.3 / zlib polynomial, init and final XOR 0xFFFFFFFF) -- the same value
    // RdbMatcher.ComputeCrcCandidates derives from the ROM, so a fixture record carrying it is
    // matched by content rather than by name.
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
