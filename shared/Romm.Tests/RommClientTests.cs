using System.Net;
using System.Text;
using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

/// <summary>
/// RomM reads its token only from the Authorization header, so these check what goes out on
/// the wire as well as what comes back.
/// </summary>
public class RommClientTests
{
    private const string BaseUrl = "http://romm.test:8080";
    private const string Token = "rmm_test";

    [Fact]
    public async Task LoadsEveryPageWithTheBearerToken()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == "/api/platforms")
            {
                return Json("""[ { "id": 10, "slug": "snes", "fs_slug": "snes", "display_name": "SNES", "firmware": [] } ]""");
            }

            var offset = path.Contains("offset=0") ? 0 : 500;
            var count = offset == 0 ? 500 : 3;
            var items = Enumerable.Range(offset + 1, count).Select(id =>
                $$"""{ "id": {{id}}, "platform_id": 10, "name": "Game {{id}}", "files": [ { "id": {{id}}, "file_name": "g{{id}}.sfc", "file_size_bytes": 1 } ] }""");
            return Json("{ \"items\": [" + string.Join(",", items) + "] }");
        });

        var snapshot = await Client(handler).LoadAsync(RommTestRules.Rules, CancellationToken.None);

        Assert.Equal(503, snapshot.Games.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer " + Token, request.Authorization));
        Assert.Contains("platform_ids=10", handler.Requests[1].Url);
        Assert.Contains("with_files=true", handler.Requests[1].Url);
    }

    [Fact]
    public async Task AskForTheRomFileItselfAndPassesTheRangeOn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent));
        var game = new RommGame { Id = 12, Rom = new RommFile { Id = 15, FileName = "Super Mario World (USA).sfc" } };

        using var response = await Client(handler).SendRomAsync(HttpMethod.Get, game, "bytes=0-15", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            BaseUrl + "/api/roms/12/content/Super%20Mario%20World%20%28USA%29.sfc?file_ids=15&purpose=play",
            request.Url);
        Assert.Equal("bytes=0-15", request.Range);
    }

    [Fact]
    public async Task AsksForEveryFileOfADiscSetAtOnce()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var game = new RommGame { Id = 20, Rom = new RommFile { Id = 32, FileName = "Final Fantasy VII (USA)" }, FileIds = new List<int> { 31, 30, 32 } };

        using var response = await Client(handler).SendRomAsync(HttpMethod.Get, game, null, CancellationToken.None);

        Assert.Equal(
            BaseUrl + "/api/roms/20/content/Final%20Fantasy%20VII%20%28USA%29?file_ids=31,30,32&purpose=play",
            Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task DropsARangeHeaderThatIsNotAByteRange()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var firmware = new RommFile { Id = 3, FileName = "scph5501.bin" };

        using var response = await Client(handler).SendFirmwareAsync(HttpMethod.Get, firmware, "items=0-5", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(BaseUrl + "/api/firmware/3/content/scph5501.bin", request.Url);
        Assert.Null(request.Range);
    }

    [Fact]
    public async Task DownloadsAZippedRomOnlyUpToTheCap()
    {
        var bytes = new byte[2048];
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/api/roms/8/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var client = Client(handler);
        var game = new RommGame { Id = 7, Rom = new RommFile { Id = 9, FileName = "Zipped (USA).zip" } };
        var missing = new RommGame { Id = 8, Rom = new RommFile { Id = 10, FileName = "Gone.zip" } };
        var path = Path.Combine(Path.GetTempPath(), $"romm-download-{Guid.NewGuid():N}.zip");
        try
        {
            Assert.Equal(HttpStatusCode.OK, await client.DownloadRomAsync(game, path, 4096, CancellationToken.None));
            Assert.Equal(2048, new FileInfo(path).Length);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await client.DownloadRomAsync(game, path, 1024, CancellationToken.None));
            Assert.Equal(HttpStatusCode.NotFound, await client.DownloadRomAsync(missing, path, 4096, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheConnectionTestReportsARejectedToken()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/heartbeat"
            ? Json("""{ "SYSTEM": { "VERSION": "5.4.1" } }""")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<RommRequestException>(() => Client(handler).CheckAsync(CancellationToken.None));

        Assert.Equal(401, error.StatusCode);
        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task TheConnectionTestCountsWhatMoonfinCanPlay()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/heartbeat"
            ? Json("""{ "SYSTEM": { "VERSION": "5.4.1" } }""")
            : Json("""
                [ { "id": 1, "slug": "snes", "fs_slug": "snes", "rom_count": 2, "firmware": [] },
                  { "id": 2, "slug": "saturn", "fs_slug": "saturn", "rom_count": 9, "firmware": [] } ]
                """));

        var info = await Client(handler).CheckAsync(CancellationToken.None);

        Assert.Equal("5.4.1", info.Version);
        Assert.Equal(2, info.PlatformCount);
        Assert.Equal(1, info.SupportedPlatformCount);
        Assert.Equal(2, info.SupportedRomCount);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080/", "http://192.168.1.20:8080")]
    [InlineData(" https://romm.example.com ", "https://romm.example.com")]
    [InlineData("ftp://romm.example.com", null)]
    [InlineData("http://user:pass@romm.example.com", null)]
    [InlineData("http://romm.example.com/?x=1", null)]
    [InlineData("romm.example.com", null)]
    [InlineData("", null)]
    public void NormalizesTheServerAddress(string input, string? expected) =>
        Assert.Equal(expected, RommClient.NormalizeBaseUrl(input));

    [Theory]
    [InlineData("/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23 17:47:20", BaseUrl + "/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23%2017:47:20")]
    [InlineData("/api/roms/1/content/x", null)]
    [InlineData("/assets/../api/users", null)]
    [InlineData("https://elsewhere.test/a.png", null)]
    [InlineData(null, null)]
    public void OnlyBuildsUrlsIntoRommsAssetFolder(string? path, string? expected) =>
        Assert.Equal(expected, RommClient.AssetUrl(BaseUrl, path));

    private static RommClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), BaseUrl, Token);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record SeenRequest(string Url, string? Authorization, string? Range);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<SeenRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Range", out var range) ? string.Join(",", range) : null));
            return Task.FromResult(_respond(request));
        }
    }
}
