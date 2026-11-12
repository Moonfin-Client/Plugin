using System;




using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Tests
{
    public class ChannelGroupTests
    {
        [Fact]
        public void AmbiguousExternalIdRemainsUngroupedEvenWhenSeenAgain()
        {
            var snapshot = new ChannelGroupSnapshot(new[] { "same", "other", "untagged" });
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(1, "B", "same", new[] { "News" });
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(2, "B", "other", new[] { "Kids" });
            snapshot.Add(2, "B", "untagged", null);
            var visible = snapshot.ForVisibleChannels(new[] { ("c1", "same"), ("c2", "other"), ("c3", "untagged") });
            Assert.True(visible.Partial);
            Assert.Equal("c2", Assert.Single(visible.Groups).Key);
            Assert.Empty(visible.UnresolvedChannelIds!);

            var mixed = snapshot.ForVisibleChannels(new[] { ("c1", "same"), ("c4", "offline") });
            Assert.Equal(new[] { "c4" }, mixed.UnresolvedChannelIds);
            Assert.Empty(mixed.Groups);
        }
    }
}
