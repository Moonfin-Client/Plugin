using System.Reflection;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public partial class RdbServiceTests
{
    private const long MaxTransformBytes = 64L * 1024 * 1024;

    [Fact]
    public void ComputeCrcCandidates_PlainRom_HasOnlyTheFullFileCrc()
    {
        var bytes = PatternBytes(2048, seed: 1);
        var path = WriteRom("plain.bin", bytes);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        Assert.Equal([Crc32(bytes)], candidates);
    }

    [Fact]
    public void ComputeCrcCandidates_InesRom_StillAddsHeaderSkippedCrc()
    {
        var bytes = PatternBytes(2048, seed: 2);
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        var path = WriteRom("game.nes", bytes);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        Assert.Equal([Crc32(bytes), Crc32(bytes[16..])], candidates);
    }

    [Fact]
    public void ComputeCrcCandidates_Smd_AddsDeinterleavedCrcAfterFullFileCrc()
    {
        var (smd, deinterleaved) = BuildSmd(blocks: 2);
        var path = WriteRom("game.smd", smd);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        Assert.Equal(Crc32(smd), candidates[0]);
        Assert.Contains(Crc32(deinterleaved), candidates);
    }

    [Fact]
    public void ComputeCrcCandidates_SmdSizeWithoutSignature_IsNotDeinterleaved()
    {
        var (smd, _) = BuildSmd(blocks: 2);
        smd[8] = 0;
        smd[9] = 0;
        var path = WriteRom("nosig.smd", smd);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        // Still a 512-byte-header size, so only the header-skipped candidate may appear.
        Assert.Equal([Crc32(smd), Crc32(smd[512..])], candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComputeCrcCandidates_N64NonBigEndian_AddsBigEndianCrc(bool littleEndianWords)
    {
        var z64 = PatternBytes(4096, seed: 3);
        z64[0] = 0x80;
        z64[1] = 0x37;
        z64[2] = 0x12;
        z64[3] = 0x40;
        var onDisk = new byte[z64.Length];
        for (var i = 0; i < z64.Length; i += 4)
        {
            if (littleEndianWords)
            {
                onDisk[i] = z64[i + 3];
                onDisk[i + 1] = z64[i + 2];
                onDisk[i + 2] = z64[i + 1];
                onDisk[i + 3] = z64[i];
            }
            else
            {
                onDisk[i] = z64[i + 1];
                onDisk[i + 1] = z64[i];
                onDisk[i + 2] = z64[i + 3];
                onDisk[i + 3] = z64[i + 2];
            }
        }

        var path = WriteRom(littleEndianWords ? "game.n64" : "game.v64", onDisk);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        Assert.Equal([Crc32(onDisk), Crc32(z64)], candidates);
    }

    [Fact]
    public void ComputeCrcCandidates_BigEndianN64_GainsNoCandidates()
    {
        var z64 = PatternBytes(4096, seed: 4);
        z64[0] = 0x80;
        z64[1] = 0x37;
        z64[2] = 0x12;
        z64[3] = 0x40;
        var path = WriteRom("game.z64", z64);

        Assert.Equal([Crc32(z64)], RdbMatcher.ComputeCrcCandidates(path));
    }

    [Fact]
    public void ComputeCrcCandidates_N64LengthNotMultipleOfFour_IsNotConverted()
    {
        var bytes = PatternBytes(4098, seed: 5);
        bytes[0] = 0x37;
        bytes[1] = 0x80;
        bytes[2] = 0x40;
        bytes[3] = 0x12;
        var path = WriteRom("odd.v64", bytes);

        Assert.Equal([Crc32(bytes)], RdbMatcher.ComputeCrcCandidates(path));
    }

    [Fact]
    public void ComputeCrcCandidates_CopierHeaderSize_AddsHeaderlessCrc()
    {
        var bytes = PatternBytes(1024 * 3 + 512, seed: 6);
        var path = WriteRom("game.smc", bytes);

        var candidates = RdbMatcher.ComputeCrcCandidates(path);

        Assert.Equal([Crc32(bytes), Crc32(bytes[512..])], candidates);
    }

    [Fact]
    public void ComputeCrcCandidates_N64At64MiB_Converts_AndJustOverDoesNot()
    {
        var atLimit = WriteSparseN64("limit.v64", MaxTransformBytes);
        var overLimit = WriteSparseN64("over.v64", MaxTransformBytes + 4);

        Assert.Equal(2, RdbMatcher.ComputeCrcCandidates(atLimit).Count);
        Assert.Single(RdbMatcher.ComputeCrcCandidates(overLimit));
    }

    [Fact]
    public void ComputeCrcCandidates_CopierHeaderOver64MiB_IsNotStripped()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "big.smc");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(MaxTransformBytes + 512);
        }

        Assert.Single(RdbMatcher.ComputeCrcCandidates(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ComputeCrcCandidates_TinyFile_YieldsOnlyFullFileCrc(int length)
    {
        var bytes = PatternBytes(length, seed: 10);
        var path = WriteRom($"tiny{length}.bin", bytes);

        Assert.Equal([Crc32(bytes)], RdbMatcher.ComputeCrcCandidates(path));
    }

    [Fact]
    public void ComputeCrcCandidates_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() => RdbMatcher.ComputeCrcCandidates(Path.Combine(_root, "missing.smd")));
    }

    [Fact]
    public void Match_SmdRom_ResolvesViaDeinterleavedCrc()
    {
        var (smd, deinterleaved) = BuildSmd(blocks: 3);
        var path = WriteRom("zzz.smd", smd);

        var result = MatchRom(path, "Unrelated Title", [(Crc32(deinterleaved), "Sonic Quest (USA)")]);

        Assert.Equal("Sonic Quest (USA)", result?.Name);
    }

    [Fact]
    public void Match_V64Rom_ResolvesViaBigEndianCrc()
    {
        var z64 = PatternBytes(4096, seed: 7);
        var v64 = new byte[z64.Length];
        for (var i = 0; i < z64.Length; i += 2)
        {
            v64[i] = z64[i + 1];
            v64[i + 1] = z64[i];
        }

        z64[0] = 0x80;
        z64[1] = 0x37;
        z64[2] = 0x12;
        z64[3] = 0x40;
        v64[0] = 0x37;
        v64[1] = 0x80;
        v64[2] = 0x40;
        v64[3] = 0x12;
        var path = WriteRom("zzz.v64", v64);

        var result = MatchRom(path, "Unrelated Title", [(Crc32(z64), "Mario Quest (USA)")]);

        Assert.Equal("Mario Quest (USA)", result?.Name);
    }

    [Fact]
    public void Match_FullFileCrcHit_WinsOverTransformedCrcHit()
    {
        var bytes = PatternBytes(1024 * 2 + 512, seed: 8);
        var path = WriteRom("zzz.smc", bytes);

        var result = MatchRom(
            path,
            "Unrelated Title",
            [(Crc32(bytes[512..]), "Headerless Quest (USA)"), (Crc32(bytes), "Full Quest (USA)")]);

        Assert.Equal("Full Quest (USA)", result?.Name);
    }

    [Fact]
    public void Match_CopierHeaderSizeWithNoCrcHit_StillResolvesByName()
    {
        var bytes = PatternBytes(1024 * 2 + 512, seed: 9);
        var path = WriteRom("Name Quest.smc", bytes);

        var result = MatchRom(
            path,
            "Name Quest",
            [(0x12345678u, "Other Game (USA)")],
            nameKey: "namequest",
            nameRecord: "Name Quest (USA)");

        Assert.Equal("Name Quest (USA)", result?.Name);
    }

    private RdbRecord? MatchRom(
        string romPath,
        string title,
        IReadOnlyList<(uint Crc, string Name)> crcRecords,
        string? nameKey = null,
        string? nameRecord = null)
    {
        var byCrc = crcRecords.ToDictionary(r => r.Crc, r => new RdbRecord { Name = r.Name });
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal);
        if (nameKey is not null)
        {
            byName[nameKey] = new RdbRecord { Name = nameRecord! };
        }

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        return (RdbRecord?)GetMatchMethod().Invoke(service, [index, romPath, title]);
    }

    private string WriteSparseN64(string fileName, long length)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(length);
        fs.Write([0x37, 0x80, 0x40, 0x12]);
        return path;
    }

    private static byte[] PatternBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // Builds an SMD file from known de-interleaved data: per 16 KiB block the first half holds
    // the odd-offset bytes and the second half the even-offset bytes.
    private static (byte[] Smd, byte[] Deinterleaved) BuildSmd(int blocks)
    {
        var plain = PatternBytes(blocks * 16384, seed: 100 + blocks);
        var smd = new byte[512 + plain.Length];
        smd[8] = 0xAA;
        smd[9] = 0xBB;
        for (var b = 0; b < blocks; b++)
        {
            var src = b * 16384;
            var dst = 512 + src;
            for (var i = 0; i < 8192; i++)
            {
                smd[dst + i] = plain[src + 2 * i + 1];
                smd[dst + 8192 + i] = plain[src + 2 * i];
            }
        }

        return (smd, plain);
    }
}
