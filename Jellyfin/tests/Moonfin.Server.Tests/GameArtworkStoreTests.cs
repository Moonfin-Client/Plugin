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
        var siblingRegions = new[] { "USA, Europe", "Europe", "Japan", "World", "Brazil", "Korea", "Proto", "Taiwan", "France", "Germany" };
        for (var i = 0; i < siblingRegions.Length; i++)
        {
            records.Add(($"Maze Craze ~ A Game of Cops n Robbers ({siblingRegions[i]})", (uint)(0x22222222u + i)));
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

            return Task.FromResult(new HttpResponseMessage(response.Status) { Content = response.Content });
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
