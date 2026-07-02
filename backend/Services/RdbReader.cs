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

    public static IReadOnlyList<RdbRecord> ReadAll(string path)
    {
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
        var c = b[pos++];

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

            case 0xcc: return (long)b[pos++];
            case 0xcd: { var v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return (long)v; }
            case 0xce: { var v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return (long)v; }
            case 0xcf: { var v = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(pos, 8)); pos += 8; return (long)v; }

            case 0xd0: return (long)(sbyte)b[pos++];
            case 0xd1: { var v = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return (long)v; }
            case 0xd2: { var v = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return (long)v; }
            case 0xd3: { var v = BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(pos, 8)); pos += 8; return v; }

            case 0xd9: { int len = b[pos++]; return ReadString(b, ref pos, len); }
            case 0xda: { int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return ReadString(b, ref pos, len); }
            case 0xdb: { int len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return ReadString(b, ref pos, len); }

            case 0xc4: { int len = b[pos++]; return ReadBin(b, ref pos, len); }
            case 0xc5: { int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return ReadBin(b, ref pos, len); }
            case 0xc6: { int len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return ReadBin(b, ref pos, len); }

            case 0xde: { int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return ReadMap(b, ref pos, n); }
            case 0xdf: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return ReadMap(b, ref pos, n); }

            case 0xdc: { int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2)); pos += 2; return ReadArray(b, ref pos, n); }
            case 0xdd: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos, 4)); pos += 4; return ReadArray(b, ref pos, n); }

            default: return null;
        }
    }

    private static string ReadString(byte[] b, ref int pos, int len)
    {
        var s = Encoding.UTF8.GetString(b, pos, len);
        pos += len;
        return s;
    }

    private static byte[] ReadBin(byte[] b, ref int pos, int len)
    {
        var slice = new byte[len];
        Array.Copy(b, pos, slice, 0, len);
        pos += len;
        return slice;
    }

    private static Dictionary<string, object?> ReadMap(byte[] b, ref int pos, int count)
    {
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
        var list = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(ReadValue(b, ref pos));
        }

        return list;
    }
}
