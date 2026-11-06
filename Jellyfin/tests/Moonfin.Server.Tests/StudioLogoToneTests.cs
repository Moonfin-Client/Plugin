using System.IO.Compression;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class StudioLogoToneTests
{
    // A 10x10 RGBA image whose middle 6x4 block has the given color on a transparent ground.
    private static byte[] Glyph(byte r, byte g, byte b, byte groundAlpha = 0)
    {
        var rows = new List<byte>();
        for (var y = 0; y < 10; y++)
        {
            rows.Add(0);
            for (var x = 0; x < 10; x++)
            {
                var inGlyph = x >= 2 && x < 8 && y >= 3 && y < 7;
                rows.AddRange(inGlyph ? new[] { r, g, b, (byte)255 } : new byte[] { 255, 255, 255, groundAlpha });
            }
        }
        return Png(10, 10, colorType: 6, bitDepth: 8, rows.ToArray());
    }

    // CRCs are left zero, since the reader never checks them.
    private static byte[] Png(int width, int height, byte colorType, byte bitDepth, byte[] filteredRows, byte[]? palette = null, byte[]? transparency = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        void Chunk(string type, byte[] body)
        {
            output.Write(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length });
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            output.Write(body);
            output.Write(new byte[4]);
        }

        Chunk("IHDR", new byte[]
        {
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            bitDepth, colorType, 0, 0, 0
        });
        if (palette != null) Chunk("PLTE", palette);
        if (transparency != null) Chunk("tRNS", transparency);

        using var zlib = new MemoryStream();
        zlib.Write(new byte[] { 0x78, 0x9C });
        using (var deflate = new DeflateStream(zlib, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(filteredRows);
        }
        Chunk("IDAT", zlib.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    [Fact]
    public void ABlackLogoOnATransparentGroundIsDark()
    {
        Assert.True(StudioLogoTone.IsDark(Glyph(0, 0, 0)));
    }

    [Fact]
    public void AColoredLogoKeepsItsArt()
    {
        Assert.False(StudioLogoTone.IsDark(Glyph(200, 20, 30)));
    }

    [Fact]
    public void ALightLogoKeepsItsArt()
    {
        Assert.False(StudioLogoTone.IsDark(Glyph(230, 230, 230)));
    }

    [Fact]
    public void ALogoOnASolidBoxKeepsItsArt()
    {
        Assert.False(StudioLogoTone.IsDark(Glyph(0, 0, 0, groundAlpha: 255)));
    }

    [Fact]
    public void APaletteLogoIsReadThroughItsTransparency()
    {
        // Two colors, transparent white and opaque black, one bit a pixel, glyph in the left half.
        var rows = new List<byte>();
        for (var y = 0; y < 8; y++)
        {
            rows.Add(0);
            rows.Add(y >= 2 && y < 6 ? (byte)0b0111_0000 : (byte)0);
        }
        var png = Png(8, 8, colorType: 3, bitDepth: 1, rows.ToArray(),
            palette: new byte[] { 255, 255, 255, 0, 0, 0 },
            transparency: new byte[] { 0, 255 });

        Assert.True(StudioLogoTone.IsDark(png));
    }

    [Fact]
    public void FilteredRowsDecodeTheSameAsPlainOnes()
    {
        // Every row Sub filtered: each byte stored as its difference from the pixel to the left.
        var rows = new List<byte>();
        for (var y = 0; y < 10; y++)
        {
            rows.Add(1);
            var previous = new byte[4];
            for (var x = 0; x < 10; x++)
            {
                var inGlyph = x >= 2 && x < 8 && y >= 3 && y < 7;
                var pixel = inGlyph ? new byte[] { 0, 0, 0, 255 } : new byte[] { 255, 255, 255, 0 };
                for (var c = 0; c < 4; c++) rows.Add((byte)(pixel[c] - previous[c]));
                previous = pixel;
            }
        }

        Assert.True(StudioLogoTone.IsDark(Png(10, 10, colorType: 6, bitDepth: 8, rows.ToArray())));
    }

    [Fact]
    public void SomethingThatIsntAPngKeepsItsArt()
    {
        Assert.False(StudioLogoTone.IsDark(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    [Fact]
    public void ADarkLogoGetsTmdbsWhiteFilter()
    {
        Assert.Equal("https://image.tmdb.org/t/p/w500_filter(duotone,ffffff,bababa)/abc.png", StudioLogoTone.LogoUrl("/abc.png", dark: true));
        Assert.Equal("https://image.tmdb.org/t/p/w500/abc.png", StudioLogoTone.LogoUrl("/abc.png", dark: false));
    }

    [Fact]
    public void AnEntryHandsOutItsToneOnlyWhenItHasALogo()
    {
        var dark = new StudioCompanyEntry { Id = 1, Name = "A", LogoPath = "/a.png", HasLogo = true, DarkLogo = true };
        var notChecked = new StudioCompanyEntry { Id = 2, Name = "B", LogoPath = "/b.png", HasLogo = true };
        var none = new StudioCompanyEntry { Id = 3, Name = "C", LogoPath = "/c.png", HasLogo = false };

        Assert.Contains("duotone", dark.ToInfo().LogoUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w500/b.png", notChecked.ToInfo().LogoUrl);
        Assert.Null(none.ToInfo().LogoUrl);
    }
}
