using Jellyfin.Data.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Moonfin.Server.Services;
using Xunit;
namespace Moonfin.Server.Tests;
public class LiveTvChannelGroupsServiceTests : IDisposable
{
    private readonly DirectoryInfo _cache = Directory.CreateTempSubdirectory();
    private readonly Guid _viewer = Guid.NewGuid();
    private readonly LiveTvChannel _m3u = Channel("m3u_news"), _native = Channel("xtream_42");
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<List<ChannelInfo>> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime _start = DateTime.UtcNow.AddHours(-1);
    private readonly TaskResult _import = new() { Status = TaskCompletionStatus.Completed };
    private int _calls;
    private bool _tunerReady;

    public void Dispose() { _pending.TrySetResult([]); _cache.Delete(true); }
    private static LiveTvChannel Channel(string id) => new() { Id = Guid.NewGuid(), ExternalId = id, ServiceName = "Emby" };
    private static List<ChannelInfo> Stations(string group) => [new() { Id = "xtream_42", TunerHostId = "native", ChannelGroup = group }];
    private void WriteCache(string group, int minute) {
        var path = Path.Combine(_cache.FullName, "first_channels");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = _m3u.ExternalId, ChannelGroup = group } }));
        File.SetLastWriteTimeUtc(path, _start.AddMinutes(minute));
    }

    private LiveTvChannelGroupsService Service(Func<Guid, BaseItem[]> channels, bool hasImport = true)
    {
        _import.StartTimeUtc = _start;
        _import.EndTimeUtc = _start.AddMinutes(5);
        WriteCache("News", 1);
        var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
        var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((m, _) => m.Name switch {
            "get_ScheduledTask" => task, "get_State" => TaskState.Idle, "get_LastExecutionResult" => _import,
            _ => throw new NotSupportedException(m.Name)
        });
        var tuner = RecordingHostProxy.Create<ITunerHost>((m, _) => {
            if (m.Name == "get_Type") return "xtream-library";
            Assert.Equal("GetChannels", m.Name);
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            return _tunerReady ? Task.FromResult(Stations("New")) : _pending.Task;
        });
        return new(
            RecordingHostProxy.Create<ILiveTvManager>((_, args) => new QueryResult<BaseItem> { Items = channels(((LiveTvChannelQuery)args![0]!).UserId) }),
            RecordingHostProxy.Create<ITunerHostManager>((_, _) => new[] { tuner }),
            RecordingHostProxy.Create<IServerConfigurationManager>((m, _) => m.Name switch {
                "get_ApplicationPaths" => RecordingHostProxy.Create<IServerApplicationPaths>((_, _) => _cache.FullName),
                "GetConfiguration" => new LiveTvOptions { TunerHosts = [new() { Id = "first", Type = "m3u" }, new() { Id = "native", Type = "xtream-library" }] },
                _ => throw new NotSupportedException(m.Name)
            }),
            RecordingHostProxy.Create<ILibraryManager>((_, _) => new QueryResult<BaseItem>()),
            RecordingHostProxy.Create<IUserManager>((_, _) => new User("viewer", "auth", "reset")),
            RecordingHostProxy.Create<ITaskManager>((_, _) => hasImport ? new[] { worker } : Array.Empty<IScheduledTaskWorker>()),
            new NoOpLogger<LiveTvChannelGroupsService>(), RecordingHostProxy.Create<IHostApplicationLifetime>((_, _) => CancellationToken.None),
            RecordingHostProxy.Create<IDtoService>((_, args) => ((IReadOnlyList<BaseItem>)args![0]!)
                .Select(item => new BaseItemDto { Id = item.Id, Name = item.ExternalId }).ToArray()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(TaskCompletionStatus.Completed, null)]
    [InlineData(TaskCompletionStatus.Completed, true)]
    [InlineData(TaskCompletionStatus.Completed, false)]
    [InlineData(TaskCompletionStatus.Failed, true)]
    [InlineData(TaskCompletionStatus.Failed, false)]
    [InlineData(TaskCompletionStatus.Cancelled, true)]
    [InlineData(TaskCompletionStatus.Cancelled, false)]
    [InlineData(TaskCompletionStatus.Aborted, true)]
    [InlineData(TaskCompletionStatus.Aborted, false)]
    public async Task ConcurrentViewersSeeOnlyTheirOwnGuideImport(TaskCompletionStatus? next, bool? cacheUpdated)
    {
        var service = Service(_ => cacheUpdated.HasValue ? [_m3u, _native] : [_native]);
        var first = service.GetAsync(_viewer, default);
        await _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, _calls);
        if (next.HasValue) {
            if (cacheUpdated.HasValue) _import.StartTimeUtc = _import.EndTimeUtc;
            _import.EndTimeUtc = _start.AddMinutes(10);
            _import.Status = next.Value;
            if (cacheUpdated == true) WriteCache("Sports", 6);
        }
        _tunerReady = true;
        var second = service.GetAsync(cacheUpdated.HasValue ? _viewer : Guid.NewGuid(), default);
        _pending.SetResult(Stations("Old"));
        var before = await first;
        var after = await second;
        var native = _native.Id.ToString("N");
        var m3u = _m3u.Id.ToString("N");
        Assert.Equal(next.HasValue ? 2 : 1, _calls);
        Assert.Equal(next.HasValue ? "New" : "Old", Assert.Single(after.Groups[native]));
        if (next.HasValue) {
            var ids = cacheUpdated.HasValue ? new[] { m3u, native } : [native];
            Assert.True(before.Partial);
            Assert.Empty(before.Groups);
            Assert.Equal(ids.Length, before.Channels.Length);
            Assert.Equal(ids.Order(), before.UnresolvedChannelIds!.Order());
        }
        if (cacheUpdated is bool updated) {
            Assert.Equal(!updated, after.Partial);
            if (!updated) {
                Assert.Equal(m3u, Assert.Single(after.UnresolvedChannelIds!));
                Assert.DoesNotContain(m3u, after.Groups.Keys);
                WriteCache("Sports", 7);
                after = await service.GetAsync(_viewer, default);
            }
            Assert.False(after.Partial);
            Assert.Equal("Sports", Assert.Single(after.Groups[m3u]));
        }
    }

    [Fact]
    public async Task StalledTunerLeavesOtherViewersFreeToBrowseAndCanBeRetried()
    {
        var service = Service(id => id == _viewer ? [_m3u, _native] : [_m3u], hasImport: false);
        var limit = TimeSpan.FromSeconds(10);
        var first = service.GetAsync(_viewer, default);
        await _started.Task.WaitAsync(limit);
        var second = service.GetAsync(_viewer, default);
        var m3u = _m3u.Id.ToString("N");
        var native = _native.Id.ToString("N");
        Assert.Equal("News", Assert.Single((await service.GetAsync(Guid.NewGuid(), default).WaitAsync(limit)).Groups[m3u]));
        foreach (var result in await Task.WhenAll(first, second).WaitAsync(limit)) {
            Assert.True(result.Partial);
            Assert.Equal("News", Assert.Single(result.Groups[m3u]));
            Assert.Equal(native, Assert.Single(result.UnresolvedChannelIds!));
        }
        var attempts = _calls;
        Assert.InRange(attempts, 1, 2);
        _tunerReady = true;
        var recovered = await service.GetAsync(_viewer, default).WaitAsync(limit);
        Assert.True(_calls > attempts);
        Assert.False(recovered.Partial);
        Assert.Equal("New", Assert.Single(recovered.Groups[native]));
    }
}
