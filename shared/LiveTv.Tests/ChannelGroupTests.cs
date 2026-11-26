using MediaBrowser.Model.Dto;
using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Tests;

public class ChannelGroupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnresolvedIdsFollowTheViewersFinalChannelList(bool hasVisibleChannels)
    {
        var visible = new BaseItemDto { Id = Guid.NewGuid() };
        var resolved = new BaseItemDto { Id = Guid.NewGuid() };
        var hiddenId = Guid.NewGuid().ToString("N");
        var visibleId = visible.Id.ToString("N");
        var result = new ChannelGroupResult { Partial = true, UnresolvedChannelIds = [hiddenId, visibleId] };

        result.SetChannels(hasVisibleChannels ? [visible, resolved] : []);

        Assert.Equal(hasVisibleChannels ? [visibleId] : Array.Empty<string>(), result.UnresolvedChannelIds);
    }

    [Fact]
    public void ConflictingTunersOnlyRecoverWhenTheExactStationReturns()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var channels = new[] { ("c1", "same"), ("c2", "other"), ("c3", "untagged"), ("c4", "offline") };
        var ids = channels.Select(c => c.Item2).ToArray();
        ChannelGroupResult Read(ChannelGroupSnapshot snapshot) {
            snapshot.UpdateConflicts(known);
            var result = new ChannelGroupResult();
            snapshot.AddVisibleChannelsTo(result, channels);
            return result;
        }
        var mixed = new ChannelGroupSnapshot(ids);
        foreach (var host in new[] { "A", "B", "A" }) mixed.Add(1, host, "same", ["Sports"]);
        mixed.Add(2, "B", "other", ["Kids"]);
        mixed.Add(2, "B", "untagged", null);
        var first = Read(mixed);
        Assert.True(first.Partial);
        Assert.Equal("c2", Assert.Single(first.Groups).Key);
        Assert.Equal(["c4"], first.UnresolvedChannelIds);
        foreach (var returned in new string?[] { null, "SAME", "same" }) {
            var retry = new ChannelGroupSnapshot(ids);
            retry.Add(1, "B", returned, ["News"]);
            var result = Read(retry);
            Assert.DoesNotContain("c1", result.UnresolvedChannelIds!);
            if (returned == "same") Assert.Equal("News", Assert.Single(result.Groups["c1"]));
            else Assert.DoesNotContain("c1", result.Groups.Keys);
        }
        Assert.Empty(known);
    }
}
