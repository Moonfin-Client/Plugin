using System.Net;
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

    private Task<GameArtworkLookupResult> LookupAsync(GameArtworkStore store, CancellationToken cancellationToken = default) =>
        store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, RomFileName),
            title: null,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: cancellationToken);

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

        public void SetResponse(string url, HttpStatusCode status, HttpContent content) =>
            _responses[url] = (status, content);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Interlocked.Increment(ref _requestCount);

            if (!_responses.TryGetValue(url, out var response))
            {
                throw new InvalidOperationException($"Unexpected thumbnail request to unregistered URL: {url}");
            }

            return Task.FromResult(new HttpResponseMessage(response.Status) { Content = response.Content });
        }
    }

}
