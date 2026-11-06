using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Moonfin.PosterBadges;

/// <summary>A built-in label the admin can switch on without writing conditions.</summary>
public class PosterBadgePreset
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Within a group at most one preset holds, except for release tags.</summary>
    public string Group { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// The built-in labels and how each is detected from the server's track data.
///
/// Each group picks one answer per item, so a 4K file never also says 1080p and an Atmos
/// track never also says TrueHD. Detection lives here rather than in admin rules so a fix
/// reaches every server on the next plugin update.
/// </summary>
public static class PosterBadgePresets
{
    public static readonly IReadOnlyList<PosterBadgePreset> All = new List<PosterBadgePreset>
    {
        P("8k", "8K", "resolution", "#DC2626", "Width from 7600 or height from 4300."),
        P("4k", "4K", "resolution", "#7C3AED", "Width from 3800 or height from 2000, so a 3840x1600 film counts."),
        P("1440p", "1440p", "resolution", "#C026D3", "Width from 2500 or height from 1400."),
        P("1080p", "1080p", "resolution", "#0284C7", "Width from 1800 or height from 1000."),
        P("720p", "720p", "resolution", "#0D9488", "Width from 1200 or height from 700."),
        P("480p", "480p", "resolution", "#A16207", "Width from 600 or height from 400."),
        P("sd", "SD", "resolution", "#6B7280", "Anything smaller."),

        P("dolbyVision", "DV", "hdr", "#1F2937", "Dolby Vision, with or without an HDR10 fallback."),
        P("hdr10plus", "HDR10+", "hdr", "#D97706", "HDR10+ without Dolby Vision."),
        P("hdr10", "HDR10", "hdr", "#CA8A04", "Plain HDR10."),
        P("hlg", "HLG", "hdr", "#65A30D", "HLG, the broadcast HDR."),

        P("atmos", "Atmos", "audio", "#2563EB", "Dolby Atmos on any audio track."),
        P("dtsx", "DTS:X", "audio", "#EA580C", "DTS:X on any audio track."),
        P("truehd", "TrueHD", "audio", "#16A34A", "Dolby TrueHD without Atmos."),
        P("dtshdma", "DTS-HD MA", "audio", "#C2410C", "DTS-HD Master Audio without DTS:X."),
        P("dts", "DTS", "audio", "#F97316", "Other DTS."),
        P("ddplus", "DD+", "audio", "#0891B2", "Dolby Digital Plus (E-AC-3) without Atmos."),
        P("dd", "DD", "audio", "#64748B", "Dolby Digital (AC-3)."),

        P("7.1", "7.1", "channels", "#9333EA", "An audio track with 8 channels or more."),
        P("5.1", "5.1", "channels", "#6D28D9", "An audio track with 6 or 7 channels."),

        P("av1", "AV1", "codec", "#DB2777", "AV1 video."),
        P("hevc", "HEVC", "codec", "#4F46E5", "HEVC (H.265) video."),
        P("h264", "H.264", "codec", "#475569", "H.264 (AVC) video."),
        P("vp9", "VP9", "codec", "#A21CAF", "VP9 video."),

        P("remux", "REMUX", "release", "#BE123C", "\"REMUX\" in the file path."),
        P("imax", "IMAX", "release", "#0369A1", "\"IMAX\" in the file path, but not \"NON-IMAX\"."),
    };

    private static PosterBadgePreset P(string id, string label, string group, string color, string description) =>
        new() { Id = id, Label = label, Group = group, Color = color, Description = description };

    // Not \b: in .NET "_" is a word character, so "Movie_REMUX_2160p" would never match.
    private static readonly Regex Remux = new(@"(?<![\p{L}\p{N}])REMUX(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Imax = new(@"(?<!NON[ ._-]?)(?<![\p{L}\p{N}])IMAX(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DtsX = new(@"DTS[:\- ]?X\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MasterAudio = new(@"\bMA\b|Master Audio", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The ids of every preset the item meets.</summary>
    public static HashSet<string> Detect(string? path, IReadOnlyList<object> tracks)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The widest video track, since some files carry a small second one.
        object? video = null;
        var videoWidth = -1;
        var bestAudio = int.MaxValue;
        var channels = 0;

        foreach (var track in tracks)
        {
            var type = Text(track, "Type");
            if (string.Equals(type, "Video", StringComparison.OrdinalIgnoreCase))
            {
                var width = Number(track, "Width") ?? 0;
                if (width > videoWidth)
                {
                    video = track;
                    videoWidth = width;
                }
            }
            else if (string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                bestAudio = Math.Min(bestAudio, AudioRank(track));
                channels = Math.Max(channels, Number(track, "Channels") ?? 0);
            }
        }

        if (video != null)
        {
            Add(found, Resolution(Number(video, "Width") ?? 0, Number(video, "Height") ?? 0));
            Add(found, DynamicRange(video));
            Add(found, VideoCodec(Text(video, "Codec")));
        }

        if (bestAudio < AudioOrder.Length)
        {
            found.Add(AudioOrder[bestAudio]);
        }

        Add(found, channels >= 8 ? "7.1" : channels >= 6 ? "5.1" : null);

        if (!string.IsNullOrEmpty(path))
        {
            if (Remux.IsMatch(path))
            {
                found.Add("remux");
            }

            if (Imax.IsMatch(path))
            {
                found.Add("imax");
            }
        }

        return found;
    }

    private static void Add(HashSet<string> found, string? id)
    {
        if (id != null)
        {
            found.Add(id);
        }
    }

    private static string? Resolution(int width, int height)
    {
        if (width <= 0 && height <= 0)
        {
            return null;
        }

        // The same lines as the details page in the Moonfin apps, so a file never reads
        // 4K on its poster and 1440p once opened. Width counts, since scope films are wide
        // but short: 3840x1600 is still 4K.
        if (width >= 7600 || height >= 4300) return "8k";
        if (width >= 3800 || height >= 2000) return "4k";
        if (width >= 2500 || height >= 1400) return "1440p";
        if (width >= 1800 || height >= 1000) return "1080p";
        if (width >= 1200 || height >= 700) return "720p";
        if (width >= 600 || height >= 400) return "480p";
        return "sd";
    }

    private static string? DynamicRange(object video)
    {
        // Jellyfin names the range in VideoRangeType, Emby in ExtendedVideoType.
        var range = Text(video, "VideoRangeType") ?? string.Empty;
        var extended = Text(video, "ExtendedVideoType") ?? string.Empty;

        if ((range.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase) && !range.Equals("DOVIInvalid", StringComparison.OrdinalIgnoreCase))
            || extended.Equals("DolbyVision", StringComparison.OrdinalIgnoreCase))
        {
            return "dolbyVision";
        }

        if (range.Equals("HDR10Plus", StringComparison.OrdinalIgnoreCase) || extended.Equals("Hdr10Plus", StringComparison.OrdinalIgnoreCase))
        {
            return "hdr10plus";
        }

        if (range.Equals("HDR10", StringComparison.OrdinalIgnoreCase) || extended.Equals("Hdr10", StringComparison.OrdinalIgnoreCase))
        {
            return "hdr10";
        }

        if (range.Equals("HLG", StringComparison.OrdinalIgnoreCase) || extended.Equals("HyperLogGamma", StringComparison.OrdinalIgnoreCase))
        {
            return "hlg";
        }

        return null;
    }

    private static string? VideoCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        "av1" => "av1",
        "hevc" or "h265" => "hevc",
        "h264" or "avc" => "h264",
        "vp9" => "vp9",
        _ => null,
    };

    /// <summary>Best first. A file with several tracks gets the best one's label.</summary>
    private static readonly string[] AudioOrder = { "atmos", "dtsx", "truehd", "dtshdma", "dts", "ddplus", "dd" };

    private static int AudioRank(object track)
    {
        var codec = Text(track, "Codec")?.ToLowerInvariant() ?? string.Empty;
        var spatial = Text(track, "AudioSpatialFormat") ?? string.Empty;

        // Ffprobe puts Atmos and DTS:X in the profile, and releases often in the title.
        var text = Text(track, "Profile") + " " + Text(track, "Title");

        if (spatial.Equals("DolbyAtmos", StringComparison.OrdinalIgnoreCase) || text.IndexOf("Atmos", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 0;
        }

        var isDts = codec == "dts" || codec == "dca";
        if (spatial.Equals("DTSX", StringComparison.OrdinalIgnoreCase) || (isDts && DtsX.IsMatch(text)))
        {
            return 1;
        }

        if (codec == "truehd") return 2;
        if (isDts && MasterAudio.IsMatch(text)) return 3;
        if (isDts) return 4;
        if (codec == "eac3") return 5;
        if (codec == "ac3") return 6;
        return int.MaxValue;
    }

    private static string? Text(object track, string property) =>
        track.GetType().GetProperty(property)?.GetValue(track)?.ToString();

    private static int? Number(object track, string property)
    {
        var value = track.GetType().GetProperty(property)?.GetValue(track);
        return value == null ? null : (int?)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
