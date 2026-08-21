using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

// ---- Sibling-fallback tests for GameThumbService.GetThumbPathAsync ------------------------
//
// GameThumbService is what actually decides whether RdbService.MatchWithSiblings' extra
// candidate names matter, by trying each against the (here, fake) libretro thumbnail CDN in
// order and moving on only on a 404.
//
// Both services' internal test constructors point their on-disk state at a fixture folder
// instead of a live MoonfinPlugin.Instance/AppData path, so these tests never touch a real
// machine's data folder or the network -- FakeHttpMessageHandler stands in for the CDN.
public class GameThumbServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-thumb-tests-{Guid.NewGuid():N}");

    private const string Platform = "Sega - Master System - Mark III";
    private const string JapanName = "Great Golf (Japan) (En)";
    private const string WorldName = "Great Golf / Masters Golf (World)";

    // Matches GameThumbService.LibretroThumbName's own escaping: '/' becomes '_'.
    private const string WorldThumbFileName = "Great Golf _ Masters Golf (World)";

    [Fact]
    public async Task GetThumbPathAsync_BestMatchNameHasNoArt_FallsThroughToSiblingThatDoes()
    {
        WriteGreatGolfFixture();
        var handler = new FakeHttpMessageHandler();
        // Primary/best-match name 404s upstream; the sibling name serves a (fake) PNG.
        handler.SetResponse(ThumbUrl(JapanName), HttpStatusCode.NotFound, null);
        handler.SetResponse(ThumbUrl(WorldThumbFileName), HttpStatusCode.OK, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var (rdb, thumbs) = CreateServices(handler);
        var romPath = WriteRomFile();

        var lookup = await thumbs.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);

        Assert.Equal(GameArtworkLookupOutcome.Found, lookup.Outcome);
        Assert.NotNull(lookup.Path);
        var path = await thumbs.GetThumbPathAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        // Exactly two attempts: primary 404, then sibling 200. No further candidates are tried
        // once one succeeds.
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, handler.RequestedUrls.Count);
        Assert.Contains(ThumbUrl(JapanName), handler.RequestedUrls);
        Assert.Contains(ThumbUrl(WorldThumbFileName), handler.RequestedUrls);
    }

    [Fact]
    public async Task GetThumbPathAsync_PrimaryMatchSucceeds_NeverAttemptsAnySiblingOrFallbackName()
    {
        // The sibling name must never be requested once the primary succeeds on its first try.
        WriteGreatGolfFixture();
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(JapanName), HttpStatusCode.OK, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        // No response registered for the World sibling: the fake handler throws on any
        // unregistered URL, so an extra request fails the test loudly.

        var (rdb, thumbs) = CreateServices(handler);
        var romPath = WriteRomFile();

        var path = await thumbs.GetThumbPathAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);

        Assert.NotNull(path);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(ThumbUrl(JapanName), Assert.Single(handler.RequestedUrls));
    }

    [Fact]
    public async Task GetThumbPathAsync_NoCandidateNameHasArt_ReturnsNullWithoutAnUnboundedRequestStorm()
    {
        // When nothing has art anywhere the request count has to stay capped. After the primary
        // and sibling 404s, GetThumbPathAsync falls back to the filename and title candidates
        // against the primary platform and stops, and a fourth request would fail loudly
        // through FakeHttpMessageHandler's unregistered-URL guard. The filename candidate drops
        // its extension, so here it matches the title and the two collapse into one probe.
        WriteGreatGolfFixture();
        var handler = new FakeHttpMessageHandler();
        handler.SetResponse(ThumbUrl(JapanName), HttpStatusCode.NotFound, null);
        handler.SetResponse(ThumbUrl(WorldThumbFileName), HttpStatusCode.NotFound, null);
        handler.SetResponse(ThumbUrl("Great Golf"), HttpStatusCode.NotFound, null);

        var (rdb, thumbs) = CreateServices(handler);
        var romPath = WriteRomFile();

        var lookup = await thumbs.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);

        Assert.Equal(GameArtworkLookupOutcome.Missing, lookup.Outcome);
        Assert.Null(lookup.Path);
        var path = await thumbs.GetThumbPathAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);
        Assert.Null(path);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task LookupThumbAsync_ProviderFailure_IsRetryableAndPreservesRetryAfter()
    {
        WriteGreatGolfFixture();
        var handler = new FakeHttpMessageHandler();
        var retryAfter = TimeSpan.FromMinutes(2);
        foreach (var name in new[] { JapanName, WorldThumbFileName, "Great Golf" })
        {
            handler.SetResponse(ThumbUrl(name), HttpStatusCode.TooManyRequests, null, retryAfter);
        }

        var (rdb, thumbs) = CreateServices(handler);
        var romPath = WriteRomFile();

        var lookup = await thumbs.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: romPath,
            title: "Great Golf",
            kind: GameThumbService.ThumbKind.Boxart);

        Assert.Equal(GameArtworkLookupOutcome.TransientFailure, lookup.Outcome);
        Assert.Null(lookup.Path);
        Assert.Equal(retryAfter, lookup.RetryDelay);
        // The legacy request-path API retains its established null contract for this failure.
        Assert.Null(await thumbs.GetThumbPathAsync(
            "segaMS", false, "Sega Master System", romPath, "Great Golf", GameThumbService.ThumbKind.Boxart));
        Assert.Equal(6, handler.RequestCount);
    }

    // ---- GetDerivedThumbAsync: encode budget and concurrency cap ------------------------------
    //
    // These reproduce the client-observed bug where the artwork tile times out even though the
    // art genuinely exists: GetThumbPathAsync (above) resolves and downloads the original in a
    // couple hundred ms, but the derived-thumbnail re-encode that runs after it was previously
    // unbounded in both wall-clock time and concurrency, so a scroll burst of cache misses could
    // make the *encode* step alone take longer than the client's 15s GameArtworkLoadScheduler
    // timeout even though the original was already sitting on disk the whole time.

    [Fact]
    public async Task GetDerivedThumbAsync_EncodeExceedsBudget_ServesOriginalWithoutDuplicatingTheBackgroundEncode()
    {
        var encoder = new BlockingFakeImageEncoder();
        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var thumbs = new GameThumbService(
            new ThrowingHttpClientFactory(),
            new NoOpLogger<GameThumbService>(),
            rdb,
            _root,
            encoder,
            encodeConcurrencyForTests: 4,
            derivedThumbBudgetForTests: TimeSpan.FromMilliseconds(50));

        var originalPath = WriteOriginalPngFixture("great-golf.png");

        // The encoder never returns on its own here -- it's blocked until Release() below -- so
        // this proves the wait gives up on its budget rather than hanging the request.
        var first = await thumbs.GetDerivedThumbAsync(originalPath, CancellationToken.None);
        Assert.Equal(originalPath, first.Path);
        Assert.Equal("image/png", first.ContentType);
        // Not a final answer -- the caller must retry rather than record this as ready, or the
        // entry is pinned to the full-size original forever. See DerivedThumbnailOutcome.TimedOut.
        Assert.Equal(DerivedThumbnailOutcome.TimedOut, first.Outcome);

        // Let the still-running background encode finish, then confirm a second request for the
        // same original picks up the now-cached derived file instead of starting a second,
        // duplicate encode -- the failure mode the completion-based (not wait-based) removal from
        // _derivedInFlight guards against.
        encoder.Release(1);
        var derivedPath = DerivedPathFor(originalPath, ".jpg");
        await WaitUntil(() => File.Exists(derivedPath), TimeSpan.FromSeconds(5));

        var second = await thumbs.GetDerivedThumbAsync(originalPath, CancellationToken.None);
        Assert.Equal(derivedPath, second.Path);
        Assert.Equal("image/jpeg", second.ContentType);
        Assert.Equal(DerivedThumbnailOutcome.Encoded, second.Outcome);
        Assert.Equal(1, encoder.InvocationCount);
    }

    [Fact]
    public async Task GetDerivedThumbAsync_EncodeExceedsBudget_ReconciliationMarksEntryRetryableNotReady()
    {
        // Reproduces the D3 bug end to end: a budget overrun used to be recorded via
        // MarkThumbnailReadyAsync(..., RetryAfterUtc: null), which GetStartupRecoveryWorkAsync
        // never revisits because it only re-queues OriginalReady entries with a null ThumbnailPath.
        // The fix is in GameArtworkReconciliationService.ThumbnailWorkerAsync: a TimedOut outcome
        // must go through MarkRetryableAsync so the entry stays revisitable, and a later pass (once
        // the encoder is unblocked) must produce a real thumbnail.
        var encoder = new BlockingFakeImageEncoder();
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        const string LibraryId = "lib1";
        const string GameId = "great-golf";
        const string SystemId = "sega-master-system";
        var key = ArtworkCatalogKey.Create(LibraryId, "great-golf.sms", "fingerprint", "boxart");
        var originalPath = WriteOriginalPngFixture("great-golf.png");
        await catalog.GetOrCreateAsync(key, SystemId, "provider-v1", GameId);
        await catalog.MarkOriginalReadyAsync(key, SystemId, originalPath);

        var thumbs = new DerivedThumbnailService(
            new NoOpLogger<DerivedThumbnailService>(),
            Path.Combine(_root, "game_thumbs"),
            encoder,
            encodeConcurrencyForTests: 4,
            derivedThumbBudgetForTests: TimeSpan.FromMilliseconds(50));

        var timedOut = await thumbs.GetDerivedThumbAsync(originalPath, CancellationToken.None);
        Assert.Equal(DerivedThumbnailOutcome.TimedOut, timedOut.Outcome);

        // Mirrors ThumbnailWorkerAsync's TimedOut branch.
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
        await catalog.MarkRetryableAsync(key, SystemId, ArtworkCatalogWorkStage.DeriveThumbnail, retryAfter);

        var afterTimeout = await catalog.GetByGameIdAsync(LibraryId, GameId, "boxart")
            ?? throw new InvalidOperationException("Entry not found after MarkRetryableAsync.");
        Assert.Equal(ArtworkCatalogState.OriginalReady, afterTimeout.State);
        Assert.Null(afterTimeout.ThumbnailPath);

        // GetStartupRecoveryWorkAsync only re-queues OriginalReady entries with a null
        // ThumbnailPath -- confirm this entry qualifies, i.e. it will actually be revisited.
        var recovery = await catalog.GetStartupRecoveryWorkAsync();
        var requeued = Assert.Single(recovery, item => item.Entry.Key.StorageKey == key.StorageKey);
        Assert.Equal(ArtworkCatalogWorkStage.DeriveThumbnail, requeued.Stage);

        // A later pass, once the encoder is unblocked, produces a real thumbnail.
        encoder.Release(2);
        var derivedPath = DerivedPathFor(originalPath, ".jpg");
        await WaitUntil(() => File.Exists(derivedPath), TimeSpan.FromSeconds(5));
        var encoded = await thumbs.GetDerivedThumbAsync(originalPath, CancellationToken.None);
        Assert.Equal(DerivedThumbnailOutcome.Encoded, encoded.Outcome);
        Assert.Equal(derivedPath, encoded.Path);

        await catalog.MarkThumbnailReadyAsync(key, SystemId, encoded.Path);
        var final = await catalog.GetByGameIdAsync(LibraryId, GameId, "boxart")
            ?? throw new InvalidOperationException("Entry not found after MarkThumbnailReadyAsync.");
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, final.State);
        Assert.Equal(derivedPath, final.ThumbnailPath);
    }

    [Fact]
    public async Task GetDerivedThumbAsync_ConcurrentMissesAcrossDifferentGames_NeverExceedsTheEncodeConcurrencyCap()
    {
        const int Cap = 2;
        const int RequestCount = 5;
        var encoder = new BlockingFakeImageEncoder();
        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var thumbs = new GameThumbService(
            new ThrowingHttpClientFactory(),
            new NoOpLogger<GameThumbService>(),
            rdb,
            _root,
            encoder,
            encodeConcurrencyForTests: Cap,
            // Generous: this test drives completion by hand via encoder.Release(), never by
            // waiting out the real clock, so a long budget just means it never fires here.
            derivedThumbBudgetForTests: TimeSpan.FromSeconds(30));

        var originals = Enumerable.Range(0, RequestCount)
            .Select(i => WriteOriginalPngFixture($"game{i}.png"))
            .ToList();

        var pending = originals
            .Select(path => thumbs.GetDerivedThumbAsync(path, CancellationToken.None))
            .ToList();

        // All 5 requests are in flight; only Cap of them should have made it past the semaphore
        // into the (blocked) encoder at once.
        await WaitUntil(() => encoder.CurrentlyBlocked == Cap, TimeSpan.FromSeconds(5));
        encoder.Release(RequestCount);
        await Task.WhenAll(pending);

        Assert.Equal(Cap, encoder.MaxConcurrent);
        Assert.Equal(RequestCount, encoder.InvocationCount);
    }

    private string WriteOriginalPngFixture(string fileName)
    {
        var dir = Path.Combine(_root, "game_thumbs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        return path;
    }

    private string DerivedPathFor(string originalPath, string extension) =>
        Path.Combine(_root, "game_thumbs", Path.GetFileNameWithoutExtension(originalPath) + "_thumb" + extension);

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

    private (RdbService Rdb, GameThumbService Thumbs) CreateServices(FakeHttpMessageHandler handler)
    {
        // Throws if RdbService ever tries to download; fixtures are written directly to disk.
        var rdb = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root)
        {
            // MoonfinPlugin.Instance is unavailable here, and GameThumbService goes through the
            // real public ResolveArtworkNameAsync, which needs a config to not short-circuit.
            ConfigOverrideForTests = new global::Moonfin.Server.PluginConfiguration(),
        };
        var thumbs = new GameThumbService(
            new FakeHttpClientFactory(handler),
            new NoOpLogger<GameThumbService>(),
            rdb,
            _root);
        return (rdb, thumbs);
    }

    // Writes the two-record Master System fixture (Japan as primary fuzzy match, World as the
    // farther sibling) that MatchWithSiblings resolves in this order (see RdbServiceTests).
    // RdbService's cache key needs the ROM to actually exist on disk; exact bytes don't matter
    // since these records carry no "crc" field, so resolution always falls to fuzzy-prefix.
    private string WriteRomFile()
    {
        var romPath = Path.Combine(_root, "Great Golf.sms");
        File.WriteAllBytes(romPath, new byte[] { 9, 9, 9, 9 });
        return romPath;
    }

    private void WriteGreatGolfFixture()
    {
        var rdbPath = Path.Combine(_root, "gamemeta", Platform + ".rdb");
        Directory.CreateDirectory(Path.GetDirectoryName(rdbPath)!);

        using var ms = new MemoryStream();
        ms.Write("RARCHDB\0"u8.ToArray());
        ms.Write(new byte[8]); // metadata offset = 0 (records run to end of file)
        WriteFixMapNameOnlyRecord(ms, JapanName);
        WriteFixMapNameOnlyRecord(ms, WorldName);

        File.WriteAllBytes(rdbPath, ms.ToArray());
    }

    // Same fixmap shape as RdbServiceTests.WriteFixMapGameRecord but name-only (no "crc"), so the
    // CRC step misses and resolution goes through Match's fuzzy-prefix step.
    private static void WriteFixMapNameOnlyRecord(MemoryStream ms, string name)
    {
        ms.WriteByte(0x81); // fixmap, 1 entry
        WriteFixStr(ms, "name");
        WriteFixStr(ms, name);
    }

    private static void WriteFixStr(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= 31)
        {
            ms.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else if (bytes.Length <= 255)
        {
            ms.WriteByte(0xd9);
            ms.WriteByte((byte)bytes.Length);
        }
        else
        {
            throw new InvalidOperationException("Test fixture strings must fit in a msgpack str8 (<= 255 bytes).");
        }

        ms.Write(bytes);
    }

    // Must match GameThumbService.BuildUrl's escaping and go through the same Uri.ToString()
    // normalization FakeHttpMessageHandler observes, or comparisons silently mismatch.
    private static string ThumbUrl(string thumbName)
    {
        var raw = "https://thumbnails.libretro.com/" + Uri.EscapeDataString(Platform) + "/Named_Boxarts/" + Uri.EscapeDataString(thumbName) + ".png";
        return new Uri(raw).ToString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Stands in for thumbnails.libretro.com. Throws on any unregistered URL so a test with a
    // bounded request count fails loudly instead of silently 404ing.
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[]? Body, TimeSpan? RetryAfter)> _responses = new(StringComparer.Ordinal);

        public int RequestCount { get; private set; }

        public List<string> RequestedUrls { get; } = new();

        public void SetResponse(string url, HttpStatusCode status, byte[]? body, TimeSpan? retryAfter = null) =>
            _responses[url] = (status, body, retryAfter);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestCount++;
            RequestedUrls.Add(url);

            if (!_responses.TryGetValue(url, out var response))
            {
                throw new InvalidOperationException($"Unexpected thumbnail request to unregistered URL: {url}");
            }

            var message = new HttpResponseMessage(response.Status);
            if (response.Body != null)
            {
                message.Content = new ByteArrayContent(response.Body);
            }

            if (response.RetryAfter is { } retryAfter)
            {
                message.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            }

            return Task.FromResult(message);
        }
    }

    // Stands in for SkiaEncoder. EncodeImage blocks on a gate the test controls directly (never a
    // real-time sleep) so tests can deterministically observe how many encodes are running at
    // once and prove the wait-budget/concurrency-cap behavior without racing the real clock.
    private sealed class BlockingFakeImageEncoder : IImageEncoder
    {
        private readonly SemaphoreSlim _gate = new(0);
        private int _current;
        private int _maxConcurrent;
        private int _invocationCount;

        public int InvocationCount => _invocationCount;

        public int CurrentlyBlocked => _current;

        public int MaxConcurrent => _maxConcurrent;

        public IReadOnlyCollection<string> SupportedInputFormats { get; } = new[] { "png" };

        public IReadOnlyCollection<ImageFormat> SupportedOutputFormats { get; } = new[] { ImageFormat.Jpg };

        public string Name => "Fake";

        public bool SupportsImageCollageCreation => false;

        public bool SupportsImageEncoding => true;

        public void Release(int count) => _gate.Release(count);

        public ImageDimensions GetImageSize(string path) => throw new NotSupportedException();

        public string GetImageBlurHash(int xComp, int yComp, string path) => throw new NotSupportedException();

        public string EncodeImage(
            string inputPath,
            DateTime dateModified,
            string outputPath,
            bool autoOrient,
            ImageOrientation? orientation,
            int quality,
            ImageProcessingOptions options,
            ImageFormat outputFormat)
        {
            Interlocked.Increment(ref _invocationCount);
            var concurrent = Interlocked.Increment(ref _current);
            InterlockedSetMax(ref _maxConcurrent, concurrent);
            try
            {
                _gate.Wait();
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }

            File.WriteAllBytes(outputPath, new byte[] { 1, 2, 3 });
            return outputPath;
        }

        public void CreateImageCollage(ImageCollageOptions options, string? libraryName) =>
            throw new NotSupportedException();

        public void CreateSplashscreen(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops) =>
            throw new NotSupportedException();

        public int CreateTrickplayTile(ImageCollageOptions options, int quality, int imgWidth, int? imgHeight) =>
            throw new NotSupportedException();

        private static void InterlockedSetMax(ref int target, int value)
        {
            int initial;
            do
            {
                initial = target;
                if (value <= initial)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref target, value, initial) != initial);
        }
    }

}
