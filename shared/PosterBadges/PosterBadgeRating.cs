using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Moonfin.PosterBadges;

/// <summary>
/// The item's age rating as the server stores it, such as "FR-12", "PG-13" or "TV-MA". There
/// is no list of ratings to keep up: the label shows the text, a little cleaned up.
/// </summary>
public static class PosterBadgeRating
{
    /// <summary>The built-in label that shows the rating.</summary>
    public const string PresetId = "ageRating";

    /// <summary>What a rating says when there is none. Kept with or without a country, as in "FR-NR".</summary>
    private static readonly HashSet<string> Unrated = new(StringComparer.OrdinalIgnoreCase)
    {
        "NR", "N/A", "Unrated", "Not Rated",
    };

    /// <summary>
    /// US ratings whose first two letters are also a country's code: PG-13 is not Papua New
    /// Guinea's 13, NC-17 not New Caledonia's, TV-MA not Tuvalu's.
    /// </summary>
    private static readonly HashSet<string> NotCountries = new(StringComparer.OrdinalIgnoreCase) { "pg", "nc", "tv" };

    /// <summary>
    /// The rating to show, or null when the item has none. NFO files write "Rated R", so
    /// "Rated " goes.
    /// </summary>
    public static string? Clean(string? rating)
    {
        var text = rating?.Trim() ?? string.Empty;
        if (text.StartsWith("Rated ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(6).Trim();
        }

        return text.Length == 0 || Unrated.Contains(Split(text).Rating) ? null : text;
    }

    /// <summary>
    /// The country, and the rating without it. Jellyfin and Emby write a rating from outside
    /// the US as "FR-12", Plex as "fr/12", and Jellyfin turns "DE-12" into "FSK-12". A US
    /// rating has no country, and neither has one from an NFO file.
    /// </summary>
    public static (string? Country, string Rating) Split(string text)
    {
        if (text.StartsWith("FSK-", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
        {
            return ("de", text.Substring(4).Trim());
        }

        if (text.Length > 3 && (text[2] == '-' || text[2] == '/') && char.IsLetter(text[0]) && char.IsLetter(text[1]))
        {
            var code = text.Substring(0, 2).ToLowerInvariant();
            if (!NotCountries.Contains(code) && Array.IndexOf(PosterBadgeFlags.Codes, code) >= 0)
            {
                return (code, text.Substring(3).Trim());
            }
        }

        return (null, text);
    }

    /// <summary>
    /// The text and flag the label shows, null when the item has no rating. The country is
    /// "keep" (FR-12), "hide" (12) or "flag" (the French flag, then 12). A rating with no
    /// country always shows as it is, with the rule's own icon.
    /// </summary>
    public static (string Text, string? Flag)? Show(string? rating, string? country)
    {
        var text = Clean(rating);
        if (text == null)
        {
            return null;
        }

        var (code, rest) = Split(text);
        if (code == null || string.Equals(country?.Trim(), "keep", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(country))
        {
            return (text, null);
        }

        return string.Equals(country!.Trim(), "flag", StringComparison.OrdinalIgnoreCase) ? (rest, "flag-" + code) : (rest, null);
    }

    /// <summary>The youngest age of each colour band: all ages, then 7, 12, 16 and 18 and over.</summary>
    public static readonly int[] AgeBands = { 0, 7, 12, 16, 18 };

    /// <summary>
    /// The age a rating names in its text: 13 in PG-13, 7 in TV-Y7, 15 in MA 15+. Null when
    /// it is only letters, such as R or TP, which only the server's own tables can place.
    /// </summary>
    public static int? AgeInText(string? rating)
    {
        var text = Clean(rating);
        if (text == null)
        {
            return null;
        }

        var digits = Regex.Match(Split(text).Rating, @"\d+");
        return digits.Success && int.TryParse(digits.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var age) ? age : null;
    }

    /// <summary>
    /// The rule's colour or icon for the age, one per band, or null to keep the label's own:
    /// when the rule has none per age, or the age is unknown.
    /// </summary>
    public static string? ForAge(IReadOnlyList<string>? perBand, int? age)
    {
        if (age == null || perBand == null || perBand.Count != AgeBands.Length)
        {
            return null;
        }

        var band = AgeBands.Length - 1;
        while (band > 0 && age < AgeBands[band])
        {
            band--;
        }

        return perBand[band];
    }
}
