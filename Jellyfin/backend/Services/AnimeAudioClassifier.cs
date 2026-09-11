using System.Text.Json.Serialization;

namespace Moonfin.Server.Services;

/// <summary>
/// Whether a file is the original Japanese audio or carries a dub.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnimeAudioKind
{
    /// <summary>Japanese audio only, so anything you read is a subtitle.</summary>
    Subbed,

    /// <summary>Carries an audio track in some other language.</summary>
    Dubbed,

    /// <summary>
    /// Carries both Japanese and another language, so the viewer can choose. The caller
    /// may fold this back to <see cref="Dubbed"/>.
    /// </summary>
    SubbedAndDubbed
}

/// <summary>
/// Decides whether an episode is subbed or dubbed from the languages its audio tracks
/// declare.
///
/// The rule is the one a viewer actually means: Japanese and nothing else is "subbed";
/// the presence of any other spoken language makes it "dubbed", whether or not the
/// Japanese track is still there. Kept free of Jellyfin types so the awkward cases can be
/// tested directly.
/// </summary>
public static class AnimeAudioClassifier
{
    /// <summary>
    /// The spellings of Japanese that turn up in real files. Jellyfin normalises most
    /// tracks to ISO 639-2/B, but remuxes carry whatever the muxer wrote.
    /// </summary>
    private static readonly HashSet<string> JapaneseCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpn", "jp", "ja", "jap", "japanese", "japan"
    };

    /// <summary>
    /// Values that mean "nobody tagged this", which is not a language and must not be read
    /// as a foreign dub. A file tagged only with these is left unclassified.
    /// </summary>
    private static readonly HashSet<string> UnknownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "und", "unknown", "unk", "zxx", "mis", "mul"
    };

    /// <summary>
    /// Classifies one file from its audio track languages, or null when nothing usable was
    /// tagged. Null means "say nothing", never "assume subbed": an untagged rip is the
    /// common case and guessing would put a wrong pill on it.
    /// </summary>
    public static AnimeAudioKind? Classify(IEnumerable<string?> audioLanguages)
    {
        var sawJapanese = false;
        var sawOther = false;

        foreach (var raw in audioLanguages)
        {
            var language = raw?.Trim();
            if (string.IsNullOrEmpty(language) || UnknownCodes.Contains(language))
            {
                continue;
            }

            if (JapaneseCodes.Contains(language))
            {
                sawJapanese = true;
            }
            else
            {
                sawOther = true;
            }
        }

        if (sawOther)
        {
            return sawJapanese ? AnimeAudioKind.SubbedAndDubbed : AnimeAudioKind.Dubbed;
        }

        return sawJapanese ? AnimeAudioKind.Subbed : null;
    }

    /// <summary>
    /// Folds the dual-audio verdict back into <see cref="AnimeAudioKind.Dubbed"/> for admins
    /// who would rather see one label. Applied at the edge, so the classifier itself always
    /// reports what the file actually holds.
    /// </summary>
    public static AnimeAudioKind Collapse(AnimeAudioKind kind, bool separateDualAudio) =>
        !separateDualAudio && kind == AnimeAudioKind.SubbedAndDubbed
            ? AnimeAudioKind.Dubbed
            : kind;

    /// <summary>
    /// Classifies a whole season from its episodes' verdicts.
    ///
    /// A season only earns a pill when every episode that could be classified agrees, since
    /// the pill is a promise about the season as a whole. A season holding both a dubbed and
    /// a subbed episode is left blank rather than being labelled by majority, which would be
    /// wrong for whichever episodes are in the minority. Episodes with no verdict are
    /// ignored rather than counted against the rest.
    /// </summary>
    public static AnimeAudioKind? ClassifySeason(IEnumerable<AnimeAudioKind?> episodeKinds)
    {
        AnimeAudioKind? agreed = null;

        foreach (var kind in episodeKinds)
        {
            if (kind == null)
            {
                continue;
            }

            if (agreed == null)
            {
                agreed = kind;
                continue;
            }

            if (agreed != kind)
            {
                return null;
            }
        }

        return agreed;
    }
}
