using System.Globalization;
using System.Xml;

namespace Moonfin.Server.Services;

/// <summary>Streams an arcade DAT snapshot into its compact immutable match index.</summary>
internal static class ArcadeDatIndexLoader
{
    public static ArcadeDatIndex Load(string path, CancellationToken cancellationToken = default)
    {
        var sets = new List<ArcadeDatSet>();
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
        };

        using var reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("game" or "machine"))
            {
                continue;
            }

            var set = ParseSet(reader, cancellationToken);
            if (set.Requirements.Length > 0)
            {
                sets.Add(set);
            }
        }

        return new ArcadeDatIndex(sets);
    }

    private static ArcadeDatSet ParseSet(XmlReader reader, CancellationToken cancellationToken)
    {
        var requirements = new List<ArcadeDatRom>();
        if (reader.IsEmptyElement)
        {
            return new ArcadeDatSet([]);
        }

        var setDepth = reader.Depth;
        while (reader.Read() && reader.Depth > setDepth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element ||
                reader.Depth != setDepth + 1 ||
                reader.LocalName != "rom")
            {
                continue;
            }

            if (string.Equals(reader.GetAttribute("status"), "nodump", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = ParseLong(reader.GetAttribute("size"));
            var sha1Raw = reader.GetAttribute("sha1")?.Trim().ToLowerInvariant();
            var crc32Raw = reader.GetAttribute("crc")?.Trim().ToLowerInvariant();
            if (size < 0 || (string.IsNullOrEmpty(sha1Raw) && string.IsNullOrEmpty(crc32Raw)))
            {
                continue;
            }

            ArcadeSha1Digest? sha1 = sha1Raw != null ? ArcadeSha1Digest.Parse(sha1Raw) : null;
            uint? crc32 = crc32Raw != null && TryParseCrc32(crc32Raw, out var parsedCrc)
                ? parsedCrc
                : null;
            requirements.Add(new ArcadeDatRom(size, sha1, crc32));
        }

        return new ArcadeDatSet([.. requirements]);
    }

    private static long ParseLong(string? value) => long.TryParse(value, out var parsed) ? parsed : -1;

    private static bool TryParseCrc32(string hex, out uint value)
    {
        if (hex.Length == 8)
        {
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
    }
}
