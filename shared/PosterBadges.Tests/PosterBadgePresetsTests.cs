using System.Collections.Generic;
using System.Linq;
using Moonfin.PosterBadges;
using Xunit;

namespace Moonfin.PosterBadges.Tests;

/// <summary>
/// The built-in labels, the viewer filter and the look the server fills in, checked against
/// how real files describe their tracks.
/// </summary>
public class PosterBadgePresetsTests
{
    private enum TrackType { Audio, Video, Subtitle }

    private sealed class Track
    {
        public TrackType Type { get; set; }
        public string? Codec { get; set; }
        public string? Profile { get; set; }
        public string? Title { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Channels { get; set; }
        public string? VideoRangeType { get; set; }
        public string? ExtendedVideoType { get; set; }
    }

    private static Track Video(int width, int height, string codec = "hevc", string? range = null) =>
        new Track { Type = TrackType.Video, Width = width, Height = height, Codec = codec, VideoRangeType = range };

    private static Track Audio(string codec, int channels, string? profile = null, string? title = null) =>
        new Track { Type = TrackType.Audio, Codec = codec, Channels = channels, Profile = profile, Title = title };

    private static HashSet<string> Detect(string path, params Track[] tracks) =>
        PosterBadgePresets.Detect(path, tracks.Cast<object>().ToList());

    [Fact]
    public void AScopeFilmIsStill4KAndNever1080p()
    {
        var found = Detect("/films/a.mkv", Video(3840, 1600));
        Assert.Contains("4k", found);
        Assert.DoesNotContain("1080p", found);
    }

    [Fact]
    public void ResolutionsFollowTheDetailsPage()
    {
        Assert.Contains("1440p", Detect("/films/a.mkv", Video(2560, 1440)));
        Assert.Contains("480p", Detect("/films/a.mkv", Video(720, 480)));
        Assert.Contains("sd", Detect("/films/a.mkv", Video(480, 360)));

        // Narrower than the details page's 4K line, so not 4K anywhere.
        var almost = Detect("/films/a.mkv", Video(3600, 1500));
        Assert.Contains("1440p", almost);
        Assert.DoesNotContain("4k", almost);
    }

    [Fact]
    public void AnAnamorphic1080FileIs1080p()
    {
        Assert.Contains("1080p", Detect("/films/a.mkv", Video(1440, 1080)));
    }

    [Fact]
    public void DolbyVisionWithAnHdr10FallbackOnlySaysDv()
    {
        var found = Detect("/films/a.mkv", Video(3840, 2160, range: "DOVIWithHDR10"));
        Assert.Contains("dolbyVision", found);
        Assert.DoesNotContain("hdr10", found);
    }

    [Fact]
    public void EmbyHdr10PlusIsReadFromItsOwnProperty()
    {
        var video = new Track { Type = TrackType.Video, Width = 3840, Height = 2160, Codec = "hevc", ExtendedVideoType = "Hdr10Plus" };
        Assert.Contains("hdr10plus", Detect("/films/a.mkv", video));
    }

    [Fact]
    public void TheBestAudioTrackWinsAndAtmosHidesTrueHd()
    {
        var found = Detect(
            "/films/a.mkv",
            Video(1920, 1080),
            Audio("ac3", 6),
            Audio("truehd", 8, profile: "Dolby TrueHD + Dolby Atmos"));

        Assert.Contains("atmos", found);
        Assert.DoesNotContain("truehd", found);
        Assert.DoesNotContain("dd", found);
        Assert.Contains("7.1", found);
        Assert.DoesNotContain("5.1", found);
    }

    [Fact]
    public void DtsXBeatsMasterAudio()
    {
        Assert.Contains("dtsx", Detect("/films/a.mkv", Audio("dts", 8, profile: "DTS-HD MA + DTS:X")));
        Assert.Contains("dtshdma", Detect("/films/a.mkv", Audio("dts", 6, profile: "DTS-HD MA")));
    }

    [Fact]
    public void NonImaxIsNotImax()
    {
        Assert.Contains("imax", Detect("/films/Dune.2021.IMAX.2160p.REMUX.mkv", Video(3840, 2160)));
        Assert.DoesNotContain("imax", Detect("/films/Dune.2021.NON-IMAX.2160p.mkv", Video(3840, 2160)));
    }

    [Fact]
    public void APresetRuleIgnoresConditionsAndUsesItsDetection()
    {
        var rules = new List<PosterBadgeRule> { new PosterBadgeRule { Label = "UHD", Preset = "4k" } };
        var badges = PosterBadgeMatcher.Match(rules, "/films/a.mkv", new object[] { Video(3840, 2160) });
        Assert.Equal(new[] { "UHD" }, badges.Select(b => b.Label));
    }

    [Fact]
    public void EmptySettingsComeFromTheDefaultsAndBadOnesAreDropped()
    {
        var rules = new List<PosterBadgeRule>
        {
            new PosterBadgeRule { Label = "4K", Preset = "4k", Color = "nope", Corner = "middle", Opacity = 500, Size = "large" },
        };
        var defaults = new PosterBadgeDefaults { Style = "glass", Corner = "bottomRight", Opacity = 80 };

        var badge = PosterBadgeMatcher.Match(rules, null, new object[] { Video(3840, 2160) }, defaults).Single();

        Assert.Equal("#2563EB", badge.Color);
        Assert.Equal("glass", badge.Style);
        Assert.Equal("bottomRight", badge.Corner);
        Assert.Equal(100, badge.Opacity);
        Assert.Equal("large", badge.Size);
        Assert.Equal("rounded", badge.Shape);
        Assert.Equal(string.Empty, badge.TextColor);
    }

    [Fact]
    public void ARuleLimitedToUsersOrDevicesOnlyReachesThem()
    {
        var rules = new List<PosterBadgeRule>
        {
            new PosterBadgeRule { Label = "All", Preset = "4k" },
            new PosterBadgeRule { Label = "Axel", Preset = "4k", UserIds = new List<string> { "6c7a1e0f9b1d4b4f8d1e2a3b4c5d6e7f" } },
            new PosterBadgeRule { Label = "TV", Preset = "4k", Profiles = new List<string> { "tv" } },
            new PosterBadgeRule { Label = "Off", Preset = "4k", Enabled = false },
        };

        // The same user, dashed this time.
        var forAxelOnTv = PosterBadgeMatcher.ForViewer(rules, "6c7a1e0f-9b1d-4b4f-8d1e-2a3b4c5d6e7f", "TV");
        Assert.Equal(new[] { "All", "Axel", "TV" }, forAxelOnTv.Select(r => r.Label));

        var forSomeoneOnAnOldClient = PosterBadgeMatcher.ForViewer(rules, "00000000-0000-0000-0000-000000000001", null);
        Assert.Equal(new[] { "All" }, forSomeoneOnAnOldClient.Select(r => r.Label));
    }
}
