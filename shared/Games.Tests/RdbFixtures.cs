using System.Buffers.Binary;
using System.Text;

namespace Moonfin.Games.Tests;

/// <summary>
/// Writes the subset of MessagePack that libretro emits, so the reader can be driven without
/// checking a real database into the repo.
/// </summary>
internal sealed class MsgPack
{
    private readonly List<byte> _bytes = new();

    public byte[] ToArray() => _bytes.ToArray();

    public MsgPack FixMap(int pairs)
    {
        _bytes.Add((byte)(0x80 | pairs));
        return this;
    }

    public MsgPack Str(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length < 32)
        {
            _bytes.Add((byte)(0xA0 | utf8.Length));
        }
        else
        {
            _bytes.Add(0xD9);
            _bytes.Add((byte)utf8.Length);
        }

        _bytes.AddRange(utf8);
        return this;
    }

    /// <summary>Writes the narrowest encoding that fits, the way a real writer would.</summary>
    public MsgPack Int(long value)
    {
        if (value >= 0 && value <= 0x7F)
        {
            _bytes.Add((byte)value);
        }
        else if (value >= 0 && value <= byte.MaxValue)
        {
            _bytes.Add(0xCC);
            _bytes.Add((byte)value);
        }
        else if (value >= 0 && value <= ushort.MaxValue)
        {
            _bytes.Add(0xCD);
            var buf = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value);
            _bytes.AddRange(buf);
        }
        else
        {
            _bytes.Add(0xD3);
            var buf = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buf, value);
            _bytes.AddRange(buf);
        }

        return this;
    }

    public MsgPack Bin(params byte[] data)
    {
        _bytes.Add(0xC4);
        _bytes.Add((byte)data.Length);
        _bytes.AddRange(data);
        return this;
    }

    public MsgPack Bool(bool value)
    {
        _bytes.Add(value ? (byte)0xC3 : (byte)0xC2);
        return this;
    }
}

internal static class RdbFixtures
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RARCHDB\0");

    /// <summary>
    /// Magic, a big-endian offset to the trailing metadata block, then one record per map. The
    /// offset is placed just past the records unless <paramref name="metadataOffset"/> says
    /// otherwise, which is how the truncation cases are built.
    /// </summary>
    public static string Write(string path, IEnumerable<byte[]> records, ulong? metadataOffset = null)
    {
        var body = records.SelectMany(r => r).ToArray();
        var offset = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(offset, metadataOffset ?? (ulong)(16 + body.Length));

        // Trailing bytes stand in for the metadata block a real file carries.
        var file = Magic.Concat(offset).Concat(body).Concat(new byte[] { 0xC0, 0xC0 }).ToArray();
        File.WriteAllBytes(path, file);
        return path;
    }

    public static byte[] Game(string name, uint? crc = null, int? year = null)
    {
        var pairs = 1 + (crc.HasValue ? 1 : 0) + (year.HasValue ? 1 : 0);
        var pack = new MsgPack().FixMap(pairs).Str("name").Str(name);

        if (crc.HasValue)
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, crc.Value);
            pack.Str("crc").Bin(buf);
        }

        if (year.HasValue)
        {
            pack.Str("releaseyear").Int(year.Value);
        }

        return pack.ToArray();
    }
}
