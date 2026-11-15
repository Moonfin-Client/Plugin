using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.LiveTv;
using Moonfin.LiveTv.Tests;
using Moonfin.Server.Api;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// With the recording owners setting off, every owners endpoint answers 404 with the same JSON
/// error body Emby sends, so a client can tell "feature off" from "no such program".
/// </summary>
public sealed class RecordingOwnersControllerTests
{
    private static RecordingOwnersController DisabledController()
    {
        var host = new FakeRecordingHost();
        var service = new RecordingOwnershipService(
            host,
            NoRecordingLog.Instance,
            PlainStateFile.Instance,
            () => DateTime.UtcNow,
            () => throw new InvalidOperationException("A disabled endpoint must not touch the store"),
            () => false);
        return new RecordingOwnersController(service, host) { IsEnabled = () => false };
    }

    private static void AssertOwnersNotAvailable(IActionResult? result)
    {
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
        Assert.Equal("{\"error\":\"Recording owners are not available\"}", JsonSerializer.Serialize(notFound.Value));
    }

    [Fact]
    public async Task Disabled_EveryEndpointReturns404WithErrorBody()
    {
        var controller = DisabledController();

        AssertOwnersNotAvailable((await controller.Record(new RecordOwnerBody { ProgramId = "p1" }, CancellationToken.None)).Result);
        AssertOwnersNotAvailable((await controller.RecordSeries(new RecordSeriesOwnerBody { ProgramId = "p1" }, CancellationToken.None)).Result);
        AssertOwnersNotAvailable((await controller.Claim(new ClaimBody { Kind = "timer", Id = "t1" }, CancellationToken.None)).Result);
        AssertOwnersNotAvailable((await controller.Lookup(new LookupBody(), CancellationToken.None)).Result);
        AssertOwnersNotAvailable((await controller.Mine(CancellationToken.None)).Result);
    }

    [Fact]
    public void OwnersNotAvailable_MatchesEmbysBody()
    {
        AssertOwnersNotAvailable(RecordingOwnersController.OwnersNotAvailable());
    }

    private static readonly Guid Caller = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // An enabled controller called by Caller, its store in a fresh temp folder.
    private static RecordingOwnersController EnabledController(FakeRecordingHost host)
    {
        host.Users.Add(new ServerUser(Caller, "Alex", null, false));
        var storePath = Path.Combine(Path.GetTempPath(), "moonfin-owners-api-" + Guid.NewGuid().ToString("N"), RecordingOwnershipService.StoreFileName);
        var service = new RecordingOwnershipService(host, NoRecordingLog.Instance, PlainStateFile.Instance, () => DateTime.UtcNow, storePath, () => true);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", Caller.ToString()) }, "test"));
        return new RecordingOwnersController(service, host)
        {
            IsEnabled = () => true,
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };
    }

    private static GuideProgram AddProgram(FakeRecordingHost host, string id)
    {
        var start = DateTime.UtcNow.AddHours(1);
        var program = new GuideProgram { Id = id, ItemId = Guid.NewGuid(), SeriesId = "SH001", SeriesName = "Bonanza", StartDate = start, EndDate = start.AddMinutes(30) };
        host.Programs.Add(program);
        return program;
    }

    [Fact]
    public async Task Record_ServerRefuses_409CarriesTheServersReason()
    {
        var host = new FakeRecordingHost { SchedulingError = "Emby Premiere required to record" };
        AddProgram(host, "p1");
        var controller = EnabledController(host);

        var conflict = Assert.IsType<ConflictObjectResult>((await controller.Record(new RecordOwnerBody { ProgramId = "p1" }, CancellationToken.None)).Result);

        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal("{\"error\":\"Emby Premiere required to record\"}", JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task RecordSeries_ServerRefuses_409CarriesTheServersReason()
    {
        var host = new FakeRecordingHost { SeriesCreationError = "Emby Premiere required to record" };
        AddProgram(host, "p1");
        var controller = EnabledController(host);

        var conflict = Assert.IsType<ConflictObjectResult>((await controller.RecordSeries(new RecordSeriesOwnerBody { ProgramId = "p1" }, CancellationToken.None)).Result);

        Assert.Equal("{\"error\":\"Emby Premiere required to record\"}", JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task Record_FailsWithoutReason_409CarriesTheGenericText()
    {
        var host = new FakeRecordingHost { SchedulingFails = true };
        AddProgram(host, "p1");
        var controller = EnabledController(host);

        var conflict = Assert.IsType<ConflictObjectResult>((await controller.Record(new RecordOwnerBody { ProgramId = "p1" }, CancellationToken.None)).Result);

        Assert.Equal("{\"error\":\"The server could not schedule the recording.\"}", JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task Lookup_ListsUnownedRecordingsInNFormat()
    {
        var host = new FakeRecordingHost();
        var recording = host.AddItem("/rec/a.ts");
        var movie = host.AddItem("/movies/film.mkv");
        host.NotRecordings.Add(movie.Id);
        var controller = EnabledController(host);

        var ok = Assert.IsType<OkObjectResult>((await controller.Lookup(new LookupBody { ItemIds = new List<string> { recording.Id.ToString("D"), movie.Id.ToString("N") } }, CancellationToken.None)).Result);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        Assert.Equal(new[] { recording.Id.ToString("N") }, json.GetProperty("recordingItemIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(json.GetProperty("items").EnumerateObject());
    }

    [Fact]
    public async Task Lookup_HostCantTell_LeavesRecordingItemIdsOut()
    {
        var host = new FakeRecordingHost { RecordingKindUnknown = true };
        var recording = host.AddItem("/rec/a.ts");
        var controller = EnabledController(host);

        var ok = Assert.IsType<OkObjectResult>((await controller.Lookup(new LookupBody { ItemIds = new List<string> { recording.Id.ToString("N") } }, CancellationToken.None)).Result);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        Assert.False(json.TryGetProperty("recordingItemIds", out _));
    }
}
