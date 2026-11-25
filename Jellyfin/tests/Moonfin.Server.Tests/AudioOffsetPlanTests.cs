using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The remux copies video from a keyframe and reads the audio from a shifted point, so the cut
/// point and the audio's start decide whether the client's clock and the shift come out right.
/// </summary>
public sealed class AudioOffsetPlanTests
{
    [Fact]
    public void ChooseKeyframe_TakesTheLastOneAtOrBeforeTheTarget()
    {
        Assert.Equal(40.0, AudioOffsetPlan.ChooseKeyframe([35.0, 40.0, 45.0], 42.5));
        Assert.Equal(40.0, AudioOffsetPlan.ChooseKeyframe([45.0, 40.0, 35.0], 40.0));
    }

    [Fact]
    public void ChooseKeyframe_FallsBackToTheEarliestWhenNoneComesBefore()
    {
        Assert.Equal(12.0, AudioOffsetPlan.ChooseKeyframe([15.0, 12.0], 10.0));
        Assert.Equal(0.0, AudioOffsetPlan.ChooseKeyframe([], 10.0));
    }

    [Fact]
    public void AudioStart_ReadsLaterAudioFromBeforeTheCut()
    {
        // Audio 300 ms late: the sound under the picture at 100 s is from 99.7 s
        var (seek, pad) = AudioOffsetPlan.AudioStart(100, 300);
        Assert.Equal(99.7, seek, 3);
        Assert.Equal(0, pad);
    }

    [Fact]
    public void AudioStart_ReadsEarlierAudioFromAfterTheCut()
    {
        var (seek, pad) = AudioOffsetPlan.AudioStart(100, -300);
        Assert.Equal(100.3, seek, 3);
        Assert.Equal(0, pad);
    }

    [Fact]
    public void AudioStart_PadsWithSilenceWhenThereIsNoneThatEarly()
    {
        var (seek, pad) = AudioOffsetPlan.AudioStart(0.2, 1000);
        Assert.Equal(0, seek);
        Assert.Equal(0.8, pad, 3);
    }

    [Fact]
    public void ClampOffset_KeepsShiftsWithinTenSeconds()
    {
        Assert.Equal(10_000, AudioOffsetPlan.ClampOffset(60_000));
        Assert.Equal(-10_000, AudioOffsetPlan.ClampOffset(-60_000));
        Assert.Equal(-250, AudioOffsetPlan.ClampOffset(-250));
    }

    [Fact]
    public void AudioEncoding_KeepsSurroundUpToFivePointOne()
    {
        Assert.Equal((6, "640k"), AudioOffsetPlan.AudioEncoding(8));
        Assert.Equal((2, "192k"), AudioOffsetPlan.AudioEncoding(2));
        Assert.Equal((2, "192k"), AudioOffsetPlan.AudioEncoding(null));
    }

    [Fact]
    public void ParseKeyframes_KeepsKeyframesAndTakesOffTheFileStart()
    {
        var output = "1.400000\n1.466667,K__\n1.508333,___\n6.466667,K_\n";
        var keyframes = AudioOffsetPlan.ParseKeyframes(output);
        Assert.Equal(2, keyframes.Count);
        Assert.Equal(0.066667, keyframes[0], 5);
        Assert.Equal(5.066667, keyframes[1], 5);
    }

    [Fact]
    public void VideoArguments_CopiesThePictureFromTheKeyframeAndWritesTheMaster()
    {
        var args = AudioOffsetPlan.VideoArguments("/m/a.mkv", 0, 2, "eac3", 6, 100, "/t/job");
        Assert.Single(args, a => a == "-i");
        Assert.Equal("100", args[args.IndexOf("-i") - 1]);
        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("master.m3u8", args[args.IndexOf("-master_pl_name") + 1]);
        Assert.Equal("v:0,agroup:aud a:0,agroup:aud,default:yes", args[args.IndexOf("-var_stream_map") + 1]);
    }

    [Fact]
    public void AudioArguments_ReadTheSoundFromTheShiftedPoint()
    {
        var args = AudioOffsetPlan.AudioArguments("/m/a.mkv", 2, "eac3", 6, 100, -300, "/t/job");
        Assert.Single(args, a => a == "-i");
        // Opened ten seconds early and cut exactly at 100.3 after
        Assert.Equal("90.3", args[args.IndexOf("-i") - 1]);
        Assert.Equal("10", args[args.IndexOf("-i") + 3]);
        Assert.Contains("0:2", args);
        // DD+ is copied, so Atmos in it survives
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.EndsWith("audio.m3u8", args[^1]);
    }

    [Fact]
    public void AudioArguments_EncodeWhatRokuCantPlayAndPaddedStarts()
    {
        var truehd = AudioOffsetPlan.AudioArguments("/m/a.mkv", 1, "truehd", 8, 100, 0, "/t/job");
        Assert.Equal("ac3", truehd[truehd.IndexOf("-c:a") + 1]);
        Assert.Equal("6", truehd[truehd.IndexOf("-ac") + 1]);

        var padded = AudioOffsetPlan.AudioArguments("/m/a.mkv", 1, "ac3", 6, 0.2, 1000, "/t/job");
        Assert.Equal("adelay=800:all=1", padded[padded.IndexOf("-af") + 1]);
        Assert.Equal("ac3", padded[padded.IndexOf("-c:a") + 1]);
    }

    [Fact]
    public void PointMasterAtShiftedAudio_SwapsTheAudioRendition()
    {
        var master = "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"group_aud\",URI=\"stream_1.m3u8\"\n#EXT-X-STREAM-INF:AUDIO=\"group_aud\"\nstream_0.m3u8\n";
        var pointed = AudioOffsetPlan.PointMasterAtShiftedAudio(master);
        Assert.Contains("URI=\"audio.m3u8\"", pointed);
        Assert.Contains("\nstream_0.m3u8", pointed);
    }

    [Theory]
    [InlineData("master.m3u8", true)]
    [InlineData("stream_0.m3u8", true)]
    [InlineData("init_0.mp4", true)]
    [InlineData("seg_0_00042.m4s", true)]
    [InlineData("audio.m3u8", true)]
    [InlineData("audio_init.mp4", true)]
    [InlineData("aseg_00042.m4s", true)]
    [InlineData("stream_1.m3u8", false)]
    [InlineData("seg_1_00042.m4s", false)]
    [InlineData("seg_0_0004a.m4s", false)]
    [InlineData("../master.m3u8", false)]
    [InlineData("aseg_00042.m4s.tmp", false)]
    public void IsServedFile_OnlyNamesTheJobsOwnFiles(string name, bool served)
    {
        Assert.Equal(served, AudioOffsetPlan.IsServedFile(name));
    }
}
