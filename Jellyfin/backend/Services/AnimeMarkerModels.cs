using System.Text.Json.Serialization;

namespace Moonfin.Server.Services;

/// <summary>
/// One of the four categories AnimeFillerList uses to classify episodes. The site does not
/// have a separate "recap" category, so recaps are stored in a separate field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnimeEpisodeKind
{
    /// <summary>Adapted straight from the manga.</summary>
    MangaCanon,

    /// <summary>Not in the manga, but part of the anime's own continuity.</summary>
    AnimeCanon,

    /// <summary>Part manga, part padding. Worth watching, worth warning about.</summary>
    Mixed,

    /// <summary>Pure filler.</summary>
    Filler
}

/// <summary>
/// One AnimeFillerList show as it appears in the site's A-Z index.
/// </summary>
public class AnimeFillerShow
{
    /// <summary>The path segment under /shows/, used verbatim. The site's own slugs are
    /// not always derivable from the title, so this is stored rather than recomputed.</summary>
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>The display title exactly as the index lists it, parentheses included.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// One episode's classification, numbered the way AnimeFillerList numbers it: absolute
/// across the whole show, ignoring season boundaries.
/// </summary>
public class AnimeMarkerEpisode
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("kind")]
    public AnimeEpisodeKind Kind { get; set; }

    /// <summary>
    /// MyAnimeList's recap flag, filled in separately and only when the recap lookup is
    /// enabled. AnimeFillerList has no recap category of its own.
    /// </summary>
    [JsonPropertyName("recap")]
    public bool Recap { get; set; }
}

/// <summary>
/// One cached show: every episode AnimeFillerList lists for it, plus what it was matched
/// from.
/// </summary>
public class AnimeMarkerCacheEntry
{
    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("episodes")]
    public List<AnimeMarkerEpisode> Episodes { get; set; } = new();

    /// <summary>
    /// The MyAnimeList id the recap flags came from, when one was resolved. Null means the
    /// recap pass has not run for this show, so <see cref="AnimeMarkerEpisode.Recap"/> is
    /// "not known" rather than "known false".
    /// </summary>
    [JsonPropertyName("recapMalId")]
    public int? RecapMalId { get; set; }

    [JsonPropertyName("cachedAt")]
    public DateTimeOffset CachedAt { get; set; }
}
