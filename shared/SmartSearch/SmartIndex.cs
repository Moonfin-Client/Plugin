using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Moonfin.SmartSearch;

/// <summary>One searchable thing: a library item, a person, or a TMDB title for Seerr.</summary>
public sealed class SmartEntry
{
    /// <summary>Library item id, "person:" plus a name, or "movie:123" and "tv:123" for catalog titles.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>One of the SmartQuery kind names.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>First and last year the entry covers, 0 when unknown.</summary>
    public int YearMin { get; set; }

    public int YearMax { get; set; }

    /// <summary>
    /// Release date as yyyymmdd, 0 when unknown. Only catalog titles have one. For a movie it's the
    /// first theatrical or digital release, for a show its premiere.
    /// </summary>
    public int Released { get; set; }

    /// <summary>TMDB vote count, -1 when unknown. Feeds the popularity prior.</summary>
    public int Votes { get; set; } = -1;

    public string TmdbId { get; set; } = string.Empty;

    /// <summary>Normalized names of the people credited on it, or its own name for a person.</summary>
    public string[] People { get; set; } = Array.Empty<string>();

    /// <summary>Hash of the embedded text, so a rebuild only embeds what changed.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Catalog entries keep their Seerr-shaped JSON here to hand back as is.</summary>
    public string Payload { get; set; } = string.Empty;

    public float[] Vector { get; set; } = Array.Empty<float>();

    /// <summary>The title in the form queries are compared in. Not saved, the engine fills it on load.</summary>
    public string MatchTitle { get; set; } = string.Empty;
}

public readonly struct SmartHit
{
    public SmartHit(SmartEntry entry, float score, float similarity)
    {
        Entry = entry;
        Score = score;
        Similarity = similarity;
    }

    public SmartEntry Entry { get; }

    public float Score { get; }

    public float Similarity { get; }
}

/// <summary>A list of embedded entries, saved as one binary file, and the ranking over it.</summary>
public sealed class SmartIndex
{
    private const int FileVersion = 3;

    // Weights for what the embedding alone can't see, tuned by hand on a set of vague queries.
    public const float PopularityWeight = 0.25f;
    public const float LibraryBoost = 0.05f;
    public const float PersonBoost = 0.25f;
    public const float InRangeBoost = 0.05f;
    public const float MismatchPenalty = 0.3f;
    public const float PersonPenalty = 0.15f;
    public const float ExactTitleBoost = 0.3f;
    public const float TitleInQueryBoost = 0.1f;
    public const int MaxVotes = 40000;

    /// <summary>The SmartModel id the vectors came from. Vectors of two models can't be compared.</summary>
    public string Model { get; set; } = string.Empty;

    public List<SmartEntry> Entries { get; } = new List<SmartEntry>();

    public static string HashText(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 12);
    }

    public static SmartIndex Load(string path)
    {
        var index = new SmartIndex();
        if (!File.Exists(path))
        {
            return index;
        }

        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadInt32() != FileVersion)
        {
            return index;
        }

        index.Model = reader.ReadString();
        var count = reader.ReadInt32();
        var dims = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var entry = new SmartEntry
            {
                Key = reader.ReadString(),
                Kind = reader.ReadString(),
                Title = reader.ReadString(),
                YearMin = reader.ReadInt32(),
                YearMax = reader.ReadInt32(),
                Released = reader.ReadInt32(),
                Votes = reader.ReadInt32(),
                TmdbId = reader.ReadString(),
                People = new string[reader.ReadInt32()],
            };
            for (var p = 0; p < entry.People.Length; p++)
            {
                entry.People[p] = reader.ReadString();
            }

            entry.Hash = reader.ReadString();
            entry.Payload = reader.ReadString();
            entry.Vector = new float[dims];
            for (var d = 0; d < dims; d++)
            {
                entry.Vector[d] = reader.ReadSingle();
            }

            index.Entries.Add(entry);
        }

        return index;
    }

    /// <summary>Writes to a temporary file first so a crash never leaves half an index behind.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
        {
            var dims = Entries.Count > 0 ? Entries[0].Vector.Length : 0;
            writer.Write(FileVersion);
            writer.Write(Model);
            writer.Write(Entries.Count);
            writer.Write(dims);
            foreach (var e in Entries)
            {
                writer.Write(e.Key);
                writer.Write(e.Kind);
                writer.Write(e.Title);
                writer.Write(e.YearMin);
                writer.Write(e.YearMax);
                writer.Write(e.Released);
                writer.Write(e.Votes);
                writer.Write(e.TmdbId);
                writer.Write(e.People.Length);
                foreach (var p in e.People)
                {
                    writer.Write(p);
                }

                writer.Write(e.Hash);
                writer.Write(e.Payload);
                foreach (var v in e.Vector)
                {
                    writer.Write(v);
                }
            }
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>Entries ranked for the query, best first, with library ones getting a small edge.</summary>
    public List<SmartHit> Rank(
        float[] queryVector,
        SmartQuery query,
        SmartModel model,
        bool isLibrary,
        IReadOnlyDictionary<string, int>? votesByTitle = null,
        IReadOnlyDictionary<string, HashSet<string>>? creditsByPerson = null)
    {
        var maxLog = (float)Math.Log10(1 + MaxVotes);
        var queryText = " " + SmartQuery.NormalizeName(query.Text) + " ";

        // A person's text is short and lists titles, so it outscores the titles themselves
        // unless the query is about people: just a name, or asking for actors.
        var aboutPeople = query.NameOnly || query.Kinds.Contains(SmartQuery.Person);
        var hits = new List<SmartHit>(Entries.Count);
        foreach (var e in Entries)
        {
            if (query.HasDates && !WithinDates(e, query))
            {
                continue;
            }

            var similarity = (BertEncoder.Dot(queryVector, e.Vector) - model.SimilarityOffset) * model.SimilarityScale;
            var score = similarity;

            var votes = e.Votes;
            if (votes < 0 && votesByTitle != null && e.TmdbId.Length > 0 && votesByTitle.TryGetValue(TitleKey(e.Kind, e.TmdbId), out var known))
            {
                votes = known;
            }

            var popularity = votes < 0 ? 0.5f : Math.Min(1f, (float)Math.Log10(1 + votes) / maxLog);
            score += PopularityWeight * popularity;
            if (isLibrary)
            {
                score += LibraryBoost;
            }

            var namesPerson = query.People.Count > 0 && e.People.Any(query.People.Contains);
            if (namesPerson && (e.Kind != SmartQuery.Person || aboutPeople))
            {
                score += PersonBoost;
            }

            if (e.Kind == SmartQuery.Person && !aboutPeople)
            {
                score -= PersonPenalty;
            }

            // "actor from breaking bad": a person query that names a title they're credited on.
            if (e.Kind == SmartQuery.Person && query.Kinds.Contains(SmartQuery.Person) && creditsByPerson != null
                && e.People.Length > 0 && creditsByPerson.TryGetValue(e.People[0], out var credits)
                && credits.Any(t => t.IndexOf(' ') > 0 && queryText.IndexOf(" " + t + " ", StringComparison.Ordinal) >= 0))
            {
                score += PersonBoost;
            }

            if (query.Kinds.Count > 0 && !query.Kinds.Contains(e.Kind))
            {
                score -= MismatchPenalty;
            }

            if (!query.HasDates && query.HasYears && e.YearMin > 0)
            {
                var inRange = e.YearMax >= query.YearMin && e.YearMin <= query.YearMax;
                // An artist's years come from the albums on the server, which can miss when they started.
                score += inRange ? InRangeBoost : e.Kind == SmartQuery.MusicArtist ? 0 : -MismatchPenalty;
            }

            if (e.MatchTitle.Length > 0)
            {
                if (queryText.Length == e.MatchTitle.Length + 2 && queryText.IndexOf(e.MatchTitle, StringComparison.Ordinal) == 1)
                {
                    score += ExactTitleBoost;
                }
                // Only titles of several words, since a lone word like "frozen" is usually just a word.
                // People named in the query already got the person boost.
                else if (e.Kind != SmartQuery.Person && e.MatchTitle.IndexOf(' ') > 0 && queryText.IndexOf(" " + e.MatchTitle + " ", StringComparison.Ordinal) >= 0)
                {
                    score += TitleInQueryBoost;
                }
            }

            hits.Add(new SmartHit(e, score, similarity));
        }

        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits;
    }

    /// <summary>
    /// Whether an entry fits "new" or "upcoming". Entries without a date go by their first year, so a show
    /// counts from its premiere, and anything with neither, like a person, is left out.
    /// </summary>
    private static bool WithinDates(SmartEntry e, SmartQuery query) =>
        e.Released > 0
            ? e.Released >= query.DateMin && e.Released <= query.DateMax
            : e.YearMin >= query.YearMin && e.YearMin <= query.YearMax;

    /// <summary>TMDB ids repeat across movies and shows, so titles are told apart by kind too.</summary>
    public static string TitleKey(string kind, string tmdbId) => kind + ":" + tmdbId;

    /// <summary>The text a movie or show is embedded as. Keywords and cast come before the overview so a long overview gets cut instead of them.</summary>
    public static string TitleText(string title, int year, string kindWord, IEnumerable<string>? genres, IEnumerable<string>? keywords, IEnumerable<string>? cast, string? overview)
    {
        var sb = new StringBuilder();
        sb.Append(title);
        if (year > 0)
        {
            sb.Append(" (").Append(year).Append(')');
        }

        sb.Append(". ").Append(kindWord).Append('.');
        AppendList(sb, " ", genres, 4);
        AppendList(sb, " ", keywords, 12);
        AppendList(sb, " Starring ", cast, 5);
        if (!string.IsNullOrWhiteSpace(overview))
        {
            sb.Append(' ').Append(overview!.Trim());
        }

        return sb.ToString();
    }

    public static void AppendList(StringBuilder sb, string prefix, IEnumerable<string>? values, int max)
    {
        var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Take(max).ToList();
        if (list == null || list.Count == 0)
        {
            return;
        }

        sb.Append(prefix).Append(string.Join(", ", list)).Append('.');
    }
}
