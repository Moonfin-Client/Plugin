using System.Globalization;
using System.Text;

namespace Moonfin.Server.Services;

/// <summary>
/// Applies deterministic ROM-to-RDB matching rules to an already-built platform index.
/// </summary>
internal sealed class RdbMatcher
{
    internal RdbRecord? Match(RdbPlatformIndex index, string romPath, string? title)
    {
        foreach (var crc in ComputeCrcCandidates(romPath))
        {
            if (index.ByCrc.TryGetValue(crc, out var byCrc))
            {
                return byCrc;
            }
        }

        var fileName = NormalizeName(Path.GetFileNameWithoutExtension(romPath));
        if (fileName.Length > 0 && index.ByName.TryGetValue(fileName, out var byFile))
        {
            return byFile;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            var norm = NormalizeName(title);
            if (norm.Length > 0 && index.ByName.TryGetValue(norm, out var byTitle))
            {
                return byTitle;
            }
        }

        if (fileName.Length >= 5)
        {
            RdbRecord? bestRecord = null;
            var minDiff = int.MaxValue;
            var ambiguous = false;
            foreach (var kv in index.ByName)
            {
                if (kv.Key.StartsWith(fileName, StringComparison.Ordinal) ||
                    fileName.StartsWith(kv.Key, StringComparison.Ordinal))
                {
                    var diff = Math.Abs(kv.Key.Length - fileName.Length);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestRecord = kv.Value;
                        ambiguous = false;
                    }
                    else if (diff == minDiff && !ReferenceEquals(kv.Value, bestRecord))
                    {
                        ambiguous = true;
                    }
                }
            }

            if (bestRecord != null && !ambiguous && minDiff <= 45)
            {
                return bestRecord;
            }
        }

        return null;
    }

    internal List<RdbRecord> MatchWithSiblings(RdbPlatformIndex index, string romPath, string? title, int maxSiblings)
    {
        var primary = Match(index, romPath, title);
        var results = new List<RdbRecord>(1 + maxSiblings);
        if (primary == null || string.IsNullOrWhiteSpace(primary.Name))
        {
            if (primary != null)
            {
                results.Add(primary);
            }

            return results;
        }

        results.Add(primary);
        var baseTitle = NormalizeName(ExtractBaseTitle(primary.Name));
        if (baseTitle.Length < 5)
        {
            return results;
        }

        var seenRecords = new HashSet<RdbRecord>(ReferenceEqualityComparer.Instance) { primary };
        var candidates = new List<(RdbRecord Record, int Diff)>();
        foreach (var kv in index.ByName)
        {
            var candidate = kv.Value;
            if (!seenRecords.Add(candidate) || string.IsNullOrWhiteSpace(candidate.Name))
            {
                continue;
            }

            var candidateBaseTitle = NormalizeName(ExtractBaseTitle(candidate.Name));
            if (candidateBaseTitle.Length == 0)
            {
                continue;
            }

            if (candidateBaseTitle.StartsWith(baseTitle, StringComparison.Ordinal) ||
                baseTitle.StartsWith(candidateBaseTitle, StringComparison.Ordinal))
            {
                var diff = Math.Abs(candidateBaseTitle.Length - baseTitle.Length);
                if (diff <= 45)
                {
                    candidates.Add((candidate, diff));
                }
            }
        }

        candidates.Sort((a, b) => a.Diff.CompareTo(b.Diff));
        for (var i = 0; i < candidates.Count && results.Count < 1 + maxSiblings; i++)
        {
            results.Add(candidates[i].Record);
        }

        return results;
    }

    internal static RdbRecord? MatchByCrc(RdbPlatformIndex index, IReadOnlyList<uint> crcCandidates)
    {
        foreach (var crc in crcCandidates)
        {
            if (index.ByCrc.TryGetValue(crc, out var record))
            {
                return record;
            }
        }

        return null;
    }

    internal static IReadOnlyList<uint> ComputeCrcCandidates(string romPath)
    {
        var candidates = new List<uint>(2);
        try
        {
            candidates.Add(Crc32File(romPath, 0));
            if (HasInesHeader(romPath))
            {
                candidates.Add(Crc32File(romPath, 16));
            }
        }
        catch
        {
            // Preserve the optional metadata lookup behavior when a ROM cannot be read.
        }

        return candidates;
    }

    internal static string ExtractBaseTitle(string name)
    {
        var result = name;
        while (true)
        {
            var trimmedEnd = result.TrimEnd();
            if (trimmedEnd.Length == 0 || trimmedEnd[^1] != ')')
            {
                result = trimmedEnd;
                break;
            }

            var openIndex = trimmedEnd.LastIndexOf('(');
            if (openIndex < 0)
            {
                result = trimmedEnd;
                break;
            }

            result = trimmedEnd.Substring(0, openIndex);
        }

        return result.TrimEnd();
    }

    internal static string NormalizeName(string value)
    {
        var normalizedString = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(value.Length);
        foreach (var ch in normalizedString)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static bool HasInesHeader(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Length > 16 && fs.Read(head) == 4 &&
            head[0] == (byte)'N' && head[1] == (byte)'E' && head[2] == (byte)'S' && head[3] == 0x1A;
    }

    private static uint Crc32File(string path, int skip)
    {
        var crc = 0xFFFFFFFFu;
        using var fs = File.OpenRead(path);
        if (skip > 0)
        {
            fs.Seek(skip, SeekOrigin.Begin);
        }

        var buffer = new byte[65536];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

internal sealed record RdbPlatformIndex(
    IReadOnlyDictionary<uint, RdbRecord> ByCrc,
    IReadOnlyDictionary<string, RdbRecord> ByName);
