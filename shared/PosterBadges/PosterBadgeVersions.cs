using System;
using System.Collections;
using System.Globalization;

namespace Moonfin.PosterBadges;

/// <summary>The file a movie's labels are read from when the library holds several versions.</summary>
public static class PosterBadgeVersions
{
    /// <summary>
    /// The version with the largest picture, as the server's own media sources, read by
    /// property name so this works with Jellyfin and Emby alike. A tie keeps the first, which
    /// is the main version. Null when nothing usable came back, so the caller keeps its own.
    /// </summary>
    public static (string? Path, IEnumerable? Streams)? Pick(IEnumerable? sources)
    {
        if (sources == null)
        {
            return null;
        }

        object? best = null;
        IEnumerable? bestStreams = null;
        long bestPixels = -1;

        foreach (var source in sources)
        {
            if (source == null)
            {
                continue;
            }

            var streams = source.GetType().GetProperty("MediaStreams")?.GetValue(source) as IEnumerable;
            var pixels = LargestPicture(streams);
            if (pixels > bestPixels)
            {
                best = source;
                bestStreams = streams;
                bestPixels = pixels;
            }
        }

        if (best == null || bestStreams == null)
        {
            return null;
        }

        return (best.GetType().GetProperty("Path")?.GetValue(best) as string, bestStreams);
    }

    private static long LargestPicture(IEnumerable? streams)
    {
        long largest = 0;
        if (streams == null)
        {
            return largest;
        }

        foreach (var stream in streams)
        {
            var type = stream?.GetType();
            if (type == null || !string.Equals(type.GetProperty("Type")?.GetValue(stream)?.ToString(), "Video", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            largest = Math.Max(largest, Number(type.GetProperty("Width")?.GetValue(stream)) * Number(type.GetProperty("Height")?.GetValue(stream)));
        }

        return largest;
    }

    private static long Number(object? value) =>
        value == null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
