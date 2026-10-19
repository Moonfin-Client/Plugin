using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class RdbUpdateCheckTests : IDisposable
{
    private const string Platform = "Nintendo - Super Nintendo Entertainment System";
    private const string BaseUrl = "https://mirror.test/rdb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-rdb-update-{Guid.NewGuid():N}");

    private string RdbPath => Path.Combine(_root, "gamemeta", Platform + ".rdb");

    private string SidecarPath => RdbPath + ".json";

    private static global::Moonfin.Server.PluginConfiguration Config(string baseLocation) =>
        new() { GamesMetadataDbUrlBase = baseLocation };

    private RdbIndexStore Store(ScriptedHandler handler) =>
        new(new FakeHttpClientFactory(handler), new NoOpLogger<RdbService>(), _root);

    [Fact]
    public async Task FirstDownload_WritesSidecarAndIsNotRecheckedInTheSameRun()
    {
        var bytes = RdbBytes("Alpha", "Beta");
        var handler = ScriptedHandler.Sync(_ => Ok(bytes, "\"v1\"", ModifiedA));
        var store = Store(handler);

        await store.EnsureDownloadedAsync(Platform, Config(BaseUrl));

        var sidecar = ReadSidecar();
        Assert.Equal("\"v1\"", sidecar.GetProperty("etag").GetString());
        Assert.Equal(Sha(bytes), sidecar.GetProperty("sha256").GetString());
        Assert.Equal(2, sidecar.GetProperty("recordCount").GetInt32());
        Assert.NotNull(await store.GetIndexAsync(Platform, Config(BaseUrl)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Restart_WithUnchangedSource_SendsOneConditionalRequestAndRecordsTheCheckOnly()
    {
        var bytes = RdbBytes("Alpha");
        await Store(ScriptedHandler.Sync(_ => Ok(bytes, "W/\"v1\"", ModifiedA))).EnsureDownloadedAsync(Platform, Config(BaseUrl));
        var checkedBefore = ReadSidecar().GetProperty("checkedUtc").GetDateTime();
        await Task.Delay(20);

        var handler = ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var restarted = Store(handler);
        var result = await restarted.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));
        await restarted.GetIndexAsync(Platform, Config(BaseUrl));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("W/\"v1\"", request.IfNoneMatch);
        Assert.Equal(ModifiedA.ToString("R"), request.IfModifiedSince);
        Assert.False(result.ReopenPending);
        Assert.Equal(bytes, File.ReadAllBytes(RdbPath));
        Assert.False(File.Exists(RdbPath + ".prev"));
        Assert.True(ReadSidecar().GetProperty("checkedUtc").GetDateTime() > checkedBefore);
    }

    [Fact]
    public async Task FullResponseWithIdenticalBytes_ReplacesNothingAndLeavesNoMarker()
    {
        var bytes = RdbBytes("Alpha", "Beta");
        WriteRdb(bytes);
        var handler = ScriptedHandler.Sync(_ => Ok(bytes, "\"v1\"", ModifiedA));

        var result = await Store(handler).EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.Null(Assert.Single(handler.Requests).IfNoneMatch);
        Assert.False(result.ReopenPending);
        Assert.False(File.Exists(RdbPath + ".prev"));
        var sidecar = ReadSidecar();
        Assert.Equal("\"v1\"", sidecar.GetProperty("etag").GetString());
        Assert.Equal(Sha(bytes), sidecar.GetProperty("sha256").GetString());
        Assert.False(sidecar.TryGetProperty("pendingReopenDigest", out var marker) && marker.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task ChangedValidRdb_IsReplacedWithPreviousKeptAndAMarkerLeft()
    {
        var oldBytes = RdbBytes("Alpha", "Beta");
        var newBytes = RdbBytes("Alpha", "Beta", "Gamma");
        WriteRdb(oldBytes);
        var handler = ScriptedHandler.Sync(_ => Ok(newBytes, "\"v2\"", ModifiedA));
        var store = Store(handler);

        var result = await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.True(result.ReopenPending);
        Assert.Equal(Sha(newBytes), result.Digest);
        Assert.Equal(newBytes, File.ReadAllBytes(RdbPath));
        Assert.Equal(oldBytes, File.ReadAllBytes(RdbPath + ".prev"));
        var sidecar = ReadSidecar();
        Assert.Equal(Sha(newBytes), sidecar.GetProperty("pendingReopenDigest").GetString());
        Assert.Equal("\"v2\"", sidecar.GetProperty("etag").GetString());
        Assert.Equal(3, sidecar.GetProperty("recordCount").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RdbPath)!, "*.update"));

        var index = await store.GetIndexAsync(Platform, Config(BaseUrl));
        Assert.Contains(RdbMatcher.NormalizeName("Gamma"), index!.ByName.Keys);
    }

    [Fact]
    public async Task ClearPendingReopen_OnlyClearsAMatchingDigest()
    {
        WriteRdb(RdbBytes("Alpha"));
        var store = Store(ScriptedHandler.Sync(_ => Ok(RdbBytes("Alpha", "Beta"), "\"v2\"", ModifiedA)));
        var result = await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        store.ClearPendingReopen(Platform, "not-the-digest");
        Assert.Equal(result.Digest, ReadSidecar().GetProperty("pendingReopenDigest").GetString());

        store.ClearPendingReopen(Platform, result.Digest!);
        Assert.False(ReadSidecar().TryGetProperty("pendingReopenDigest", out var marker) && marker.ValueKind == JsonValueKind.String);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("garbage")]
    [InlineData("shrunk")]
    public async Task UnusableReplacement_IsRejectedAndTheCurrentFileKept(string kind)
    {
        var current = RdbBytes(Enumerable.Range(0, 10).Select(i => "Game " + i).ToArray());
        WriteRdb(current);
        var replacement = kind switch
        {
            "truncated" => RdbBytes(Enumerable.Range(0, 12).Select(i => "Game " + i).ToArray())[..^3],
            "garbage" => Encoding.ASCII.GetBytes("<html>not an rdb</html>"),
            _ => RdbBytes("Game 0", "Game 1", "Game 2", "Game 3", "Game 4"),
        };
        var handler = ScriptedHandler.Sync(_ => Ok(replacement, "\"v2\"", ModifiedA));
        var store = Store(handler);

        var result = await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.False(result.ReopenPending);
        Assert.Equal(current, File.ReadAllBytes(RdbPath));
        Assert.False(File.Exists(RdbPath + ".prev"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RdbPath)!, "*.update"));
        Assert.False(File.Exists(SidecarPath) && ReadSidecar().TryGetProperty("pendingReopenDigest", out var marker) && marker.ValueKind == JsonValueKind.String);
        Assert.NotNull(await store.GetIndexAsync(Platform, Config(BaseUrl)));
    }

    [Fact]
    public async Task ReplacementTruncatedAtARecordBoundary_IsRejectedEvenAboveTheCountThreshold()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"Game {i:00}").ToArray();
        var current = RdbBytes(names);
        WriteRdb(current);
        var truncated = RdbBytes(names[..19]);
        BinaryPrimitives.WriteUInt64BigEndian(truncated.AsSpan(8, 8), (ulong)current.Length);
        var store = Store(ScriptedHandler.Sync(_ => Ok(truncated, "\"v2\"", ModifiedA)));

        var result = await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.False(result.ReopenPending);
        Assert.Equal(current, File.ReadAllBytes(RdbPath));
        Assert.False(File.Exists(RdbPath + ".prev"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task ReplacementWithMetadata_MustHaveTheDeclaredNumberOfRecords(byte count, bool accepted)
    {
        var current = RdbBytes("Alpha");
        WriteRdb(current);
        var records = RdbBytes("Alpha", "Beta");
        BinaryPrimitives.WriteUInt64BigEndian(records.AsSpan(8, 8), (ulong)records.Length + 1);
        byte[] replacement = [.. records, 0xc0, 0x81, 0xa5, (byte)'c', (byte)'o', (byte)'u', (byte)'n', (byte)'t', count];
        var store = Store(ScriptedHandler.Sync(_ => Ok(replacement, "\"v2\"", ModifiedA)));

        var result = await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.Equal(accepted, result.ReopenPending);
        Assert.Equal(accepted ? replacement : current, File.ReadAllBytes(RdbPath));
    }

    [Fact]
    public async Task UpdateWithAStalledResponseBody_TimesOutAndBuildsFromTheCachedFile()
    {
        var current = RdbBytes("Alpha");
        WriteRdb(current);
        using var body = new StalledResponseBody();
        var handler = ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var store = Store(handler);
        store.UpdateTimeoutForTests = TimeSpan.FromSeconds(1);

        var build = store.GetIndexAsync(Platform, Config(BaseUrl));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var index = await build.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(body.ReadToken.CanBeCanceled);
        Assert.True(body.ReadToken.IsCancellationRequested);
        Assert.Contains(RdbMatcher.NormalizeName("Alpha"), index!.ByName.Keys);
        Assert.Equal(current, File.ReadAllBytes(RdbPath));
        Assert.False(File.Exists(RdbPath + ".prev"));
        Assert.False(File.Exists(RdbPath + ".update"));
    }

    internal sealed class StalledResponseBody : MemoryStream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReadToken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailure_BuildsFromTheCurrentFileWithoutThrowing(bool timeout)
    {
        WriteRdb(RdbBytes("Alpha"));
        var handler = ScriptedHandler.Sync(_ => timeout
            ? throw new TaskCanceledException("timed out")
            : throw new HttpRequestException("down"));
        var store = Store(handler);

        var index = await store.GetIndexAsync(Platform, Config(BaseUrl));

        Assert.NotNull(index);
        Assert.Contains(RdbMatcher.NormalizeName("Alpha"), index!.ByName.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SecondLookupInTheSameRun_SendsNoRequest()
    {
        WriteRdb(RdbBytes("Alpha"));
        var handler = ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var store = Store(handler);

        await store.GetIndexAsync(Platform, Config(BaseUrl));
        await store.EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));
        await store.GetIndexAsync(Platform, Config(BaseUrl));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetIndexAsync_WaitsForAnInFlightCheckAndBuildsFromTheNewFile()
    {
        WriteRdb(RdbBytes("Alpha"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async _ =>
        {
            await release.Task;
            return Ok(RdbBytes("Alpha", "Gamma"), "\"v2\"", ModifiedA);
        });
        var store = Store(handler);

        var pending = store.GetIndexAsync(Platform, Config(BaseUrl));
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        Assert.Null(store.GetIndexOrStartBuild(Platform, Config(BaseUrl)));

        release.SetResult();
        var index = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(RdbMatcher.NormalizeName("Gamma"), index!.ByName.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MarkerForAFileThatWasNeverReplaced_IsCleared()
    {
        WriteRdb(RdbBytes("Alpha"));
        WriteSidecarJson(new { sha256 = "x", pendingReopenDigest = "deadbeef" });

        var result = await Store(ScriptedHandler.Sync(_ => throw new InvalidOperationException()))
            .EnsureUpdateCheckedAsync(Platform, Config(string.Empty));

        Assert.False(result.ReopenPending);
        Assert.False(ReadSidecar().TryGetProperty("pendingReopenDigest", out var marker) && marker.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task MarkerMatchingTheCurrentFile_ReportsAReopenPendingOnRestart()
    {
        var bytes = RdbBytes("Alpha");
        WriteRdb(bytes);
        WriteSidecarJson(new { etag = "\"v2\"", sha256 = Sha(bytes), pendingReopenDigest = Sha(bytes) });

        var result = await Store(ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotModified)))
            .EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.True(result.ReopenPending);
        Assert.Equal(Sha(bytes), result.Digest);
    }

    [Fact]
    public async Task MalformedSidecar_IsTreatedAsAbsent()
    {
        var bytes = RdbBytes("Alpha");
        WriteRdb(bytes);
        File.WriteAllText(SidecarPath, "{ not json");
        var handler = ScriptedHandler.Sync(_ => Ok(bytes, "\"v1\"", ModifiedA));

        var result = await Store(handler).EnsureUpdateCheckedAsync(Platform, Config(BaseUrl));

        Assert.False(result.ReopenPending);
        Assert.Null(Assert.Single(handler.Requests).IfNoneMatch);
        Assert.Equal("\"v1\"", ReadSidecar().GetProperty("etag").GetString());
    }

    [Fact]
    public async Task LocalFolderSource_IsCopiedOnlyWhenItChanged()
    {
        var source = Path.Combine(_root, "mirror");
        Directory.CreateDirectory(source);
        var oldBytes = RdbBytes("Alpha", "Beta");
        WriteRdb(oldBytes);
        File.WriteAllBytes(Path.Combine(source, Platform + ".rdb"), oldBytes);
        var handler = ScriptedHandler.Sync(_ => throw new InvalidOperationException("no HTTP for a local folder"));

        var unchanged = await Store(handler).EnsureUpdateCheckedAsync(Platform, Config(source));
        Assert.False(unchanged.ReopenPending);
        Assert.False(File.Exists(RdbPath + ".prev"));

        var newBytes = RdbBytes("Alpha", "Beta", "Gamma");
        File.WriteAllBytes(Path.Combine(source, Platform + ".rdb"), newBytes);
        var changed = await Store(handler).EnsureUpdateCheckedAsync(Platform, Config(source));

        Assert.True(changed.ReopenPending);
        Assert.Equal(Sha(newBytes), changed.Digest);
        Assert.Equal(newBytes, File.ReadAllBytes(RdbPath));
        Assert.Equal(oldBytes, File.ReadAllBytes(RdbPath + ".prev"));
        Assert.Empty(handler.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly DateTimeOffset ModifiedA = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static HttpResponseMessage Ok(byte[] body, string etag, DateTimeOffset modified)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        response.Content.Headers.LastModified = modified;
        return response;
    }

    private void WriteRdb(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RdbPath)!);
        File.WriteAllBytes(RdbPath, bytes);
    }

    private void WriteSidecarJson(object value) =>
        File.WriteAllText(SidecarPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    private JsonElement ReadSidecar() => JsonDocument.Parse(File.ReadAllText(SidecarPath)).RootElement.Clone();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Minimal libretro .rdb: 8-byte magic, 8-byte metadata offset, then one fixmap per game.
    internal static byte[] RdbBytes(params string[] names)
    {
        using var ms = new MemoryStream();
        ms.Write("RARCHDB"u8.ToArray());
        ms.WriteByte(0);
        ms.Write(new byte[8]);
        foreach (var name in names)
        {
            ms.WriteByte(0x81);
            WriteString(ms, "name");
            WriteString(ms, name);
        }

        return ms.ToArray();
    }

    private static void WriteString(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        ms.WriteByte(0xd9);
        ms.WriteByte((byte)bytes.Length);
        ms.Write(bytes);
    }

    internal sealed record RecordedRequest(string Url, string? IfNoneMatch, string? IfModifiedSince);

    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        private readonly System.Collections.Concurrent.ConcurrentQueue<RecordedRequest> _requests = new();

        public static ScriptedHandler Sync(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            new(request => Task.FromResult(responder(request)));

        public ScriptedHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(new RecordedRequest(
                request.RequestUri!.ToString(),
                request.Headers.IfNoneMatch.Count == 0 ? null : request.Headers.IfNoneMatch.ToString(),
                request.Headers.IfModifiedSince?.ToString("R")));
            return _responder(request);
        }
    }
}
