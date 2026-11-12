
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

public class LiveTvChannelGroupsServiceTests
{
    [Fact]
    public async Task StalledXtreamLookupReturnsM3uGroupsAndDoesNotBlockOtherViewers()
    {
        var cache = Directory.CreateTempSubdirectory();
        var pending = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            File.WriteAllText(Path.Combine(cache.FullName, "first_channels"), """[{"Id":"m3u_news","ChannelGroup":"News"}]""");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var xtream = RecordingHostProxy.Create<ITunerHost>((method, _) =>
            {
                if (method.Name == "get_Type") return "xtream-library";
                Assert.Equal("GetChannels", method.Name);
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return pending.Task;
            });
            var m3u = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "m3u_news", ServiceName = "Emby" };
            var native = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "xtream_42", ServiceName = "Emby" };
            var mixedUser = Guid.NewGuid();
            var service = CreateGroupService(cache.FullName, [xtream], id => id == mixedUser ? [m3u, native] : [m3u]);

            var mixed = service.GetAsync(mixedUser, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var anotherMixed = service.GetAsync(mixedUser, CancellationToken.None);
            var local = await service.GetAsync(Guid.NewGuid(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEmpty(local.Channels);
            Assert.Equal("News", Assert.Single(local.Groups[m3u.Id.ToString("N")]));
            foreach (var partial in await Task.WhenAll(mixed, anotherMixed).WaitAsync(TimeSpan.FromSeconds(10)))
            {
                Assert.True(partial.Partial);
                Assert.Equal("News", Assert.Single(partial.Groups[m3u.Id.ToString("N")]));
                Assert.Equal(native.Id.ToString("N"), Assert.Single(partial.UnresolvedChannelIds!));
            }
            Assert.Equal(1, calls);
            pending.SetResult([new ChannelInfo { Id = native.ExternalId, TunerHostId = "native", ChannelGroup = "Radio" }]);
            var recovered = await service.GetAsync(mixedUser, CancellationToken.None);
            Assert.False(recovered.Partial);
            Assert.Equal("Radio", Assert.Single(recovered.Groups[native.Id.ToString("N")]));
        }
        finally
        {
            pending.TrySetResult([]);
            cache.Delete(true);
        }
    }
    private static LiveTvChannelGroupsService CreateGroupService(string cache, ITunerHost[] tuners, Func<Guid, BaseItem[]> channels)
    {
        var paths = RecordingHostProxy.Create<IServerApplicationPaths>((_, _) => cache);
        var options = new LiveTvOptions { TunerHosts = [
            new() { Id = "first", Type = "m3u" }, new() { Id = "second", Type = "m3u" },
            new() { Id = "native", Type = "xtream-library" }] };
        var config = RecordingHostProxy.Create<IServerConfigurationManager>((method, _) => method.Name switch
        {
            "get_ApplicationPaths" => paths,
            "GetConfiguration" => options,
            _ => throw new NotSupportedException(method.Name)
        });
        var liveTv = RecordingHostProxy.Create<ILiveTvManager>((_, args) =>
            new QueryResult<BaseItem> { Items = channels(((LiveTvChannelQuery)args![0]!).UserId) });
        var dtos = RecordingHostProxy.Create<IDtoService>((_, args) =>
        {
            var requested = (DtoOptions)args![1]!;
            Assert.True(requested.EnableImages && requested.EnableUserData);
            Assert.False(requested.AddCurrentProgram);
            Assert.IsType<User>(args[2]);
            return ((IReadOnlyList<BaseItem>)args[0]!).Select(item => new BaseItemDto { Id = item.Id, Name = item.ExternalId }).ToArray();
        });
        return new LiveTvChannelGroupsService(liveTv, RecordingHostProxy.Create<ITunerHostManager>((_, _) => tuners), config,
            RecordingHostProxy.Create<ILibraryManager>((_, _) => new QueryResult<BaseItem>()),
            RecordingHostProxy.Create<IUserManager>((_, _) => new User("viewer", "auth", "reset")),
            RecordingHostProxy.Create<ITaskManager>((_, _) => Array.Empty<IScheduledTaskWorker>()),
            new NoOpLogger<LiveTvChannelGroupsService>(),
            RecordingHostProxy.Create<IHostApplicationLifetime>((_, _) => CancellationToken.None), dtos);
    }
}
