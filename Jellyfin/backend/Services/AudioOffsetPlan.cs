using System.Globalization;

namespace Moonfin.Server.Services;

/// <summary>
/// Works out how to remux a title with its audio moved earlier or later, for clients whose player
/// can't delay audio itself (Roku's Video node has no such control). The picture is copied from a
/// keyframe and the sound is read from a point moved by the offset, each into an HLS rendition of
/// its own that starts at zero, so the two meet in the player already offset.
/// </summary>
public static class AudioOffsetPlan
{
    /// <summary>The largest shift a client may ask for, either way.</summary>
    public const int MaxOffsetMs = 10_000;

    /// <summary>Seconds of video read before the start point when looking for the keyframe to cut at.</summary>
    public const double KeyframeSearchWindowSeconds = 20;

    public static int ClampOffset(int offsetMs) => Math.Clamp(offsetMs, -MaxOffsetMs, MaxOffsetMs);

    /// <summary>
    /// The last keyframe at or before <paramref name="target"/>, since copied video can only start
    /// on one. The title then starts a little before where the viewer was, never after. Falls back
    /// to the earliest keyframe read when none comes before the target, and to the start of the
    /// file when none was read at all.
    /// </summary>
    public static double ChooseKeyframe(IEnumerable<double> keyframes, double target)
    {
        double? before = null;
        double? earliest = null;
        foreach (var k in keyframes)
        {
            if (k < 0)
            {
                continue;
            }

            if (k <= target && (before == null || k > before))
            {
                before = k;
            }

            if (earliest == null || k < earliest)
            {
                earliest = k;
            }
        }

        return before ?? earliest ?? 0;
    }

    /// <summary>How far before the audio's start point the file is opened, so the cut can be made exactly.</summary>
    public const double AudioSeekLeadSeconds = 10;

    /// <summary>
    /// Where in the title to start reading the sound, so that the sound from any moment plays
    /// <paramref name="offsetMs"/> after (or, negative, before) its picture: both renditions count
    /// from zero, and the sound under the picture's first frame comes from <c>start - offset</c>.
    /// Near the start of the file there may be no sound that early, and the rest is made up with
    /// silence (<c>Pad</c>, in seconds).
    /// </summary>
    /// <remarks>
    /// Roku lines an audio rendition up with the video by where each sits in its playlist, not by
    /// timestamps, so the shift has to be in what the audio holds rather than in when it says it plays.
    /// </remarks>
    public static (double Seek, double Pad) AudioStart(double videoStart, int offsetMs)
    {
        var seek = videoStart - offsetMs / 1000.0;
        return seek >= 0 ? (seek, 0) : (0, -seek);
    }

    // Every Roku plays these or passes them through, so they're copied as they are, which keeps
    // Dolby Atmos riding in DD+
    private static readonly HashSet<string> CopyableAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac3", "eac3", "aac",
    };

    public static bool CopiesAudio(string? sourceCodec) => CopyableAudio.Contains(sourceCodec ?? string.Empty);

    /// <summary>AC3 keeps up to 5.1 and plays on every Roku, passed through or decoded.</summary>
    public static (int Channels, string Bitrate) AudioEncoding(int? sourceChannels)
    {
        var channels = Math.Clamp(sourceChannels ?? 2, 1, 6);
        return (channels, channels > 2 ? "640k" : "192k");
    }

    /// <summary>
    /// The ffmpeg arguments for the picture, copied from <paramref name="videoStart"/> into an fMP4
    /// rendition, with the master playlist. Roku plays fMP4 audio only as a rendition of its own, so
    /// the master names the audio as one. The unshifted audio this also writes is there only so
    /// ffmpeg lists both codecs in the master; <see cref="PointMasterAtShiftedAudio"/> swaps it for
    /// the shifted one.
    /// </summary>
    public static List<string> VideoArguments(
        string inputPath,
        int videoStreamIndex,
        int audioStreamIndex,
        string? audioCodec,
        int? audioChannels,
        double videoStart,
        string outputDir)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            // A remux runs far faster than playback. Reading at twice the speed after the first
            // minute keeps it ahead of the player without writing out the whole title at once.
            "-readrate", "2", "-readrate_initial_burst", "60",
            "-ss", Seconds(videoStart), "-i", inputPath,
            "-map", "0:" + videoStreamIndex.ToString(CultureInfo.InvariantCulture),
            "-map", "0:" + audioStreamIndex.ToString(CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:v", "copy",
        };
        args.AddRange(AudioCodecArguments(audioCodec, audioChannels));
        args.AddRange(
        [
            "-max_muxing_queue_size", "4096",
            "-f", "hls",
            "-hls_time", "6",
            "-hls_playlist_type", "event",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", "init_%v.mp4",
            "-hls_segment_filename", Path.Combine(outputDir, "seg_%v_%05d.m4s"),
            "-master_pl_name", PlaylistName,
            "-var_stream_map", "v:0,agroup:aud a:0,agroup:aud,default:yes",
            Path.Combine(outputDir, "stream_%v.m3u8"),
        ]);
        return args;
    }

    /// <summary>The ffmpeg arguments for the shifted sound, read from <see cref="AudioStart"/> into an fMP4 rendition of its own.</summary>
    public static List<string> AudioArguments(
        string inputPath,
        int audioStreamIndex,
        string? audioCodec,
        int? audioChannels,
        double videoStart,
        int offsetMs,
        string outputDir)
    {
        var (seek, pad) = AudioStart(videoStart, offsetMs);
        // Seeking a file lands on the cluster of the keyframe before, and copied audio is never
        // trimmed back to the asked point, so it would start where the picture does whatever the
        // offset. The file is opened a little before the point instead, and an output seek drops
        // the packets up to it exactly.
        var opened = Math.Max(0, seek - AudioSeekLeadSeconds);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-readrate", "2", "-readrate_initial_burst", "60",
            "-ss", Seconds(opened), "-i", inputPath,
            "-ss", Seconds(seek - opened),
            "-map", "0:" + audioStreamIndex.ToString(CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-map_chapters", "-1",
        };

        if (pad > 0)
        {
            // Silence can't be copied in, so a padded start is encoded
            var (channels, bitrate) = AudioEncoding(audioChannels);
            args.AddRange(["-af", "adelay=" + Math.Round(pad * 1000).ToString(CultureInfo.InvariantCulture) + ":all=1",
                "-c:a", "ac3", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-b:a", bitrate]);
        }
        else
        {
            args.AddRange(AudioCodecArguments(audioCodec, audioChannels));
        }

        args.AddRange(
        [
            "-max_muxing_queue_size", "4096",
            "-f", "hls",
            "-hls_time", "6",
            "-hls_playlist_type", "event",
            "-hls_segment_type", "fmp4",
            "-hls_fmp4_init_filename", AudioInitName,
            "-hls_segment_filename", Path.Combine(outputDir, "aseg_%05d.m4s"),
            Path.Combine(outputDir, AudioPlaylistName),
        ]);
        return args;
    }

    private static List<string> AudioCodecArguments(string? audioCodec, int? audioChannels)
    {
        if (CopiesAudio(audioCodec))
        {
            return ["-c:a", "copy"];
        }

        var (channels, bitrate) = AudioEncoding(audioChannels);
        return ["-c:a", "ac3", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-b:a", bitrate];
    }

    /// <summary>The master playlist with its audio rendition pointed at the shifted one.</summary>
    public static string PointMasterAtShiftedAudio(string master) =>
        master.Replace("URI=\"stream_1.m3u8\"", "URI=\"" + AudioPlaylistName + "\"", StringComparison.Ordinal);

    /// <summary>The ffprobe arguments that list the video's packet times and keyframe flags just before <paramref name="target"/>.</summary>
    public static List<string> KeyframeProbeArguments(string inputPath, int videoStreamIndex, double target)
    {
        var from = Math.Max(0, target - KeyframeSearchWindowSeconds);
        return
        [
            "-v", "error",
            "-select_streams", videoStreamIndex.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", Seconds(from) + "%" + Seconds(target + 0.5),
            "-show_entries", "packet=pts_time,flags:format=start_time",
            "-of", "csv=p=0",
            inputPath,
        ];
    }

    /// <summary>
    /// Keyframe times, counted from the start of the file, from <see cref="KeyframeProbeArguments"/>'s
    /// output. Packet lines read <c>pts_time,flags</c> with K in the flags for a keyframe; the
    /// format's start time comes on a line of its own and is taken off every packet time.
    /// </summary>
    public static List<double> ParseKeyframes(string probeOutput)
    {
        var times = new List<double>();
        double start = 0;
        foreach (var raw in probeOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length == 1)
            {
                if (TryParse(parts[0], out var s))
                {
                    start = s;
                }

                continue;
            }

            if (parts[1].Contains('K', StringComparison.Ordinal) && TryParse(parts[0], out var t))
            {
                times.Add(t);
            }
        }

        return times.Select(t => t - start).ToList();
    }

    /// <summary>The master playlist, which names the video's playlist and the audio's.</summary>
    public const string PlaylistName = "master.m3u8";

    public const string AudioPlaylistName = "audio.m3u8";

    private const string AudioInitName = "audio_init.mp4";

    /// <summary>The video's and the shifted audio's own playlists, which both have to hold a segment before the stream can play.</summary>
    public static readonly string[] MediaPlaylists = ["stream_0.m3u8", AudioPlaylistName];

    private static readonly System.Text.RegularExpressions.Regex ServedFile = new(
        @"^(master\.m3u8|stream_0\.m3u8|init_0\.mp4|seg_0_\d{5}\.m4s|audio\.m3u8|audio_init\.mp4|aseg_\d{5}\.m4s)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="name"/> is one of the files a job writes, so nothing else in its folder can be asked for.</summary>
    public static bool IsServedFile(string name) => ServedFile.IsMatch(name);

    public static string ContentType(string name) =>
        name.EndsWith(".m3u8", StringComparison.Ordinal) ? "application/vnd.apple.mpegurl" : "video/mp4";

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryParse(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
