using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Moonfin.Server.Services;

/// <summary>Reads an arcade ZIP and computes all hashes needed by DAT matching in one pass.</summary>
internal static class ArcadeArchiveHasher
{
    private static readonly uint[] Crc32Table = CreateCrc32Table();

    // Ceiling on a single decompressed archive entry while hashing, mirroring
    // GamesService.MaxExtractedRomBytes for the sibling ROM-extraction path. This method runs on
    // every arcade detail/thumbnail resolution (ArcadeCompatibilityService.ResolveAsync), so an
    // entry with a high compression ratio (a "zip bomb") would otherwise tie up a thread-pool
    // worker streaming an unbounded amount of decompressed data through SHA-1/CRC32 with no cap.
    public const long MaxHashedEntryBytes = 512L * 1024 * 1024;

    public static ArcadeArchiveContents Read(
        string archivePath,
        CancellationToken cancellationToken,
        long maxEntryBytes = MaxHashedEntryBytes)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new List<ArcadeArchiveRom>();
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // The zip central directory's uncompressed-size field is attacker-controlled data (a
            // crafted/corrupt archive can lie), so it is only a fast pre-check. HashEntry below
            // enforces the real ceiling against actual decompressed bytes as they stream through.
            if (entry.Length > maxEntryBytes)
            {
                throw new RomTooLargeException(entry.Length, maxEntryBytes);
            }

            using var stream = entry.Open();
            var hash = HashEntry(stream, cancellationToken, maxEntryBytes);
            entries.Add(new ArcadeArchiveRom(entry.Length, hash.Sha1Hex, hash.Sha1, hash.Crc32));
        }

        return new ArcadeArchiveContents(entries);
    }

    private static (string Sha1Hex, ArcadeSha1Digest Sha1, uint Crc32) HashEntry(
        Stream stream,
        CancellationToken cancellationToken,
        long maxEntryBytes)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var crc = 0xffffffffu;
        var buffer = new byte[81920];
        long total = 0;
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += bytesRead;
            if (total > maxEntryBytes)
            {
                throw new RomTooLargeException(total, maxEntryBytes);
            }

            sha1.AppendData(buffer, 0, bytesRead);
            for (var index = 0; index < bytesRead; index++)
            {
                crc = (crc >> 8) ^ Crc32Table[(crc ^ buffer[index]) & 0xff];
            }
        }

        var hashBytes = sha1.GetHashAndReset();
        return (
            Convert.ToHexString(hashBytes).ToLowerInvariant(),
            ArcadeSha1Digest.FromHash(hashBytes),
            ~crc);
    }

    private static uint[] CreateCrc32Table()
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

internal sealed class ArcadeArchiveContents
{
    public ArcadeArchiveContents(List<ArcadeArchiveRom> entries)
    {
        Entries = entries;
        var canonical = string.Join("\n", entries
            .OrderBy(entry => entry.Length)
            .ThenBy(entry => entry.Sha1Hex, StringComparer.Ordinal)
            .Select(entry => $"{entry.Length}:{entry.Sha1Hex}"));
        ContentKey = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public List<ArcadeArchiveRom> Entries { get; }

    public string ContentKey { get; }
}

internal readonly record struct ArcadeArchiveRom(
    long Length,
    string Sha1Hex,
    ArcadeSha1Digest Sha1,
    uint Crc32);

/// <summary>A compact parsed 20-byte SHA-1 digest used as an index key.</summary>
internal readonly record struct ArcadeSha1Digest(bool IsValid, ulong Hi, ulong Mid, uint Lo)
{
    public static ArcadeSha1Digest Parse(string hex)
    {
        if (hex.Length != 40)
        {
            return default;
        }

        Span<byte> bytes = stackalloc byte[20];
        return TryDecodeHex(hex, bytes) ? FromHash(bytes) : default;
    }

    public static ArcadeSha1Digest FromHash(ReadOnlySpan<byte> hash) =>
        new(
            true,
            BinaryPrimitives.ReadUInt64BigEndian(hash),
            BinaryPrimitives.ReadUInt64BigEndian(hash[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(hash[16..]));

    private static bool TryDecodeHex(ReadOnlySpan<char> hex, Span<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = HexNibble(hex[i * 2]);
            var lo = HexNibble(hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0)
            {
                return false;
            }

            bytes[i] = (byte)((hi << 4) | lo);
        }

        return true;
    }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
