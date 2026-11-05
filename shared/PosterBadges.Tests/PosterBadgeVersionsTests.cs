using System.Collections.Generic;
using System.Linq;
using Moonfin.PosterBadges;
using Xunit;

namespace Moonfin.PosterBadges.Tests;

/// <summary>A movie stored twice gets the labels of its best copy.</summary>
public class PosterBadgeVersionsTests
{
    private enum TrackType { Audio, Video }

    private sealed class Track
    {
        public TrackType Type { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    private sealed class Source
    {
        public string? Path { get; set; }
        public List<Track> MediaStreams { get; set; } = new();
    }

    private static Source Copy(string path, int width, int height) => new Source
    {
        Path = path,
        MediaStreams = new List<Track> { new Track { Type = TrackType.Video, Width = width, Height = height }, new Track { Type = TrackType.Audio } },
    };

    [Fact]
    public void TheLargestPictureWinsWhereverItIsListed()
    {
        var best = PosterBadgeVersions.Pick(new[] { Copy("/films/Dune.1080p.mkv", 1920, 800), Copy("/films/Dune.2160p.mkv", 3840, 1600) });
        Assert.Equal("/films/Dune.2160p.mkv", best!.Value.Path);
    }

    [Fact]
    public void ATieKeepsTheMainVersion()
    {
        var best = PosterBadgeVersions.Pick(new[] { Copy("/films/Dune.mkv", 1920, 1080), Copy("/films/Dune.extended.mkv", 1920, 1080) });
        Assert.Equal("/films/Dune.mkv", best!.Value.Path);
    }

    [Fact]
    public void NoSourcesLeavesTheCallerItsOwnFile()
    {
        Assert.Null(PosterBadgeVersions.Pick(null));
        Assert.Null(PosterBadgeVersions.Pick(Enumerable.Empty<Source>()));
    }
}
