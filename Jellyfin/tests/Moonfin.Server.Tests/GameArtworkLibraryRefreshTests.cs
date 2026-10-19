using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class GameArtworkLibraryRefreshTests : IDisposable
{
    private const string BaseUrl = "https://mirror.test/rdb";
    private const string SnesName = "Nintendo - Super Nintendo Entertainment System";
    private const string GenesisName = "Sega - Mega Drive - Genesis";

    private static readonly string CurrentProviderVersion = (string)typeof(GameArtworkReconciliationService)
        .GetField("ProviderVersion", BindingFlags.Static | BindingFlags.NonPublic)!
        .GetRawConstantValue()!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-library-refresh-{Guid.NewGuid():N}");
    private readonly string _libraryId = Guid.NewGuid().ToString();
    private byte[]? _upstreamSnes;
    private Task? _gate;
    private Stream? _stagingBody;

    private sealed record Fixture(
        GameArtworkReconciliationService Service,
        GameArtworkCatalog Catalog,
        RdbService Rdb,
        RdbUpdateCheckTests.ScriptedHandler RdbHandler,
        Dictionary<string, GameArtworkReconciliationService.ArtworkCandidate> Games);

    [Fact]
    public void Endpoint_RequiresElevationAndRoutesBesideThePerGameRefresh()
    {
        var method = typeof(GamesController).GetMethod(nameof(GamesController.RefreshLibraryArtwork))!;

        Assert.Equal("RequiresElevation", method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("{libraryId}/Artwork/Refresh", method.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    [Fact]
    public async Task Endpoint_WithGamesDisabled_IsNotFound()
    {
        var controller = (GamesController)RuntimeHelpers.GetUninitializedObject(typeof(GamesController));

        Assert.IsType<NotFoundResult>(await controller.RefreshLibraryArtwork("any", null, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownLibraryAndUnknownSystem_AreRejectedWithoutTouchingAnything()
    {
        var fixture = await CreateAsync();

        Assert.Equal(LibraryArtworkRefreshStatus.NotFound, (await fixture.Service.RefreshLibraryArtworkAsync("nope", null)).Status);
        Assert.Equal(LibraryArtworkRefreshStatus.BadSystem, (await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, "Atari")).Status);
        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.Games["Chrono"].Key))?.State);
        Assert.Empty(QueuedRemote(fixture.Service));
    }

    [Fact]
    public async Task ConcurrentRefreshOfTheSameLibrary_IsAConflictAndTheGuardIsReleasedAfterwards()
    {
        var fixture = await CreateAsync();
        var gate = new TaskCompletionSource();
        _gate = gate.Task;
        _upstreamSnes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        var requestsBefore = fixture.RdbHandler.Requests.Count;

        var first = fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);
        await WaitUntilAsync(() => fixture.RdbHandler.Requests.Count > requestsBefore);
        var second = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);
        gate.SetResult();
        var firstResult = await first;
        var third = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);

        Assert.Equal(LibraryArtworkRefreshStatus.Conflict, second.Status);
        Assert.Equal(LibraryArtworkRefreshStatus.Accepted, firstResult.Status);
        Assert.Equal(LibraryArtworkRefreshStatus.Accepted, third.Status);
    }

    [Fact]
    public async Task Refresh_ReopensOnlyAcquisitionStageEntriesOnTheBulkLane()
    {
        var fixture = await CreateAsync();
        var zelda = await fixture.Catalog.GetAsync(fixture.Games["Zelda"].Key);

        var result = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);

        Assert.Equal(LibraryArtworkRefreshStatus.Accepted, result.Status);
        Assert.Equal(3, result.Reopened);
        var queued = QueuedRemote(fixture.Service);
        foreach (var name in new[] { "Chrono", "Mario", "Sonic" })
        {
            Assert.Equal(ArtworkCatalogState.Pending, (await fixture.Catalog.GetAsync(fixture.Games[name].Key))?.State);
            Assert.Equal(ArtworkLane.Bulk, queued["remote:" + fixture.Games[name].Key.StorageKey]);
        }

        Assert.Equal(zelda, await fixture.Catalog.GetAsync(fixture.Games["Zelda"].Key));
        Assert.Equal(ArtworkCatalogState.OriginalReady, zelda!.State);
        Assert.NotNull(zelda.RetryAfterUtc);
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, (await fixture.Catalog.GetAsync(fixture.Games["Earthbound"].Key))?.State);
        Assert.Equal(ArtworkCatalogState.Pending, (await fixture.Catalog.GetAsync(fixture.Games["Metroid"].Key))?.State);
        Assert.Equal(ArtworkCatalogState.MetadataDeferred, (await fixture.Catalog.GetAsync(fixture.Games["Contra"].Key))?.State);
        foreach (var name in new[] { "Zelda", "Earthbound", "Metroid", "Contra" })
        {
            Assert.DoesNotContain("remote:" + fixture.Games[name].Key.StorageKey, queued.Keys);
        }

        var onDisk = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        Assert.Equal(ArtworkCatalogState.Pending, (await onDisk.GetAsync(fixture.Games["Chrono"].Key))?.State);
    }

    [Fact]
    public async Task Refresh_AtQueueCapacity_RetainsEveryReopenedEntryUntilCapacityReturns()
    {
        var fixture = await CreateAsync();
        var first = GameArtworkReconciliationServiceTests.FillRemoteQueue(fixture.Service, fixture.Games["Chrono"]);

        var result = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);

        Assert.Equal(3, result.Reopened);
        Assert.Equal(20_000, QueuedRemote(fixture.Service).Count);
        GameArtworkReconciliationServiceTests.CompleteQueuedRemote(fixture.Service, first);
        var seen = new HashSet<string>();
        for (var i = 0; i < 3; i++)
        {
            var queued = QueuedRemote(fixture.Service);
            var admitted = Assert.Single(new[] { "Chrono", "Mario", "Sonic" }, name => queued.ContainsKey("remote:" + fixture.Games[name].Key.StorageKey));
            Assert.True(seen.Add(admitted));
            if (i < 2)
            {
                GameArtworkReconciliationServiceTests.CompleteQueuedRemote(fixture.Service, fixture.Games[admitted]);
            }
        }

        Assert.Equal(20_000, QueuedRemote(fixture.Service).Count);
        var persisted = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        foreach (var name in new[] { "Chrono", "Mario", "Sonic" })
        {
            Assert.Equal(ArtworkCatalogState.Pending, (await persisted.GetAsync(fixture.Games[name].Key))?.State);
        }
    }

    [Fact]
    public async Task Refresh_CancelledDuringRdbBody_StopsStagingAndReleasesTheLibraryGuard()
    {
        var fixture = await CreateAsync();
        var original = File.ReadAllBytes(RdbPath(SnesName));
        using var body = new RdbUpdateCheckTests.StalledResponseBody();
        _stagingBody = body;
        using var cancellation = new CancellationTokenSource();
        var refresh = fixture.Service.RefreshLibraryArtworkAsync(_libraryId, "snes", cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(original, File.ReadAllBytes(RdbPath(SnesName)));
        Assert.False(File.Exists(RdbPath(SnesName) + ".next"));
        Assert.False(File.Exists(RdbPath(SnesName) + ".next.tmp"));
        _stagingBody = null;
        var retried = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, "snes");
        Assert.Equal(LibraryArtworkRefreshStatus.Accepted, retried.Status);
    }

    [Fact]
    public async Task SystemFilter_LimitsBothTheReopenAndTheStaging()
    {
        var fixture = await CreateAsync();
        _upstreamSnes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        var requestsBefore = fixture.RdbHandler.Requests.Count;

        var result = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, "snes");

        Assert.Equal(2, result.Reopened);
        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.Games["Sonic"].Key))?.State);
        Assert.DoesNotContain("remote:" + fixture.Games["Sonic"].Key.StorageKey, QueuedRemote(fixture.Service).Keys);
        Assert.Equal([SnesName], result.RdbUpdatesStaged);
        Assert.DoesNotContain(
            fixture.RdbHandler.Requests.Skip(requestsBefore),
            request => request.Url.Contains(Uri.EscapeDataString(GenesisName), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedRdb_IsStagedBesideTheLiveFileWhichStaysUntouched()
    {
        var fixture = await CreateAsync();
        var liveBytes = File.ReadAllBytes(RdbPath(SnesName));
        var liveSidecar = File.ReadAllText(RdbPath(SnesName) + ".json");
        var newBytes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        _upstreamSnes = newBytes;

        var result = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);

        Assert.True(result.RestartRequired);
        Assert.Equal([SnesName], result.RdbUpdatesStaged);
        Assert.Equal(newBytes, File.ReadAllBytes(RdbPath(SnesName) + ".next"));
        var meta = JsonDocument.Parse(File.ReadAllText(RdbPath(SnesName) + ".next.json")).RootElement;
        Assert.Equal("\"v2\"", meta.GetProperty("etag").GetString());
        Assert.Equal(Sha(newBytes), meta.GetProperty("sha256").GetString());
        Assert.Equal(2, meta.GetProperty("recordCount").GetInt32());
        Assert.Equal(liveBytes, File.ReadAllBytes(RdbPath(SnesName)));
        Assert.Equal(liveSidecar, File.ReadAllText(RdbPath(SnesName) + ".json"));
        Assert.False(File.Exists(RdbPath(SnesName) + ".prev"));
    }

    [Fact]
    public async Task UnchangedRdb_StagesNothing()
    {
        var fixture = await CreateAsync();

        var result = await fixture.Service.RefreshLibraryArtworkAsync(_libraryId, null);

        Assert.Empty(result.RdbUpdatesStaged);
        Assert.False(result.RestartRequired);
        Assert.False(File.Exists(RdbPath(SnesName) + ".next"));
    }

    [Fact]
    public async Task AfterARestart_TheStagedRdbIsAppliedAndThatPlatformsMissingEntriesAreReopened()
    {
        var first = await CreateAsync();
        _upstreamSnes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        await first.Service.RefreshLibraryArtworkAsync(_libraryId, null);
        var oldBytes = File.ReadAllBytes(RdbPath(SnesName));
        // The reopened entries failed again after the refresh.
        await first.Catalog.MarkMissingAsync(first.Games["Chrono"].Key, first.Games["Chrono"].SystemId, CurrentProviderVersion);
        await first.Catalog.MarkMissingAsync(first.Games["Sonic"].Key, first.Games["Sonic"].SystemId, CurrentProviderVersion);

        var restartedCatalog = first.Catalog;
        var restarted = BuildService(restartedCatalog);
        await ReconcileAsync(restarted.Service);

        Assert.Equal(_upstreamSnes, File.ReadAllBytes(RdbPath(SnesName)));
        Assert.Equal(oldBytes, File.ReadAllBytes(RdbPath(SnesName) + ".prev"));
        Assert.False(File.Exists(RdbPath(SnesName) + ".next"));
        Assert.False(File.Exists(RdbPath(SnesName) + ".next.json"));
        Assert.Equal(ArtworkCatalogState.Pending, (await restartedCatalog.GetAsync(first.Games["Chrono"].Key))?.State);
        Assert.Contains("remote:" + first.Games["Chrono"].Key.StorageKey, QueuedRemote(restarted.Service).Keys);
        Assert.Equal(ArtworkCatalogState.Missing, (await restartedCatalog.GetAsync(first.Games["Sonic"].Key))?.State);
    }

    private async Task<Fixture> CreateAsync()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "SNES"));
        Directory.CreateDirectory(Path.Combine(libraryRoot, "Genesis"));
        byte seed = 1;
        foreach (var rom in new[] { "SNES/Chrono Trigger.sfc", "SNES/Super Mario World.sfc", "SNES/Zelda.sfc", "SNES/Earthbound.sfc", "SNES/Metroid.sfc", "SNES/Contra.sfc", "Genesis/Sonic.md" })
        {
            await File.WriteAllBytesAsync(Path.Combine(libraryRoot, rom), [seed++, seed++, seed++]);
        }

        Directory.CreateDirectory(Path.Combine(_root, "gamemeta"));
        WriteRdb(SnesName, RdbUpdateCheckTests.RdbBytes("Alpha"));
        WriteRdb(GenesisName, RdbUpdateCheckTests.RdbBytes("Sonic"));

        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var (service, rdb, handler) = BuildService(catalog);
        var games = ((GamesService)typeof(GameArtworkReconciliationService)
            .GetField("_games", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).GetGames(_libraryId, null);
        var candidates = new Dictionary<string, GameArtworkReconciliationService.ArtworkCandidate>(StringComparer.Ordinal);
        foreach (var name in new[] { "Chrono", "Mario", "Zelda", "Earthbound", "Metroid", "Contra", "Sonic" })
        {
            var game = games.Single(item => item.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
            candidates[name] = ResolveCandidate(service, _libraryId, game.Id);
            await catalog.GetOrCreateAsync(candidates[name].Key, candidates[name].SystemId, CurrentProviderVersion, candidates[name].GameId);
        }

        GameArtworkReconciliationService.ArtworkCandidate Entry(string name) => candidates[name];
        foreach (var name in new[] { "Chrono", "Sonic" })
        {
            await catalog.MarkMissingAsync(Entry(name).Key, Entry(name).SystemId, CurrentProviderVersion);
        }

        await catalog.MarkRetryableAsync(Entry("Mario").Key, Entry("Mario").SystemId, ArtworkCatalogWorkStage.AcquireOriginal, DateTimeOffset.UtcNow.AddHours(1));
        var original = Path.Combine(_root, "zelda-original.png");
        await File.WriteAllBytesAsync(original, [1]);
        await catalog.MarkOriginalReadyAsync(Entry("Zelda").Key, Entry("Zelda").SystemId, original);
        await catalog.MarkRetryableAsync(Entry("Zelda").Key, Entry("Zelda").SystemId, ArtworkCatalogWorkStage.DeriveThumbnail, DateTimeOffset.UtcNow.AddHours(1));
        var thumb = Path.Combine(_root, "earthbound.png");
        await File.WriteAllBytesAsync(thumb, [1]);
        await catalog.MarkThumbnailReadyAsync(Entry("Earthbound").Key, Entry("Earthbound").SystemId, thumb);
        await catalog.MarkMetadataPendingAsync(
            Entry("Contra").Key,
            Entry("Contra").SystemId,
            CurrentProviderVersion,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromHours(3),
            TimeSpan.Zero);

        // The startup check has already run, as it has on a server that has been up for a while.
        await rdb.EnsureUpdateCheckedAsync(SnesName);
        await rdb.EnsureUpdateCheckedAsync(GenesisName);
        Assert.Equal(ArtworkCatalogState.MetadataDeferred, (await catalog.GetAsync(Entry("Contra").Key))?.State);
        return new Fixture(service, catalog, rdb, handler, candidates);
    }

    private (GameArtworkReconciliationService Service, RdbService Rdb, RdbUpdateCheckTests.ScriptedHandler Handler) BuildService(GameArtworkCatalog catalog)
    {
        var handler = new RdbUpdateCheckTests.ScriptedHandler(async request =>
        {
            if (_gate != null)
            {
                await _gate;
            }

            if (_stagingBody != null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_stagingBody) };
            }

            return request.RequestUri!.AbsoluteUri.Contains(Uri.EscapeDataString(SnesName), StringComparison.Ordinal) && _upstreamSnes is { } bytes
                ? Ok(bytes, "\"v2\"")
                : new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var artHandler = RdbUpdateCheckTests.ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var rdb = new RdbService(new FakeHttpClientFactory(handler), new NoOpLogger<RdbService>(), _root)
        {
            ConfigOverrideForTests = new global::Moonfin.Server.PluginConfiguration { GamesMetadataDbUrlBase = BaseUrl },
        };
        var thumbs = new GameThumbService(
            new FakeHttpClientFactory(artHandler),
            new NoOpLogger<GameThumbService>(),
            rdb,
            Path.Combine(_root, "thumb-data"));
        var service = new GameArtworkReconciliationService(
            new GamesService(new FakeLibraryManager(
            [
                new VirtualFolderInfo { Name = "Games", ItemId = _libraryId, Locations = [Path.Combine(_root, "Games")] },
            ])),
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null,
            watchLibraryFileSystem: false);
        return (service, rdb, handler);
    }

    private string RdbPath(string platform) => Path.Combine(_root, "gamemeta", platform + ".rdb");

    private void WriteRdb(string platform, byte[] bytes)
    {
        File.WriteAllBytes(RdbPath(platform), bytes);
        File.WriteAllText(
            RdbPath(platform) + ".json",
            JsonSerializer.Serialize(
                new { etag = "\"v1\"", sha256 = Sha(bytes), recordCount = 1 },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpResponseMessage Ok(byte[] body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition");
            await Task.Delay(10);
        }
    }

    private static Task ReconcileAsync(GameArtworkReconciliationService service) =>
        (Task)typeof(GameArtworkReconciliationService)
            .GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, BindingFlags.DoNotWrapExceptions, binder: null, parameters: [CancellationToken.None, false], culture: null)!;

    private static Dictionary<string, ArtworkLane> QueuedRemote(GameArtworkReconciliationService service)
    {
        var queued = (System.Collections.IDictionary)typeof(GameArtworkReconciliationService)
            .GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var result = new Dictionary<string, ArtworkLane>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry pair in queued)
        {
            result[(string)pair.Key] = (ArtworkLane)pair.Value!.GetType().GetProperty("Lane")!.GetValue(pair.Value)!;
        }

        return result;
    }

    private static GameArtworkReconciliationService.ArtworkCandidate ResolveCandidate(
        GameArtworkReconciliationService service,
        string libraryId,
        string gameId)
    {
        var method = typeof(GameArtworkReconciliationService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "TryCreateCandidate" &&
                candidate.GetParameters().FirstOrDefault()?.ParameterType == typeof(string));
        return Assert.IsType<GameArtworkReconciliationService.ArtworkCandidate>(method.Invoke(
            service,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            parameters: [libraryId, gameId, GameThumbService.ThumbKind.Boxart, null],
            culture: null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class RdbStagedUpdateTests : IDisposable
{
    private const string Platform = "Nintendo - Super Nintendo Entertainment System";
    private const string BaseUrl = "https://mirror.test/rdb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-rdb-staged-{Guid.NewGuid():N}");
    private byte[]? _upstream;

    private string RdbPath => Path.Combine(_root, "gamemeta", Platform + ".rdb");

    private static global::Moonfin.Server.PluginConfiguration Config => new() { GamesMetadataDbUrlBase = BaseUrl };

    private RdbIndexStore Store(out RdbUpdateCheckTests.ScriptedHandler handler)
    {
        handler = RdbUpdateCheckTests.ScriptedHandler.Sync(_ =>
        {
            if (_upstream is not { } bytes)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"v2\"");
            return response;
        });
        return new RdbIndexStore(new FakeHttpClientFactory(handler), new NoOpLogger<RdbService>(), _root);
    }

    private byte[] WriteLive(params string[] names)
    {
        var bytes = RdbUpdateCheckTests.RdbBytes(names);
        Directory.CreateDirectory(Path.GetDirectoryName(RdbPath)!);
        File.WriteAllBytes(RdbPath, bytes);
        File.WriteAllText(
            RdbPath + ".json",
            JsonSerializer.Serialize(
                new { etag = "\"v1\"", sha256 = Sha(bytes), recordCount = names.Length },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task Stage_LeavesTheLiveFileIndexAndSidecarUntouched()
    {
        var live = WriteLive("Alpha");
        var store = Store(out _);
        var before = await store.GetIndexAsync(Platform, Config);
        var sidecarBefore = File.ReadAllText(RdbPath + ".json");
        _upstream = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");

        Assert.True(await store.StageUpdateAsync(Platform, Config, CancellationToken.None));

        Assert.Equal(_upstream, File.ReadAllBytes(RdbPath + ".next"));
        Assert.Equal(live, File.ReadAllBytes(RdbPath));
        Assert.Equal(sidecarBefore, File.ReadAllText(RdbPath + ".json"));
        Assert.Same(before, await store.GetIndexAsync(Platform, Config));
        Assert.DoesNotContain(RdbMatcher.NormalizeName("Beta"), before!.ByName.Keys);
    }

    [Fact]
    public async Task Stage_WhenUpstreamMatchesTheLiveFile_StagesNothingAndDropsAStaleStagedCopy()
    {
        var live = WriteLive("Alpha");
        var store = Store(out _);
        await store.EnsureUpdateCheckedAsync(Platform, Config);
        _upstream = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        Assert.True(await store.StageUpdateAsync(Platform, Config, CancellationToken.None));

        _upstream = live;

        Assert.False(await store.StageUpdateAsync(Platform, Config, CancellationToken.None));
        Assert.False(File.Exists(RdbPath + ".next"));
        Assert.False(File.Exists(RdbPath + ".next.json"));
    }

    [Fact]
    public async Task StageThenFirstLoadInTheSameProcess_BuildsFromTheOldFile_AndARestartAppliesTheStagedOne()
    {
        var live = WriteLive("Alpha");
        var store = Store(out _);
        await store.EnsureUpdateCheckedAsync(Platform, Config);
        _upstream = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta", "Gamma");
        Assert.True(await store.StageUpdateAsync(Platform, Config, CancellationToken.None));

        var index = await store.GetIndexAsync(Platform, Config);

        Assert.DoesNotContain(RdbMatcher.NormalizeName("Gamma"), index!.ByName.Keys);
        Assert.Equal(live, File.ReadAllBytes(RdbPath));
        Assert.True(File.Exists(RdbPath + ".next"));

        var restarted = Store(out var handler);
        var result = await restarted.EnsureUpdateCheckedAsync(Platform, Config);

        Assert.True(result.ReopenPending);
        Assert.Equal(Sha(_upstream), result.Digest);
        Assert.Equal(_upstream, File.ReadAllBytes(RdbPath));
        Assert.Equal(live, File.ReadAllBytes(RdbPath + ".prev"));
        Assert.False(File.Exists(RdbPath + ".next"));
        Assert.False(File.Exists(RdbPath + ".next.json"));
        var sidecar = JsonDocument.Parse(File.ReadAllText(RdbPath + ".json")).RootElement;
        Assert.Equal("\"v2\"", sidecar.GetProperty("etag").GetString());
        Assert.Equal(3, sidecar.GetProperty("recordCount").GetInt32());
        Assert.Equal("\"v2\"", Assert.Single(handler.Requests).IfNoneMatch);
        Assert.Contains(RdbMatcher.NormalizeName("Gamma"), (await restarted.GetIndexAsync(Platform, Config))!.ByName.Keys);
    }

    [Fact]
    public async Task ConcurrentStagesOfOnePlatform_LeaveOneValidStagedFile()
    {
        WriteLive("Alpha");
        var store = Store(out _);
        await store.EnsureUpdateCheckedAsync(Platform, Config);
        _upstream = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");

        var results = await Task.WhenAll(
            Task.Run(() => store.StageUpdateAsync(Platform, Config, CancellationToken.None)),
            Task.Run(() => store.StageUpdateAsync(Platform, Config, CancellationToken.None)));

        Assert.All(results, Assert.True);
        Assert.Equal(2, RdbReader.ReadAll(RdbPath + ".next").Count);
        Assert.Equal(
            [RdbPath + ".next", RdbPath + ".next.json"],
            Directory.GetFiles(Path.GetDirectoryName(RdbPath)!, "*.next*").OrderBy(path => path, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("shrunk")]
    public async Task InvalidInheritedStagedFile_IsDeletedAndTheCurrentFileKept(string kind)
    {
        var live = WriteLive(Enumerable.Range(0, 10).Select(i => "Game " + i).ToArray());
        File.WriteAllBytes(
            RdbPath + ".next",
            kind == "garbage" ? "<html>not an rdb</html>"u8.ToArray() : RdbUpdateCheckTests.RdbBytes("Game 0", "Game 1"));
        File.WriteAllText(RdbPath + ".next.json", "{}");

        var result = await Store(out _).EnsureUpdateCheckedAsync(Platform, Config);

        Assert.False(result.ReopenPending);
        Assert.Equal(live, File.ReadAllBytes(RdbPath));
        Assert.False(File.Exists(RdbPath + ".next"));
        Assert.False(File.Exists(RdbPath + ".next.json"));
        Assert.False(File.Exists(RdbPath + ".prev"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class GameArtworkMissCacheTests : IDisposable
{
    private const string Platform = "Sega - Master System - Mark III";
    private const string RomFileName = "Great Golf.sms";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-miss-cache-{Guid.NewGuid():N}");

    private GameArtworkStore CreateStore(RdbUpdateCheckTests.ScriptedHandler handler)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, RomFileName), [9, 9, 9, 9]);
        return new GameArtworkStore(
            new FakeHttpClientFactory(handler),
            new NoOpLogger<GameArtworkStore>(),
            new RdbService(new FakeHttpClientFactory(handler), new NoOpLogger<RdbService>(), _root),
            _root,
            GameArtworkStore.MaxArtworkBytes);
    }

    private Task<GameArtworkLookupResult> LookupAsync(GameArtworkStore store) =>
        store.LookupThumbAsync(
            core: "segaMS",
            coreWasDefaulted: false,
            systemName: "Sega Master System",
            romPath: Path.Combine(_root, RomFileName),
            title: null,
            kind: GameThumbService.ThumbKind.Boxart,
            cancellationToken: CancellationToken.None,
            budget: GameArtworkStore.PrewarmRequestBudget);

    [Fact]
    public async Task ACachedMiss_IsProbedAgainAfterTheCachesAreCleared()
    {
        var handler = RdbUpdateCheckTests.ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = CreateStore(handler);

        Assert.Equal(GameArtworkLookupOutcome.Missing, (await LookupAsync(store)).Outcome);
        var afterFirst = handler.Requests.Count;
        await LookupAsync(store);
        Assert.Equal(afterFirst, handler.Requests.Count);

        store.ClearMissCache();
        await LookupAsync(store);

        Assert.True(handler.Requests.Count > afterFirst);
    }

    [Fact]
    public async Task A404ThatCompletesAfterTheClear_IsNotRecorded()
    {
        var release = new TaskCompletionSource();
        var handler = new RdbUpdateCheckTests.ScriptedHandler(async _ =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var store = CreateStore(handler);

        var inFlight = LookupAsync(store);
        await WaitUntilAsync(() => handler.Requests.Count > 0);
        store.ClearMissCache();
        release.SetResult();
        await inFlight;
        var afterFirst = handler.Requests.Count;
        await LookupAsync(store);

        Assert.True(handler.Requests.Count > afterFirst);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
