namespace Moonfin.Server.Services;

/// <summary>Immutable hash-probe index for one installed arcade DAT snapshot.</summary>
internal sealed class ArcadeDatIndex
{
    private readonly Dictionary<(long Size, ArcadeSha1Digest Hash), ArcadeDatSetGroup> _bySha1;
    private readonly Dictionary<(long Size, uint Hash), ArcadeDatSetGroup> _byCrc32;

    public ArcadeDatIndex(List<ArcadeDatSet> sets)
    {
        SetCount = sets.Count;
        var bySha1 = new Dictionary<(long, ArcadeSha1Digest), List<ArcadeDatSet>>();
        var byCrc32 = new Dictionary<(long, uint), List<ArcadeDatSet>>();

        foreach (var set in sets)
        {
            foreach (var requirement in set.Requirements)
            {
                if (requirement.Sha1.HasValue)
                {
                    if (requirement.Sha1.Value.IsValid)
                    {
                        AddCandidate(bySha1, (requirement.Size, requirement.Sha1.Value), set);
                    }
                }
                else if (requirement.Crc32.HasValue)
                {
                    AddCandidate(byCrc32, (requirement.Size, requirement.Crc32.Value), set);
                }
            }
        }

        _bySha1 = ToGroups(bySha1);
        _byCrc32 = ToGroups(byCrc32);
    }

    public int SetCount { get; }

    public bool Matches(ArcadeArchiveContents contents, out int candidatesCompared)
    {
        HashSet<ArcadeDatSet>? candidates = null;
        foreach (var entry in contents.Entries)
        {
            if (_bySha1.TryGetValue((entry.Length, entry.Sha1), out var sha1Group))
            {
                sha1Group.UnionInto(candidates ??= []);
            }

            if (_byCrc32.TryGetValue((entry.Length, entry.Crc32), out var crc32Group))
            {
                crc32Group.UnionInto(candidates ??= []);
            }
        }

        if (candidates == null)
        {
            candidatesCompared = 0;
            return false;
        }

        candidatesCompared = candidates.Count;
        return candidates.Any(candidate => candidate.Matches(contents));
    }

    private static void AddCandidate<TKey>(
        Dictionary<TKey, List<ArcadeDatSet>> index,
        TKey key,
        ArcadeDatSet set)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (list.Count == 0 || !ReferenceEquals(list[^1], set))
        {
            list.Add(set);
        }
    }

    private static Dictionary<TKey, ArcadeDatSetGroup> ToGroups<TKey>(
        Dictionary<TKey, List<ArcadeDatSet>> index)
        where TKey : notnull
    {
        var groups = new Dictionary<TKey, ArcadeDatSetGroup>(index.Count);
        foreach (var (key, list) in index)
        {
            groups[key] = list.Count == 1
                ? new ArcadeDatSetGroup(list[0])
                : new ArcadeDatSetGroup(list.ToArray());
        }

        return groups;
    }
}

internal sealed record ArcadeDatSet(ArcadeDatRom[] Requirements)
{
    public bool Matches(ArcadeArchiveContents contents)
    {
        var remaining = contents.Entries.ToList();
        foreach (var required in Requirements)
        {
            var index = remaining.FindIndex(entry => entry.Length == required.Size &&
                (required.Sha1.HasValue
                    ? entry.Sha1.Equals(required.Sha1.Value)
                    : required.Crc32.HasValue && entry.Crc32 == required.Crc32.Value));
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return true;
    }
}

internal readonly struct ArcadeDatSetGroup
{
    private readonly ArcadeDatSet? _single;
    private readonly ArcadeDatSet[]? _multiple;

    public ArcadeDatSetGroup(ArcadeDatSet single)
    {
        _single = single;
        _multiple = null;
    }

    public ArcadeDatSetGroup(ArcadeDatSet[] multiple)
    {
        _single = null;
        _multiple = multiple;
    }

    public void UnionInto(HashSet<ArcadeDatSet> candidates)
    {
        if (_multiple is { } multiple)
        {
            candidates.UnionWith(multiple);
        }
        else if (_single is { } single)
        {
            candidates.Add(single);
        }
    }
}

internal readonly record struct ArcadeDatRom(
    long Size,
    ArcadeSha1Digest? Sha1,
    uint? Crc32);
