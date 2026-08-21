using System.Buffers.Binary;
using System.Text;

namespace Moonfin.Server.Services;

/// <summary>One game record parsed from a libretro <c>.rdb</c> database.</summary>
public sealed class RdbRecord
{
    public uint? Crc { get; set; }
    public string? Name { get; set; }
    public string? RomName { get; set; }
    public string? Genre { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? Franchise { get; set; }
    public string? Region { get; set; }
    public string? Description { get; set; }
    public int? ReleaseYear { get; set; }
    public int? ReleaseMonth { get; set; }
    public int? Users { get; set; }
}

/// <summary>
/// Reads libretro-database <c>.rdb</c> files. The format is an 8-byte magic ("RARCHDB\0"),
/// a big-endian uint64 offset to the trailing metadata block, then a sequence of MessagePack
/// maps (one per game) up to that offset. Only the MessagePack subset libretro emits is
/// handled. Self-contained (no third-party assemblies, which Jellyfin's plugin load context
/// cannot reliably resolve).
/// </summary>
public static class RdbReader
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RARCHDB\0");

    // Ceiling on a single .rdb metadata file read from local disk, mirroring
    // GamesService.MaxExtractedRomBytes's role for ROM extraction: an explicit, documented limit
    // rather than an unbounded File.ReadAllBytes. Real libretro-database .rdb files (even the
    // largest, e.g. MAME) are a few MB; this is a generous ceiling for those while still bounding
    // worst-case memory for a corrupt/oversized file or a misconfigured mirror (see
    // RdbIndexStore.DownloadAsync, which enforces the same ceiling while downloading).
    public const long MaxRdbFileBytes = 64L * 1024 * 1024;

    // maxFileBytes defaults to MaxRdbFileBytes; the parameter exists so a focused test can drive
    // the cap with a small file instead of needing to write a multi-megabyte fixture just to
    // exercise this comparison (mirroring ArcadeArchiveHasher.Read's maxEntryBytes seam).
    public static IReadOnlyList<RdbRecord> ReadAll(string path, long maxFileBytes = MaxRdbFileBytes)
    {
        // FileInfo.Length is OS-reported truth (unlike a zip entry's attacker-controlled header),
        // so checking it before reading is both the fast pre-check and the real enforcement here.
        var fileLength = new FileInfo(path).Length;
        if (fileLength > maxFileBytes)
        {
            throw new RdbTooLargeException(fileLength, maxFileBytes);
        }

        var bytes = File.ReadAllBytes(path);
        var records = new List<RdbRecord>();

        if (bytes.Length < 16)
        {
            return records;
        }

        for (var i = 0; i < Magic.Length; i++)
        {
            if (bytes[i] != Magic[i])
            {
                return records;
            }
        }

        var metadataOffset = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8));
        var end = (int)Math.Min(metadataOffset == 0 ? (ulong)bytes.Length : metadataOffset, (ulong)bytes.Length);

        var pos = 16;
        while (pos < end)
        {
            var value = ReadValue(bytes, ref pos);
            if (value is Dictionary<string, object?> map)
            {
                records.Add(ToRecord(map));
            }
            else if (value == null && pos >= end)
            {
                break;
            }
        }

        return records;
    }

    private static RdbRecord ToRecord(Dictionary<string, object?> map)
    {
        var record = new RdbRecord
        {
            Name = AsString(map, "name"),
            RomName = AsString(map, "rom_name"),
            Genre = AsString(map, "genre"),
            Developer = AsString(map, "developer"),
            Publisher = AsString(map, "publisher"),
            Franchise = AsString(map, "franchise"),
            Region = AsString(map, "region"),
            Description = AsString(map, "description"),
            ReleaseYear = AsInt(map, "releaseyear"),
            ReleaseMonth = AsInt(map, "releasemonth"),
            Users = AsInt(map, "users"),
        };

        if (map.TryGetValue("crc", out var crc) && crc is byte[] { Length: 4 } b)
        {
            record.Crc = BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        return record;
    }

    private static string? AsString(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    private static int? AsInt(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var v) && v is long l ? (int)l : null;

    private static object? ReadValue(byte[] b, ref int pos)
    {
        var c = ReadByteChecked(b, ref pos);

        // positive / negative fixint
        if (c <= 0x7f) return (long)c;
        if (c >= 0xe0) return (long)(sbyte)c;

        // fixstr
        if (c >= 0xa0 && c <= 0xbf) return ReadString(b, ref pos, c & 0x1f);

        // fixmap
        if (c >= 0x80 && c <= 0x8f) return ReadMap(b, ref pos, c & 0x0f);

        // fixarray
        if (c >= 0x90 && c <= 0x9f) return ReadArray(b, ref pos, c & 0x0f);

        switch (c)
        {
            case 0xc0: return null;
            case 0xc2: return false;
            case 0xc3: return true;

            case 0xcc: return (long)ReadByteChecked(b, ref pos);
            case 0xcd: return (long)BinaryPrimitives.ReadUInt16BigEndian(ReadSpanChecked(b, ref pos, 2));
            case 0xce: return (long)BinaryPrimitives.ReadUInt32BigEndian(ReadSpanChecked(b, ref pos, 4));
            case 0xcf: return (long)BinaryPrimitives.ReadUInt64BigEndian(ReadSpanChecked(b, ref pos, 8));

            case 0xd0: return (long)(sbyte)ReadByteChecked(b, ref pos);
            case 0xd1: return (long)BinaryPrimitives.ReadInt16BigEndian(ReadSpanChecked(b, ref pos, 2));
            case 0xd2: return (long)BinaryPrimitives.ReadInt32BigEndian(ReadSpanChecked(b, ref pos, 4));
            case 0xd3: return BinaryPrimitives.ReadInt64BigEndian(ReadSpanChecked(b, ref pos, 8));

            case 0xd9: { int len = ReadByteChecked(b, ref pos); return ReadString(b, ref pos, len); }
            case 0xda: { int len = BinaryPrimitives.ReadUInt16BigEndian(ReadSpanChecked(b, ref pos, 2)); return ReadString(b, ref pos, len); }
            case 0xdb: { int len = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpanChecked(b, ref pos, 4)); return ReadString(b, ref pos, len); }

            case 0xc4: { int len = ReadByteChecked(b, ref pos); return ReadBin(b, ref pos, len); }
            case 0xc5: { int len = BinaryPrimitives.ReadUInt16BigEndian(ReadSpanChecked(b, ref pos, 2)); return ReadBin(b, ref pos, len); }
            case 0xc6: { int len = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpanChecked(b, ref pos, 4)); return ReadBin(b, ref pos, len); }

            case 0xde: { int n = BinaryPrimitives.ReadUInt16BigEndian(ReadSpanChecked(b, ref pos, 2)); return ReadMap(b, ref pos, n); }
            case 0xdf: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpanChecked(b, ref pos, 4)); return ReadMap(b, ref pos, n); }

            case 0xdc: { int n = BinaryPrimitives.ReadUInt16BigEndian(ReadSpanChecked(b, ref pos, 2)); return ReadArray(b, ref pos, n); }
            case 0xdd: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(ReadSpanChecked(b, ref pos, 4)); return ReadArray(b, ref pos, n); }

            default: return null;
        }
    }

    // Bounds-checked replacement for a bare `b[pos++]`. A truncated/malformed .rdb file must
    // yield a clean FormatException here rather than an IndexOutOfRangeException from arbitrary
    // offsets deeper in the switch above.
    private static byte ReadByteChecked(byte[] b, ref int pos)
    {
        if (pos >= b.Length)
        {
            throw new FormatException("Truncated .rdb file: expected another byte but reached end of data.");
        }

        return b[pos++];
    }

    // Bounds-checked replacement for a bare `b.AsSpan(pos, len)`. Uses long arithmetic for the
    // bounds check so a huge attacker-controlled len (e.g. a 32-bit length field near uint.MaxValue)
    // cannot wrap pos + len back into range via int overflow.
    private static ReadOnlySpan<byte> ReadSpanChecked(byte[] b, ref int pos, int len)
    {
        if (len < 0 || (long)pos + len > b.Length)
        {
            throw new FormatException(
                $"Truncated .rdb file: expected {len} more bytes at offset {pos} but only {b.Length - pos} remain.");
        }

        var span = new ReadOnlySpan<byte>(b, pos, len);
        pos += len;
        return span;
    }

    private static string ReadString(byte[] b, ref int pos, int len) =>
        Encoding.UTF8.GetString(ReadSpanChecked(b, ref pos, len));

    private static byte[] ReadBin(byte[] b, ref int pos, int len) =>
        ReadSpanChecked(b, ref pos, len).ToArray();

    private static Dictionary<string, object?> ReadMap(byte[] b, ref int pos, int count)
    {
        if (count < 0)
        {
            throw new FormatException($"Malformed .rdb file: negative map entry count ({count}).");
        }

        var map = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadValue(b, ref pos);
            var value = ReadValue(b, ref pos);
            if (key is string k)
            {
                map[k] = value;
            }
        }

        return map;
    }

    private static List<object?> ReadArray(byte[] b, ref int pos, int count)
    {
        if (count < 0)
        {
            throw new FormatException($"Malformed .rdb file: negative array entry count ({count}).");
        }

        var list = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(ReadValue(b, ref pos));
        }

        return list;
    }
}

/// <summary>
/// Thrown when a <c>.rdb</c> metadata file (read from local disk or downloaded from the
/// admin-configured mirror) exceeds <see cref="RdbReader.MaxRdbFileBytes"/>. Kept as a distinct
/// type (rather than a bare <see cref="IOException"/>) so <see cref="RdbIndexStore"/> can tell a
/// too-large file apart from other, transient I/O failures (e.g. a file briefly locked by a
/// concurrent download) and apply the corrupt-file backoff only to genuine format problems.
/// </summary>
public sealed class RdbTooLargeException : IOException
{
    public RdbTooLargeException(long actualBytes, long maxBytes)
        : base($".rdb file is {actualBytes} bytes, exceeding the {maxBytes} byte limit.")
    {
        ActualBytes = actualBytes;
        MaxBytes = maxBytes;
    }

    /// <summary>The file's advertised or actual size (in bytes) that triggered the limit.</summary>
    public long ActualBytes { get; }

    /// <summary>The configured ceiling (in bytes), i.e. <see cref="RdbReader.MaxRdbFileBytes"/>.</summary>
    public long MaxBytes { get; }
}
