using System;

namespace Moonfin.CustomRows;

/// <summary>
/// The provider ids a title from an outside list and a library item can be matched on. Used to
/// find the library copy of a list entry, so clients can show the server's own artwork for it.
/// </summary>
public sealed class TitleIds
{
    public TitleIds(bool series, string? tmdb, string? imdb, string? tvdb)
    {
        Series = series;
        Tmdb = Clean(tmdb);
        Imdb = Clean(imdb);
        Tvdb = Clean(tvdb);
    }

    /// <summary>A series or a movie. TMDB numbers movies and shows separately, so a match never crosses kinds.</summary>
    public bool Series { get; }

    public string? Tmdb { get; }

    public string? Imdb { get; }

    public string? Tvdb { get; }

    public bool IsEmpty => Tmdb == null && Imdb == null && Tvdb == null;

    /// <summary>
    /// A list entry's kind is "Series" for shows and anything else for movies, the two kinds
    /// the custom rows hand out.
    /// </summary>
    public static bool IsSeriesType(string? type) =>
        string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How strongly <paramref name="other"/> is the same title: 0 when the TMDB ids agree, 1 for
    /// IMDb, 2 for TVDB, or -1 when nothing matches. Lower is better, so of several library
    /// copies the one sharing the most specific id wins.
    /// </summary>
    public int MatchRank(TitleIds other)
    {
        if (Series != other.Series)
        {
            return -1;
        }

        if (Tmdb != null && Tmdb == other.Tmdb)
        {
            return 0;
        }

        if (Imdb != null && string.Equals(Imdb, other.Imdb, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (Tvdb != null && Tvdb == other.Tvdb)
        {
            return 2;
        }

        return -1;
    }

    public bool Matches(TitleIds other) => MatchRank(other) >= 0;

    public override bool Equals(object? obj) =>
        obj is TitleIds other &&
        Series == other.Series &&
        Tmdb == other.Tmdb &&
        string.Equals(Imdb, other.Imdb, StringComparison.OrdinalIgnoreCase) &&
        Tvdb == other.Tvdb;

    public override int GetHashCode() =>
        HashCode.Combine(Series, Tmdb, Imdb?.ToLowerInvariant(), Tvdb);

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "0" ? null : trimmed;
    }
}
