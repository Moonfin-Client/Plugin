using System.Security.Cryptography;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class ArcadeCompatibilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-arcade-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_PrefersFbneo_WhenArchiveMatchesBothPinnedDats()
    {
        Directory.CreateDirectory(_root);
        var spyhunt = WriteZip("spyhunt.zip", ("sh_u1.bin", new byte[] { 1, 2, 3 }));
        var sha1 = Sha1([1, 2, 3]);
        var service = new ArcadeCompatibilityService(
            WriteDat("fbneo.dat", "spyhunt", 3, sha1),
            WriteDat("mame.dat", "spyhunt", 3, sha1));

        var result = service.Resolve(spyhunt, "arcade");

        Assert.Equal("arcade", result.RecommendedCore);
        Assert.Equal(["arcade", "mame"], result.AvailableCores);
        Assert.NotEmpty(result.ContentKey);
    }

    [Fact]
    public void Resolve_UsesMame_WhenArchiveOnlyMatchesMameDat()
    {
        Directory.CreateDirectory(_root);
        var copsnrob = WriteZip("copsnrob.zip", ("cops.rom", new byte[] { 4, 5, 6 }));
        var sha1 = Sha1([4, 5, 6]);
        var service = new ArcadeCompatibilityService(
            WriteDat("fbneo.dat", "other", 3, Sha1([9, 9, 9])),
            WriteDat("mame.dat", "copsnrob", 3, sha1));

        var result = service.Resolve(copsnrob, "arcade");

        Assert.Equal("mame", result.RecommendedCore);
        Assert.Equal(["mame"], result.AvailableCores);
    }

    [Fact]
    public void Resolve_MatchesCrcOnlyDatEntries()
    {
        Directory.CreateDirectory(_root);
        var spyhunt = WriteZip(
            "spyhunt.zip",
            ("sh_u1.bin", new byte[] { 1, 2, 3 }),
            ("sh_u2.bin", new byte[] { 8, 8, 8, 8 }));
        var secondCrc = Crc32Hex([8, 8, 8, 8]);
        var datPath = Path.Combine(_root, "fbneo.dat");
        File.WriteAllText(
            datPath,
            "<datafile><game name=\"spyhunt\">" +
            "<rom name=\"sh_u1.bin\" size=\"3\" crc=\"55bc801d\" />" +
            $"<rom name=\"sh_u2.bin\" size=\"4\" crc=\"{secondCrc}\" />" +
            "</game></datafile>");
        var service = new ArcadeCompatibilityService(datPath, Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(spyhunt, "arcade");

        // Both roms in the set are crc32-only; the multiset match must require both, not just one.
        Assert.Equal("arcade", result.RecommendedCore);
        Assert.Equal(["arcade"], result.AvailableCores);
    }

    [Fact]
    public void Resolve_MatchesArchive_WhenDatSha1AttributeIsMixedCase()
    {
        // Hex parsing (ArcadeSha1Digest.Parse) accepts 'A'-'F' and 'a'-'f' alike; a DAT
        // authored with mixed-case hex (common in hand-edited or older DATs) must still match an
        // archive digest that ArcadeArchiveHasher always produces lowercase.
        Directory.CreateDirectory(_root);
        var chip = new byte[] { 10, 20, 30, 40 };
        var lowerHex = Sha1(chip);
        var mixedHex = MixCase(lowerHex);
        var archive = WriteZip("mixedcase.zip", ("chip.bin", chip));
        var service = new ArcadeCompatibilityService(
            WriteDat("fbneo.dat", "mixedcase", chip.Length, mixedHex),
            Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(archive, "arcade");

        Assert.Equal("arcade", result.RecommendedCore);
        Assert.Equal(["arcade"], result.AvailableCores);
    }

    [Fact]
    public void Resolve_DoesNotMatch_WhenDatSha1IsEmpty_EvenWithGenuinelyMatchingCrc()
    {
        // A present-but-empty sha1 attribute routes matching through the sha1 branch only (see
        // ArcadeDatIndexLoader/ArcadeDatIndex comments) and deliberately never falls back to crc32, even
        // when the DAT's own crc attribute is the real crc of the archive entry. This is
        // pre-existing behavior carried over from the prior string-based implementation; it must
        // NOT be "fixed" to fall back to crc32.
        Directory.CreateDirectory(_root);
        var chip = new byte[] { 1, 2, 3 };
        var realCrc = Crc32Hex(chip);
        var archive = WriteZip("emptysha1.zip", ("chip.bin", chip));
        var datPath = Path.Combine(_root, "fbneo.dat");
        File.WriteAllText(
            datPath,
            "<datafile><game name=\"emptysha1\">" +
            $"<rom name=\"chip.bin\" size=\"{chip.Length}\" sha1=\"\" crc=\"{realCrc}\" />" +
            "</game></datafile>");
        var service = new ArcadeCompatibilityService(datPath, Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(archive, "arcade");

        Assert.Empty(result.AvailableCores);
        Assert.Equal("arcade", result.RecommendedCore);
    }

    [Theory]
    [InlineData("odd-length")]
    [InlineData("non-hex-garbage")]
    public void Resolve_DoesNotMatch_WhenDatSha1IsUnparseable(string scenario)
    {
        // Distinct from the empty-sha1 case above: an odd-length or non-hex sha1 also parses to
        // ArcadeSha1Digest.IsValid = false and must be equally unmatchable, via the same mechanism
        // (never indexed, so it can never surface as a probe candidate).
        Directory.CreateDirectory(_root);
        var chip = new byte[] { 1, 2, 3 };
        var validHex = Sha1(chip);
        var badHex = scenario == "odd-length"
            ? validHex[..39]
            : new string('g', 40);
        var archive = WriteZip($"unparseable-{scenario}.zip", ("chip.bin", chip));
        var service = new ArcadeCompatibilityService(
            WriteDat("fbneo.dat", "badsha1", chip.Length, badHex),
            Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(archive, "arcade");

        Assert.Empty(result.AvailableCores);
    }

    [Fact]
    public void Resolve_MatchesEitherSet_WhenSizeAndHashAreSharedAcrossTwoDatSets()
    {
        // A chip shared between two machines (e.g. a bootleg reusing its parent's rom) lands under
        // one (size, hash) key with two DatSets, exercising DatSetGroup's DatSet[] overflow path
        // rather than the single-inline-set path most other tests hit. The bootleg's requirements
        // are a subset of the parent's, so only the bootleg branch can actually succeed here,
        // which forces Matches to have tried more than one candidate from the same group.
        Directory.CreateDirectory(_root);
        var sharedChip = new byte[] { 1, 2, 3, 4 };
        var parentOnlyChip = new byte[] { 9, 9, 9 };
        var sharedHex = Sha1(sharedChip);
        var parentOnlyHex = Sha1(parentOnlyChip);

        var datPath = Path.Combine(_root, "mame.dat");
        File.WriteAllText(
            datPath,
            "<datafile>" +
            "<machine name=\"parent\">" +
            $"<rom name=\"shared.bin\" size=\"{sharedChip.Length}\" sha1=\"{sharedHex}\" />" +
            $"<rom name=\"parentonly.bin\" size=\"{parentOnlyChip.Length}\" sha1=\"{parentOnlyHex}\" />" +
            "</machine>" +
            "<machine name=\"bootleg\">" +
            $"<rom name=\"shared.bin\" size=\"{sharedChip.Length}\" sha1=\"{sharedHex}\" />" +
            "</machine>" +
            "</datafile>");

        // Only the shared chip is present, so only the bootleg's (subset) requirements can match.
        var archive = WriteZip("bootleg.zip", ("shared.bin", sharedChip));
        var service = new ArcadeCompatibilityService(
            Path.Combine(_root, "missing-fbneo.dat"),
            datPath);

        var result = service.Resolve(archive, "arcade");

        Assert.Equal("mame", result.RecommendedCore);
        Assert.Equal(["mame"], result.AvailableCores);
        // Both candidates from the shared-key group must have been probed/compared: proof the
        // DatSet[] overflow path (not just the inline single-set path) actually ran.
        Assert.Equal(2, service.CandidateComparisonCountForTests);
    }

    [Fact]
    public void ArchiveContents_ContentKey_MatchesPinnedGoldenValue()
    {
        // ContentKey is persisted in user core-override preferences (see ArcadeCoreResolution
        // docs). If this computation ever changes, every existing user's override silently
        // orphans. This pins the exact SHA-256 hex output for a fixed, known archive; the
        // expected value was derived by running the current (pre-existing, unmodified) canonical-
        // key computation in ArchiveContents once and hardcoding its output. A failure here means
        // user data compatibility is broken: restore the old computation, do not update this
        // constant to match a new one.
        Directory.CreateDirectory(_root);
        var archive = WriteZip(
            "golden.zip",
            ("a.bin", new byte[] { 1, 2, 3 }),
            ("b.bin", new byte[] { 4, 5, 6, 7 }));

        // An unrelated DAT is enough to make an index available so ResolveAsync hashes the
        // archive's real contents instead of taking the "no DAT installed" FallbackContentKey path.
        var service = new ArcadeCompatibilityService(
            WriteDat("fbneo.dat", "unrelated", 1, Sha1([255])),
            Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(archive, "arcade");

        const string expectedContentKey = "af7f86709d95264ae0d80e8290a13638cda22537427491f091b7098db245df52";
        Assert.Equal(expectedContentKey, result.ContentKey);
    }

    [Fact]
    public void Resolve_PreservesSystemDefault_WhenNoPinnedDatIsInstalled()
    {
        Directory.CreateDirectory(_root);
        var archive = WriteZip("unknown.zip", ("unknown.rom", new byte[] { 7 }));
        var service = new ArcadeCompatibilityService(
            Path.Combine(_root, "missing-fbneo.dat"),
            Path.Combine(_root, "missing-mame.dat"));

        var result = service.Resolve(archive, "arcade");

        Assert.Equal("arcade", result.RecommendedCore);
        Assert.Equal(["arcade"], result.AvailableCores);
    }

    [Fact]
    public void Resolve_CandidateComparisonCount_IsIndependentOfDatSize()
    {
        // Asserts the actual invariant the hash-probe rewrite exists to provide: the number of
        // DatSet candidates that receive a full multiset comparison during a resolve does not
        // grow with DAT size. This is deterministic (a counter, not a clock), so unlike a
        // wall-clock bound it cannot flake on a slow or noisy CI runner. A regression back to a
        // linear scan over every parsed set would make the large-DAT count equal to
        // largeSetCount while the small-DAT count stays near smallSetCount, so this still catches
        // exactly the O(n) regression a timing test was trying to catch.
        Directory.CreateDirectory(_root);
        const int smallSetCount = 200;
        const int largeSetCount = 20_000;
        const int matchingSize = 256;

        var matchingBytes = BuildBytesOfLength(matchingSize, seed: 12345);
        var matchingSha1 = Sha1(matchingBytes);
        var smallDatPath = WriteSyntheticDat("small-mame.dat", smallSetCount, smallSetCount - 1, matchingSha1, matchingSize);
        var largeDatPath = WriteSyntheticDat("large-mame.dat", largeSetCount, largeSetCount - 1, matchingSha1, matchingSize);

        var smallService = new ArcadeCompatibilityService(Path.Combine(_root, "missing-fbneo.dat"), smallDatPath);
        var largeService = new ArcadeCompatibilityService(Path.Combine(_root, "missing-fbneo.dat"), largeDatPath);

        var smallArchive = WriteZip("small-probe.zip", ("rom.bin", matchingBytes));
        var largeArchive = WriteZip("large-probe.zip", ("rom.bin", matchingBytes));

        var smallResult = smallService.Resolve(smallArchive, "arcade");
        var largeResult = largeService.Resolve(largeArchive, "arcade");

        Assert.Equal("mame", smallResult.RecommendedCore);
        Assert.Equal("mame", largeResult.RecommendedCore);
        Assert.Equal(smallService.CandidateComparisonCountForTests, largeService.CandidateComparisonCountForTests);
    }

    [Fact]
    public void Resolve_MatchesSet_WithMixedSha1AndCrc32Requirements_AmongUnrelatedArchiveEntries()
    {
        // The synthetic scale test above only exercises the simplest possible shape: one rom per
        // set, sha1-only, one archive entry. Real DATs mix sha1-only and crc-only rom entries
        // within the same machine, and real arcade ZIPs carry extra chips beyond whatever the DAT
        // requires. This is a correctness regression guard for that messier, more realistic shape
        // against the hash-probe rewrite (candidate collection must union sha1- and crc-derived
        // hits for the SAME set, and DatSet.Matches's full multiset verification must still see
        // the unrelated extra entries and ignore them rather than choke on them).
        Directory.CreateDirectory(_root);
        var sha1Chip = new byte[] { 11, 22, 33 };
        var crcChip = new byte[] { 44, 55, 66, 77 };
        var unrelatedChip = new byte[] { 200, 201 };
        var sha1Hash = Sha1(sha1Chip);
        var crcHash = Crc32Hex(crcChip);

        var datPath = Path.Combine(_root, "mame.dat");
        File.WriteAllText(
            datPath,
            "<datafile><machine name=\"mixedset\">" +
            $"<rom name=\"prog.bin\" size=\"{sha1Chip.Length}\" sha1=\"{sha1Hash}\" />" +
            $"<rom name=\"gfx.bin\" size=\"{crcChip.Length}\" crc=\"{crcHash}\" />" +
            "</machine></datafile>");

        var archive = WriteZip(
            "mixedset.zip",
            ("prog.bin", sha1Chip),
            ("gfx.bin", crcChip),
            ("unrelated.bin", unrelatedChip));

        var service = new ArcadeCompatibilityService(
            Path.Combine(_root, "missing-fbneo.dat"),
            datPath);

        var result = service.Resolve(archive, "arcade");

        Assert.Equal("mame", result.RecommendedCore);
        Assert.Equal(["mame"], result.AvailableCores);
    }

    [Fact]
    public async Task InstallDatAsync_RejectsXmlWithNoSets()
    {
        Directory.CreateDirectory(_root);
        var fbneoPath = WriteDat("fbneo.dat", "spyhunt", 3, Sha1([1, 2, 3]));
        var mamePath = Path.Combine(_root, "missing-mame.dat");
        var originalBytes = File.ReadAllBytes(fbneoPath);
        var service = new ArcadeCompatibilityService(fbneoPath, mamePath);

        // Well-formed XML, but no <game>/<machine> elements at all: ArcadeDatIndexLoader parses it
        // without error yet must still be rejected because it yields zero usable sets.
        var emptyDat = "<datafile><header><name>empty</name></header></datafile>";
        using var upload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(emptyDat));

        await Assert.ThrowsAsync<EmptyArcadeDatException>(
            () => service.InstallDatAsync(ArcadeCompatibilityService.FbneoCore, upload, CancellationToken.None));

        Assert.True(File.Exists(fbneoPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(fbneoPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteZip(string name, params (string Name, byte[] Data)[] entries) =>
        ZipFixtures.WriteZip(_root, name, entries);

    private string WriteDat(string name, string setName, long size, string sha1)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, $"<datafile><game name=\"{setName}\"><rom name=\"chip.bin\" size=\"{size}\" sha1=\"{sha1}\" /></game></datafile>");
        return path;
    }

    private string WriteSyntheticDat(string name, int setCount, int matchingIndex, string matchingSha1, int matchingSize)
    {
        var path = Path.Combine(_root, name);
        using var writer = new StreamWriter(path);
        writer.Write("<datafile>");
        for (var i = 0; i < setCount; i++)
        {
            // Every non-matching set gets a distinct, real-looking sha1 derived from its own
            // index so none of the 20,000 sets collide with each other or with the one deliberate
            // match; only the exercise of the dictionary-probe path over that many sets matters.
            var sha1 = i == matchingIndex ? matchingSha1 : Sha1(BitConverter.GetBytes(i));
            var size = i == matchingIndex ? matchingSize : 1000 + i;
            writer.Write($"<machine name=\"set{i}\"><rom name=\"chip.bin\" size=\"{size}\" sha1=\"{sha1}\" /></machine>");
        }

        writer.Write("</datafile>");
        return path;
    }

    private static byte[] BuildBytesOfLength(int length, int seed)
    {
        var bytes = new byte[length];
        var value = (byte)(seed % 256);
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((value + i) % 256);
        }

        return bytes;
    }

    private static string Sha1(byte[] value) => Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();

    /// <summary>Alternates the case of each hex character so a lowercase digest becomes mixed-case.</summary>
    private static string MixCase(string lowerHex)
    {
        var chars = lowerHex.ToCharArray();
        for (var i = 0; i < chars.Length; i += 2)
        {
            chars[i] = char.ToUpperInvariant(chars[i]);
        }

        return new string(chars);
    }

    /// <summary>
    /// Standard zlib/ISO CRC-32 (same polynomial and reflect/invert convention the production
    /// <c>ArcadeArchiveHasher</c> uses), reimplemented here so mixed-requirement
    /// test fixtures can embed a real crc value rather than a guessed literal.
    /// </summary>
    private static string Crc32Hex(byte[] value)
    {
        var crc = 0xffffffffu;
        foreach (var b in value)
        {
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xff];
        }

        return (~crc).ToString("x8");
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }

            table[value] = crc;
        }

        return table;
    }
}
