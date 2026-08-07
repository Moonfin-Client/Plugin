using System.Security.Cryptography;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class ArcadeDatComponentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-arcade-components-{Guid.NewGuid():N}");

    [Fact]
    public void LoaderAndMatcher_ValidateHashedArchiveContents()
    {
        Directory.CreateDirectory(_root);
        var chip = new byte[] { 1, 3, 3, 7 };
        var datPath = Path.Combine(_root, "arcade.dat");
        var sha1 = Convert.ToHexString(SHA1.HashData(chip)).ToLowerInvariant();
        File.WriteAllText(
            datPath,
            $"<datafile><game name=\"fixture\"><rom name=\"chip.bin\" size=\"{chip.Length}\" sha1=\"{sha1}\" /></game></datafile>");
        var zipPath = WriteZip("fixture.zip", ("chip.bin", chip));

        var index = ArcadeDatIndexLoader.Load(datPath);
        var contents = ArcadeArchiveHasher.Read(zipPath, CancellationToken.None);

        Assert.Equal(1, index.SetCount);
        Assert.True(index.Matches(contents, out var candidatesCompared));
        Assert.Equal(1, candidatesCompared);
    }

    [Fact]
    public void Loader_ExcludesNoDumpAndHashlessSets()
    {
        Directory.CreateDirectory(_root);
        var datPath = Path.Combine(_root, "empty.dat");
        File.WriteAllText(
            datPath,
            "<datafile>" +
            "<game name=\"nodump\"><rom name=\"chip.bin\" size=\"4\" status=\"nodump\" /></game>" +
            "<machine name=\"hashless\"><rom name=\"chip.bin\" size=\"4\" /></machine>" +
            "</datafile>");

        var index = ArcadeDatIndexLoader.Load(datPath);

        Assert.Equal(0, index.SetCount);
    }

    // Regression guard for a high-compression-ratio archive ("zip bomb") tying up a thread-pool
    // worker: an entry that decompresses far past the sibling ROM-extraction path's
    // GamesService.MaxExtractedRomBytes ceiling must be rejected while streaming, not hashed to
    // completion. A real zip bomb fixture would be enormous; this test instead points
    // ArcadeArchiveHasher at a tiny cap via the internal test seam so the same enforcement logic
    // is exercised without writing gigabytes to disk.
    [Fact]
    public void ArchiveHasher_EntryExceedingTheCap_ThrowsRomTooLargeException()
    {
        Directory.CreateDirectory(_root);
        // 10,000 highly-compressible bytes -- trivial to store in a zip, but well past a small
        // test cap, standing in for a high-ratio archive without needing a real multi-GB bomb.
        var chip = new byte[10_000];
        var zipPath = WriteZip("oversized.zip", ("chip.bin", chip));

        var ex = Assert.Throws<RomTooLargeException>(
            () => ArcadeArchiveHasher.Read(zipPath, CancellationToken.None, maxEntryBytes: 1_000));

        Assert.Equal(1_000, ex.MaxBytes);
    }

    [Fact]
    public void ArchiveHasher_EntryWithinTheDefaultCap_ReadsNormally()
    {
        Directory.CreateDirectory(_root);
        var chip = new byte[] { 1, 3, 3, 7 };
        var zipPath = WriteZip("normal.zip", ("chip.bin", chip));

        var contents = ArcadeArchiveHasher.Read(zipPath, CancellationToken.None);

        Assert.Single(contents.Entries);
    }

    [Fact]
    public void ArchiveHasher_ContentKeyDoesNotDependOnZipEntryOrder()
    {
        Directory.CreateDirectory(_root);
        var first = WriteZip(
            "first.zip",
            ("one.bin", new byte[] { 1, 2 }),
            ("two.bin", new byte[] { 3, 4, 5 }));
        var second = WriteZip(
            "second.zip",
            ("two.bin", new byte[] { 3, 4, 5 }),
            ("one.bin", new byte[] { 1, 2 }));

        var firstContents = ArcadeArchiveHasher.Read(first, CancellationToken.None);
        var secondContents = ArcadeArchiveHasher.Read(second, CancellationToken.None);

        Assert.Equal(firstContents.ContentKey, secondContents.ContentKey);
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
}
