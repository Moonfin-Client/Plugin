using System.Reflection;

using System.Text.Json.Nodes;
using Emby.Plugins.Moonfin.Api;
using MediaBrowser.Controller.LiveTv;

using MediaBrowser.Model.LiveTv;

using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public class EmbyTunerGroupReaderTests
{
    [Theory]
    [InlineData("HOUSEHOLD", false)]
    [InlineData("Sports", true)]
    public async Task RereadsPlaylistGroupsWithoutChangingTheImportedLineup(string required, bool keepRequired)
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        var host = new TunerHostInfo
        {
            Id = "test", DataVersion = 2, Url = "fixture.m3u", UserAgent = "fixture",
            ProviderOptions = $$"""{"additionalTags":["Household"],"RequiredTags":["{{required}}"],"ImportGuideData":true}"""
        };
        var original = host.ProviderOptions;
        probe.Imported.AddRange(new[] { Channel("1", "Sports", "Household"), Channel("2", "Household"), Channel("3", "Household") });
        probe.Playlist.AddRange(new[] { Channel("1", "Sports"), Channel("2", "Household"), Channel("3"), Channel("new", "News") });
        var channels = await EmbyTunerGroupReader.GetChannelsAsync(
            tuner, host, CancellationToken.None, CancellationToken.None, _ => true, _ => { });

        Assert.Equal(new[] { "1", "2", "3" }, channels.Select(c => c.Id));
        Assert.Equal(new[] { "Sports" }, channels[0].Tags);
        Assert.Equal(new[] { "Household" }, channels[1].Tags);
        Assert.Empty(channels[2].Tags);
        Assert.NotSame(host, probe.ReadHost);
        Assert.Equal((host.Id, host.Url, host.UserAgent, host.DataVersion),
            (probe.ReadHost!.Id, probe.ReadHost.Url, probe.ReadHost.UserAgent, probe.ReadHost.DataVersion));
        var options = JsonNode.Parse(probe.ReadHost.ProviderOptions)!;
        Assert.Null(options["additionalTags"]);
        Assert.Equal(keepRequired ? required : null, options["RequiredTags"]?[0]?.GetValue<string>());
        Assert.True(options["ImportGuideData"]!.GetValue<bool>());
        Assert.Equal(original, host.ProviderOptions);
        Assert.Equal(new[] { "Sports", "Household" }, probe.Imported[0].Tags);
    }
    private static ChannelInfo Channel(string id, params string[] tags) => new() { Id = id, Tags = tags };

    public class Tuner : DispatchProxy
    {
        public TaskCompletionSource<List<ChannelInfo>>? Pending { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }
        public bool Offline { get; set; }
        public string Type { get; set; } = "m3u";
        public List<ChannelInfo> Imported { get; } = new();
        public Func<TunerHostInfo, Task<List<ChannelInfo>>>? ReadImported { get; set; }
        public List<ChannelInfo> Playlist { get; } = new();
        public TunerHostInfo? ReadHost { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Type" => Type,
            nameof(ITunerHost.GetChannels) => ReadImported?.Invoke((TunerHostInfo)args![0]!) ?? Task.FromResult(Imported),
            _ => throw new NotSupportedException(method?.Name)
        };

        protected Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new FileNotFoundException();
            ReadHost = host;
            ReadCount++;
            Started.TrySetResult(true);
            return Pending?.Task ?? Task.FromResult(Playlist);
        }
    }
}
