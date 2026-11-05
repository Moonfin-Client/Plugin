using System.IO.Compression;

namespace Moonfin.Server.Services;

/// <summary>
/// Picks out the studio logos that are dark and colorless, which disappear on the apps' dark
/// cards. TMDB's duotone filter turns those white, while logos with color or a solid box of
/// their own keep their art.
/// </summary>
public static class StudioLogoTone
{
    private const string TmdbLogoBase = "https://image.tmdb.org/t/p/w500";
    private const string TmdbWhiteLogoBase = "https://image.tmdb.org/t/p/w500_filter(duotone,ffffff,bababa)";

    public static string LogoUrl(string logoPath, bool dark) => (dark ? TmdbWhiteLogoBase : TmdbLogoBase) + logoPath;

    /// <summary>
    /// True for a logo that covers less than 90% of its image, is mostly dark and has almost no
    /// color. False for anything else, including a PNG this can't read.
    /// </summary>
    public static bool IsDark(byte[] png)
    {
        var rgba = DecodePng(png);
        if (rgba == null) return false;

        double alphaSum = 0, graySum = 0, saturationSum = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var alpha = rgba[i + 3] / 255.0;
            if (alpha == 0) continue;

            double r = rgba[i] / 255.0, g = rgba[i + 1] / 255.0, b = rgba[i + 2] / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var lightness = (max + min) / 2;
            var saturation = max == min ? 0 : Math.Min(1, (max - min) / (1 - Math.Abs(2 * lightness - 1)));

            alphaSum += alpha;
            graySum += (0.2126 * r + 0.7152 * g + 0.0722 * b) * alpha;
            saturationSum += saturation * alpha;
        }

        if (alphaSum == 0) return false;
        var coverage = alphaSum / (rgba.Length / 4);
        return coverage < 0.9 && graySum / alphaSum < 0.4 && saturationSum / alphaSum < 0.25;
    }

    /// <summary>
    /// Decodes a non-interlaced PNG to 8-bit RGBA. Covers what TMDB serves logos as: 8-bit gray,
    /// RGB and their alpha forms, plus palettes of 1 to 8 bits. Null for anything else.
    /// </summary>
    private static byte[]? DecodePng(byte[] data)
    {
        if (data.Length < 8 || data[0] != 0x89 || data[1] != 'P' || data[2] != 'N' || data[3] != 'G') return null;

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var length = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var start = pos + 8;
            if (length < 0 || start + length > data.Length) return null;

            if (type == "IHDR" && length >= 13)
            {
                width = (data[start] << 24) | (data[start + 1] << 16) | (data[start + 2] << 8) | data[start + 3];
                height = (data[start + 4] << 24) | (data[start + 5] << 16) | (data[start + 6] << 8) | data[start + 7];
                bitDepth = data[start + 8];
                colorType = data[start + 9];
                interlace = data[start + 12];
            }
            else if (type == "PLTE") palette = data.AsSpan(start, length).ToArray();
            else if (type == "tRNS") transparency = data.AsSpan(start, length).ToArray();
            else if (type == "IDAT") compressed.Write(data, start, length);
            else if (type == "IEND") break;

            pos = start + length + 4;
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        var depthOk = bitDepth == 8 || (colorType == 3 && bitDepth is 1 or 2 or 4);
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000 || interlace != 0 || channels == 0 || !depthOk) return null;
        if (colorType == 3 && palette == null) return null;

        var bitsPerPixel = channels * bitDepth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var step = Math.Max(1, bitsPerPixel / 8);
        var raw = Inflate(compressed.ToArray());
        if (raw == null || raw.Length < (stride + 1) * height) return null;

        var rgba = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            var filter = raw[rowStart];
            for (var x = 0; x < stride; x++)
            {
                int left = x >= step ? current[x - step] : 0;
                int up = previous[x];
                int upLeft = x >= step ? previous[x - step] : 0;
                int value = raw[rowStart + 1 + x];
                current[x] = (byte)(filter switch
                {
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) >> 1),
                    4 => value + Paeth(left, up, upLeft),
                    _ => value
                });
            }

            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                switch (colorType)
                {
                    case 0:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = current[x];
                        rgba[o + 3] = 255;
                        break;
                    case 2:
                        rgba[o] = current[x * 3];
                        rgba[o + 1] = current[x * 3 + 1];
                        rgba[o + 2] = current[x * 3 + 2];
                        rgba[o + 3] = 255;
                        break;
                    case 3:
                        var bit = x * bitDepth;
                        var index = (current[bit / 8] >> (8 - bitDepth - bit % 8)) & ((1 << bitDepth) - 1);
                        if (index * 3 + 2 >= palette!.Length) return null;
                        rgba[o] = palette[index * 3];
                        rgba[o + 1] = palette[index * 3 + 1];
                        rgba[o + 2] = palette[index * 3 + 2];
                        rgba[o + 3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                        break;
                    case 4:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = current[x * 2];
                        rgba[o + 3] = current[x * 2 + 1];
                        break;
                    default:
                        Buffer.BlockCopy(current, x * 4, rgba, o, 4);
                        break;
                }
            }

            (previous, current) = (current, previous);
        }

        return rgba;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // The IDAT stream is zlib, which is raw deflate behind a two byte header.
    private static byte[]? Inflate(byte[] zlib)
    {
        if (zlib.Length < 2) return null;
        try
        {
            using var input = new MemoryStream(zlib, 2, zlib.Length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            deflate.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
