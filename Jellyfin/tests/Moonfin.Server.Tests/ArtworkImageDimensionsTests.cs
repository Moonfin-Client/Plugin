using System.Buffers.Binary;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class ArtworkImageDimensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-image-dimensions-{Guid.NewGuid():N}");

    public ArtworkImageDimensionsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Png_ReadsIhdr()
    {
        Assert.Equal((512L, 374L), ArtworkImageDimensions.Read(Png(512, 374)));
    }

    [Fact]
    public void Jpeg_SkipsSegmentsBeforeStartOfFrame()
    {
        Assert.Equal((512L, 357L), ArtworkImageDimensions.Read(Jpeg(512, 357, exifBytes: 4000)));
    }

    [Fact]
    public void ProgressiveJpeg_ReadsSof2()
    {
        Assert.Equal((300L, 420L), ArtworkImageDimensions.Read(Jpeg(300, 420, exifBytes: 0, sofMarker: 0xC2)));
    }

    [Fact]
    public void Gif_ReadsLogicalScreen()
    {
        var gif = new byte[13];
        "GIF89a"u8.CopyTo(gif);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(6), 256);
        BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(8), 224);

        Assert.Equal((256L, 224L), ArtworkImageDimensions.Read(gif));
    }

    [Fact]
    public void WebpVp8x_ReadsCanvas()
    {
        var webp = WebpHeader("VP8X");
        WriteUInt24(webp, 24, 511);
        WriteUInt24(webp, 27, 719);

        Assert.Equal((512L, 720L), ArtworkImageDimensions.Read(webp));
    }

    [Fact]
    public void WebpLossless_ReadsPackedSize()
    {
        var webp = WebpHeader("VP8L");
        webp[20] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(21), (uint)((512 - 1) | ((357 - 1) << 14)));

        Assert.Equal((512L, 357L), ArtworkImageDimensions.Read(webp));
    }

    [Fact]
    public void WebpLossy_ReadsFrameHeader()
    {
        var webp = WebpHeader("VP8 ");
        webp[23] = 0x9D;
        webp[24] = 0x01;
        webp[25] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(webp.AsSpan(26), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(webp.AsSpan(28), 512);

        Assert.Equal((512L, 512L), ArtworkImageDimensions.Read(webp));
    }

    [Fact]
    public void Bmp_TopDownHeightIsPositive()
    {
        var bmp = new byte[26];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 355);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -512);

        Assert.Equal((355L, 512L), ArtworkImageDimensions.Read(bmp));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E })]
    public void TruncatedOrUnknown_ReadsNothing(byte[] data)
    {
        Assert.Equal((0L, 0L), ArtworkImageDimensions.Read(data));
    }

    [Fact]
    public void TryRead_UsesContentNotExtension()
    {
        // Originals are cached as .png whatever the provider sent.
        var path = Path.Combine(_root, "boxart.png");
        File.WriteAllBytes(path, Jpeg(640, 480, exifBytes: 0));

        Assert.Equal((640, 480), ArtworkImageDimensions.TryRead(path));
    }

    [Fact]
    public void TryRead_MissingFileOrHtmlErrorPage_IsNull()
    {
        var html = Path.Combine(_root, "error.png");
        File.WriteAllText(html, "<html>404</html>");

        Assert.Null(ArtworkImageDimensions.TryRead(Path.Combine(_root, "absent.png")));
        Assert.Null(ArtworkImageDimensions.TryRead(html));
        Assert.Null(ArtworkImageDimensions.TryRead(null));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static byte[] Png(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        return png;
    }

    private static byte[] Jpeg(int width, int height, int exifBytes, byte sofMarker = 0xC0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
        bytes.AddRange(new byte[14]);
        if (exifBytes > 0)
        {
            bytes.AddRange(new byte[] { 0xFF, 0xE1, (byte)((exifBytes + 2) >> 8), (byte)(exifBytes + 2) });
            bytes.AddRange(new byte[exifBytes]);
        }

        bytes.AddRange(new byte[] { 0xFF, 0xC4, 0x00, 0x04, 0x00, 0x00 });
        bytes.AddRange(new byte[]
        {
            0xFF, sofMarker, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
        });
        bytes.AddRange(new byte[12]);
        return bytes.ToArray();
    }

    private static byte[] WebpHeader(string chunk)
    {
        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        System.Text.Encoding.ASCII.GetBytes(chunk).CopyTo(webp, 12);
        return webp;
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
    }
}
