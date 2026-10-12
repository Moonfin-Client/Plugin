using System.Buffers.Binary;

namespace Moonfin.Server.Services;

/// <summary>
/// Reads an image's pixel size from its header without decoding it. Originals are stored as the
/// provider sent them (named .png, never validated), so PNG, JPEG, GIF, WebP and BMP are all read.
/// </summary>
internal static class ArtworkImageDimensions
{
    internal const int PngHeaderBytes = 24;

    // JPEG keeps its size in a SOF segment after EXIF/ICC blocks, which can be tens of KB.
    private const int MaxHeaderScanBytes = 256 * 1024;

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Width/height from a PNG header, or (0, 0) when it is not one.</summary>
    internal static (long Width, long Height) ReadPng(ReadOnlySpan<byte> header)
    {
        if (header.Length < PngHeaderBytes ||
            !header[..8].SequenceEqual(PngSignature) ||
            !header[12..16].SequenceEqual("IHDR"u8))
        {
            return (0, 0);
        }

        return (
            BinaryPrimitives.ReadUInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
    }

    /// <summary>Width/height of the image at <paramref name="path"/>, or null when unreadable or unrecognised.</summary>
    internal static (int Width, int Height)? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        byte[] buffer;
        int length;
        try
        {
            using var stream = File.OpenRead(path);
            buffer = new byte[(int)Math.Min(stream.Length, MaxHeaderScanBytes)];
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var (width, height) = Read(buffer.AsSpan(0, length));
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return null;
        }

        return ((int)width, (int)height);
    }

    /// <summary>Width/height from the start of an image file, or (0, 0) when the format is not recognised.</summary>
    internal static (long Width, long Height) Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= PngHeaderBytes && data[..8].SequenceEqual(PngSignature))
        {
            return ReadPng(data);
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            return ReadJpeg(data);
        }

        if (data.Length >= 10 && data[..4].SequenceEqual("GIF8"u8))
        {
            return (
                BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]));
        }

        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return ReadWebp(data);
        }

        if (data.Length >= 26 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            // Negative height means a top-down bitmap.
            return (
                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[18..22])),
                Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[22..26])));
        }

        return (0, 0);
    }

    private static (long Width, long Height) ReadJpeg(ReadOnlySpan<byte> data)
    {
        var offset = 2;
        while (offset + 9 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return (0, 0);
            }

            var marker = data[offset + 1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            // Standalone markers carry no length.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..(offset + 4)]);
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
            {
                return (
                    BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 7)..(offset + 9)]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 5)..(offset + 7)]));
            }

            if (marker == 0xDA || segmentLength < 2)
            {
                return (0, 0);
            }

            offset += 2 + segmentLength;
        }

        return (0, 0);
    }

    private static (long Width, long Height) ReadWebp(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return (
                BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..25]);
            return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return (
                (data[24] | (data[25] << 8) | (data[26] << 16)) + 1,
                (data[27] | (data[28] << 8) | (data[29] << 16)) + 1);
        }

        return (0, 0);
    }
}
