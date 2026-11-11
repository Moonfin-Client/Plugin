using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Model.Logging;
using Moonfin.Calendar;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The Upcoming rows read the Radarr and Sonarr calendars through Seerr's arr settings. These pin
/// which server is used and when the External URL set in Seerr is tried.
/// </summary>
public class SeerrArrCalendarTests
{
    private const string Query = "start=2026-10-01&end=2026-12-30";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TheSessionCheckReadsTheCurrentSeerrPermissions()
    {
        Assert.Equal(4196386, SeerrSessionService.PermissionsFrom(Encoding.UTF8.GetBytes("""{ "id": 2, "permissions": 4196386 }""")));
        Assert.Null(SeerrSessionService.PermissionsFrom(Encoding.UTF8.GetBytes("""{ "id": 2 }""")));
        Assert.Null(SeerrSessionService.PermissionsFrom(Encoding.UTF8.GetBytes("[]")));
        Assert.Null(SeerrSessionService.PermissionsFrom(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void TheRegularDefaultWinsOverA4kServerListedFirst()
    {
        var servers = Json("""
            [
              { "name": "4k", "is4k": true, "isDefault": true },
              { "name": "spare", "is4k": false, "isDefault": false },
              { "name": "main", "is4k": false, "isDefault": true }
            ]
            """);

        Assert.Equal("main", SeerrSessionService.SelectArrServer(servers).GetProperty("name").GetString());
    }

    [Fact]
    public void ASingleServerIsUsedWhateverItsFlags()
    {
        var servers = Json("""[{ "name": "only", "is4k": true }]""");

        Assert.Equal("only", SeerrSessionService.SelectArrServer(servers).GetProperty("name").GetString());
    }

    [Fact]
    public void TheExternalUrlComesSecond()
    {
        var server = Json("""{ "hostname": "radarr.lan", "port": 7878, "baseUrl": "/radarr", "externalUrl": "https://media.example.com/radarr/" }""");

        Assert.Equal(
            new[] { "http://radarr.lan:7878/radarr", "https://media.example.com/radarr" },
            SeerrSessionService.ArrCalendarBases(server, isSonarr: false));
    }

    [Fact]
    public async Task AReachableInternalAddressIsTheOnlyRequest()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.lan", HttpStatusCode.OK, "[]");

        var result = await Fetch(handler, new[] { "http://radarr.lan:7878", "https://radarr.example.com" });

        Assert.Equal(200, result.StatusCode);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task AnErrorFromTheInternalAddressIsPassedOnWithoutTryingTheExternalUrl()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.lan", HttpStatusCode.Unauthorized, "{}");

        var result = await Fetch(handler, new[] { "http://radarr.lan:7878", "https://radarr.example.com" });

        Assert.Equal(401, result.StatusCode);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task AnUnreachableInternalAddressFallsBackToTheExternalUrl()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.example.com", HttpStatusCode.OK, "[{\"title\":\"external\"}]");

        var result = await Fetch(handler, new[] { "http://radarr.lan:7878", "https://radarr.example.com" });

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("[{\"title\":\"external\"}]", Encoding.UTF8.GetString(result.Body!));
        Assert.Equal("https://radarr.example.com/api/v3/calendar?" + Query, handler.Requested.Last());
        Assert.Equal(new[] { "secret", "secret" }, handler.ApiKeys);
    }

    [Fact]
    public async Task NoReachableAddressIsTheSame502AsBefore()
    {
        var result = await Fetch(new ArrHandler(), new[] { "http://radarr.lan:7878" });

        Assert.Equal(502, result.StatusCode);
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.5", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("198.51.100.10", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void OnlyLocalNetworkAddressesCount(string address, bool local)
    {
        Assert.Equal(local, SeerrSessionService.IsLocalNetworkAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task APlainHttpExternalUrlOnTheInternetNeverGetsTheApiKey()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.example.com", HttpStatusCode.OK, "[]");

        var result = await Fetch(handler, new[] { "http://radarr.lan:7878", "http://radarr.example.com" }, Resolves("198.51.100.10"));

        Assert.Equal(502, result.StatusCode);
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task APlainHttpExternalUrlOnTheLocalNetworkIsUsed()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.home", HttpStatusCode.OK, "[]");

        var result = await Fetch(handler, new[] { "http://radarr.lan:7878", "http://radarr.home:7878" }, Resolves("192.168.1.20"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, handler.Requested.Count);
    }

    [Theory]
    [InlineData(true, "&includeSeries=true&includeEpisodeImages=true&unmonitored=true")]
    [InlineData(false, "&includeSeries=true&includeEpisodeImages=true")]
    public async Task EpisodeImagesAndUnmonitoredReachSonarr(bool unmonitored, string flags)
    {
        var handler = new ArrHandler();
        handler.Answer("sonarr.lan", HttpStatusCode.OK, "[]");
        var query = ArrCalendarWindow.Query(true, new DateTime(2026, 11, 5), new DateTime(2027, 2, 3), unmonitored);

        await Fetch(handler, new[] { "http://sonarr.lan:8989" }, query: query);

        Assert.Equal(new[] { "http://sonarr.lan:8989/api/v3/calendar?start=2026-11-05&end=2027-02-03" + flags }, handler.Requested);
    }

    [Fact]
    public async Task UnmonitoredReachesRadarr()
    {
        var handler = new ArrHandler();
        handler.Answer("radarr.lan", HttpStatusCode.OK, "[]");

        await Fetch(handler, new[] { "http://radarr.lan:7878" }, query: ArrCalendarWindow.Query(false, new DateTime(2026, 11, 5), new DateTime(2026, 11, 6), unmonitored: true));

        Assert.Equal(new[] { "http://radarr.lan:7878/api/v3/calendar?start=2026-11-05&end=2026-11-06&unmonitored=true" }, handler.Requested);
    }

    [Fact]
    public async Task ClearSessionAsync_ThrowsWhenSessionDirectoryIsInaccessible()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var blockedSessionsDir = Path.Combine(tempFile, "seerr-sessions");
            var service = new SeerrSessionService(NoOpLogger.Create(), blockedSessionsDir);
            await Assert.ThrowsAnyAsync<IOException>(() => service.ClearSessionAsync(Guid.NewGuid()));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // Unless a test passes one, a lookup fails the test, since https and the internal address never need one.
    private static Task<SeerrProxyResponse> Fetch(ArrHandler handler, string[] bases, Func<string, Task<IPAddress[]>>? resolveHost = null, string query = Query) =>
        SeerrSessionService.FetchArrCalendarAsync(
            () => new HttpClient(handler, disposeHandler: false),
            NoOpLogger.Create(),
            "radarr",
            bases,
            "secret",
            query,
            resolveHost ?? (host => throw new InvalidOperationException("Unexpected lookup of " + host)));

    private static Func<string, Task<IPAddress[]>> Resolves(string address) => _ =>
        Task.FromResult(new[] { IPAddress.Parse(address) });

    /// <summary>Answers for the hosts it's told about and can't be reached on any other.</summary>
    private sealed class ArrHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requested { get; } = new();

        public List<string> ApiKeys { get; } = new();

        public void Answer(string host, HttpStatusCode status, string body) => _answers[host] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            ApiKeys.Add(request.Headers.GetValues("X-Api-Key").Single());
            if (!_answers.TryGetValue(request.RequestUri.Host, out var answer))
            {
                throw new HttpRequestException("Name or service not known");
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An Emby logger that drops everything.</summary>
    private class NoOpLogger : DispatchProxy
    {
        public static ILogger Create() => Create<ILogger, NoOpLogger>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
