using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class GameArtworkRdbUpdateTests : IDisposable
{
    private const string BaseUrl = "https://mirror.test/rdb";

    private static readonly string CurrentProviderVersion = (string)typeof(GameArtworkReconciliationService)
        .GetField("ProviderVersion", BindingFlags.Static | BindingFlags.NonPublic)!
        .GetRawConstantValue()!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-rdb-reconcile-{Guid.NewGuid():N}");

    private sealed record Fixture(
        GameArtworkReconciliationService Service,
        GameArtworkCatalog Catalog,
        RdbUpdateCheckTests.ScriptedHandler RdbHandler,
        RdbUpdateCheckTests.ScriptedHandler ArtHandler,
        string SnesPlatform,
        string GenesisPlatform,
        GameArtworkReconciliationService.ArtworkCandidate SnesMissing,
        GameArtworkReconciliationService.ArtworkCandidate SnesReady,
        GameArtworkReconciliationService.ArtworkCandidate GenesisMissing);

    [Fact]
    public async Task StartupPass_ChecksPlatformsOfAMissingOnlyLibraryAndReopensOnlyThatPlatformsMissingEntries()
    {
        var oldBytes = RdbUpdateCheckTests.RdbBytes("Alpha");
        var newBytes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        var fixture = await CreateAsync(
            oldBytes,
            request => request.RequestUri!.AbsoluteUri.Contains(Uri.EscapeDataString(SnesName), StringComparison.Ordinal)
                ? Ok(newBytes, "\"v2\"")
                : new HttpResponseMessage(HttpStatusCode.NotModified));

        await ReconcileAsync(fixture.Service);

        Assert.Equal(ArtworkCatalogState.Pending, (await fixture.Catalog.GetAsync(fixture.SnesMissing.Key))?.State);
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, (await fixture.Catalog.GetAsync(fixture.SnesReady.Key))?.State);
        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.GenesisMissing.Key))?.State);
        var queued = QueuedRemote(fixture.Service);
        Assert.Equal(ArtworkLane.Bulk, queued["remote:" + fixture.SnesMissing.Key.StorageKey]);
        Assert.DoesNotContain("remote:" + fixture.GenesisMissing.Key.StorageKey, queued.Keys);
        Assert.DoesNotContain("remote:" + fixture.SnesReady.Key.StorageKey, queued.Keys);
        Assert.Equal(newBytes, File.ReadAllBytes(RdbPath(fixture.SnesPlatform)));
        Assert.False(HasMarker(fixture.SnesPlatform));
        Assert.Empty(fixture.ArtHandler.Requests);
        Assert.Equal(2, fixture.RdbHandler.Requests.Count);
    }

    [Fact]
    public async Task StartupUpdate_AtQueueCapacity_ReopenedWorkRunsWhenCapacityReturns()
    {
        var updated = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        var fixture = await CreateAsync(
            RdbUpdateCheckTests.RdbBytes("Alpha"),
            request => request.RequestUri!.AbsoluteUri.Contains(Uri.EscapeDataString(SnesName), StringComparison.Ordinal)
                ? Ok(updated, "\"v2\"")
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        var first = GameArtworkReconciliationServiceTests.FillRemoteQueue(fixture.Service, fixture.SnesMissing);

        await ReconcileAsync(fixture.Service);

        Assert.Equal(ArtworkCatalogState.Pending, (await fixture.Catalog.GetAsync(fixture.SnesMissing.Key))?.State);
        Assert.DoesNotContain("remote:" + fixture.SnesMissing.Key.StorageKey, QueuedRemote(fixture.Service).Keys);
        GameArtworkReconciliationServiceTests.CompleteQueuedRemote(fixture.Service, first);
        Assert.Equal(ArtworkLane.Bulk, QueuedRemote(fixture.Service)["remote:" + fixture.SnesMissing.Key.StorageKey]);
        Assert.False(HasMarker(fixture.SnesPlatform));
    }

    [Fact]
    public async Task StartupPass_WithUnchangedSources_ReopensNothing()
    {
        var fixture = await CreateAsync(
            RdbUpdateCheckTests.RdbBytes("Alpha"),
            _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        await ReconcileAsync(fixture.Service);

        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.SnesMissing.Key))?.State);
        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.GenesisMissing.Key))?.State);
        Assert.Empty(QueuedRemote(fixture.Service));
        Assert.All(fixture.RdbHandler.Requests, request => Assert.Equal("\"v1\"", request.IfNoneMatch));
    }

    [Fact]
    public async Task MarkerLeftByAnInterruptedRun_IsRecoveredAtTheNextStartup()
    {
        // The file was replaced and the marker written, but the catalog step never ran.
        var newBytes = RdbUpdateCheckTests.RdbBytes("Alpha", "Beta");
        var fixture = await CreateAsync(
            newBytes,
            _ => new HttpResponseMessage(HttpStatusCode.NotModified),
            markerOnSnes: true);
        Assert.True(HasMarker(fixture.SnesPlatform));

        await ReconcileAsync(fixture.Service);

        Assert.Equal(ArtworkCatalogState.Pending, (await fixture.Catalog.GetAsync(fixture.SnesMissing.Key))?.State);
        Assert.Contains("remote:" + fixture.SnesMissing.Key.StorageKey, QueuedRemote(fixture.Service).Keys);
        Assert.Equal(ArtworkCatalogState.Missing, (await fixture.Catalog.GetAsync(fixture.GenesisMissing.Key))?.State);
        Assert.False(HasMarker(fixture.SnesPlatform));
    }

    [Fact]
    public async Task ReopenMissingAsync_TouchesOnlyMissingEntriesAndIsDurableOnReturn()
    {
        var catalogRoot = Path.Combine(_root, "catalog-direct");
        var catalog = new GameArtworkCatalog(catalogRoot, persistDebounce: TimeSpan.FromHours(1));
        var missing = ArtworkCatalogKey.Create("lib", "a.sfc", "f", "boxart");
        var ready = ArtworkCatalogKey.Create("lib", "b.sfc", "f", "boxart");
        var thumb = Path.Combine(_root, "t.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(thumb, [1]);
        await catalog.GetOrCreateAsync(missing, "snes", CurrentProviderVersion, "a");
        await catalog.MarkMissingAsync(missing, "snes", CurrentProviderVersion);
        await catalog.GetOrCreateAsync(ready, "snes", CurrentProviderVersion, "b");
        await catalog.MarkThumbnailReadyAsync(ready, "snes", thumb);

        var reopened = await catalog.ReopenMissingAsync("lib", [missing, ready]);

        Assert.Equal([missing.StorageKey], reopened.Select(key => key.StorageKey));
        var fresh = new GameArtworkCatalog(catalogRoot);
        Assert.Equal(ArtworkCatalogState.Pending, (await fresh.GetAsync(missing))?.State);
        Assert.Equal(ArtworkCatalogState.ThumbnailReady, (await fresh.GetAsync(ready))?.State);
        Assert.Equal("a", (await fresh.GetAsync(missing))?.GameId);
    }

    private const string SnesName = "Nintendo - Super Nintendo Entertainment System";

    private async Task<Fixture> CreateAsync(
        byte[] snesRdb,
        Func<HttpRequestMessage, HttpResponseMessage> rdbResponder,
        bool markerOnSnes = false)
    {
        var libraryRoot = Path.Combine(_root, "Games");
        Directory.CreateDirectory(Path.Combine(libraryRoot, "SNES"));
        Directory.CreateDirectory(Path.Combine(libraryRoot, "Genesis"));
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "SNES", "Chrono Trigger.sfc"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "SNES", "Super Mario World.sfc"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(libraryRoot, "Genesis", "Sonic.md"), [7, 8, 9]);

        var libraryId = Guid.NewGuid().ToString();
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var rdbHandler = RdbUpdateCheckTests.ScriptedHandler.Sync(rdbResponder);
        var artHandler = RdbUpdateCheckTests.ScriptedHandler.Sync(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var rdb = new RdbService(new FakeHttpClientFactory(rdbHandler), new NoOpLogger<RdbService>(), _root)
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
                new VirtualFolderInfo { Name = "Games", ItemId = libraryId, Locations = [libraryRoot] },
            ])),
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance,
            taskManager: null,
            watchLibraryFileSystem: false);

        var games = ((GamesService)typeof(GameArtworkReconciliationService)
            .GetField("_games", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).GetGames(libraryId, null);
        GameArtworkReconciliationService.ArtworkCandidate For(string fileName) =>
            ResolveCandidate(service, libraryId, games.Single(game => game.Title.StartsWith(fileName, StringComparison.OrdinalIgnoreCase)).Id);
        var snesMissing = For("Chrono");
        var snesReady = For("Super Mario");
        var genesisMissing = For("Sonic");

        // Fuzzy directory suggestions may add further platforms; only these two have cached files.
        var snesPlatform = SnesName;
        var genesisPlatform = "Sega - Mega Drive - Genesis";
        Assert.Contains(snesPlatform, GameThumbService.CandidatePlatforms(snesMissing.Source));
        Assert.Contains(genesisPlatform, GameThumbService.CandidatePlatforms(genesisMissing.Source));
        Assert.DoesNotContain(genesisPlatform, GameThumbService.CandidatePlatforms(snesMissing.Source));
        Assert.DoesNotContain(snesPlatform, GameThumbService.CandidatePlatforms(genesisMissing.Source));

        WriteRdb(snesPlatform, snesRdb, markerOnSnes);
        WriteRdb(genesisPlatform, RdbUpdateCheckTests.RdbBytes("Sonic"), marker: false);

        await catalog.GetOrCreateAsync(snesMissing.Key, snesMissing.SystemId, CurrentProviderVersion, snesMissing.GameId);
        await catalog.MarkMissingAsync(snesMissing.Key, snesMissing.SystemId, CurrentProviderVersion);
        await catalog.GetOrCreateAsync(genesisMissing.Key, genesisMissing.SystemId, CurrentProviderVersion, genesisMissing.GameId);
        await catalog.MarkMissingAsync(genesisMissing.Key, genesisMissing.SystemId, CurrentProviderVersion);
        var thumb = Path.Combine(_root, "ready.png");
        await File.WriteAllBytesAsync(thumb, [1]);
        await catalog.GetOrCreateAsync(snesReady.Key, snesReady.SystemId, CurrentProviderVersion, snesReady.GameId);
        await catalog.MarkThumbnailReadyAsync(snesReady.Key, snesReady.SystemId, thumb);

        return new Fixture(service, catalog, rdbHandler, artHandler, snesPlatform, genesisPlatform, snesMissing, snesReady, genesisMissing);
    }

    private string RdbPath(string platform) => Path.Combine(_root, "gamemeta", platform + ".rdb");

    // Cached RDB plus a sidecar as an earlier run would have left it (ETag "v1").
    private void WriteRdb(string platform, byte[] bytes, bool marker)
    {
        Directory.CreateDirectory(Path.Combine(_root, "gamemeta"));
        File.WriteAllBytes(RdbPath(platform), bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(
            RdbPath(platform) + ".json",
            JsonSerializer.Serialize(
                new { etag = "\"v1\"", sha256 = sha, pendingReopenDigest = marker ? sha : null },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private bool HasMarker(string platform) =>
        JsonDocument.Parse(File.ReadAllText(RdbPath(platform) + ".json")).RootElement
            .TryGetProperty("pendingReopenDigest", out var marker) && marker.ValueKind == JsonValueKind.String;

    private static HttpResponseMessage Ok(byte[] body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return response;
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
            var lane = pair.Value!.GetType().GetProperty("Lane")!.GetValue(pair.Value);
            result[(string)pair.Key] = (ArtworkLane)lane!;
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
