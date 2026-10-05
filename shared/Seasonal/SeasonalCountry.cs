using System;

namespace Moonfin.Seasonal;

/// <summary>Which country a seasonal request is for.</summary>
public static class SeasonalCountry
{
    /// <summary>A client sends this for a viewer who picked "Other": no country, and no fallback.</summary>
    public const string Other = "ZZ";

    /// <summary>
    /// The requested code when it is one, else the server's metadata country unless that is the
    /// untouched default of US, else none. None means only the holidays everyone observes apply.
    /// </summary>
    public static string? Resolve(string? requested, string? serverCountry)
    {
        var code = Clean(requested);
        if (code != null)
        {
            return code == Other ? null : code;
        }

        var fallback = Clean(serverCountry);
        return fallback == null || fallback == "US" ? null : fallback;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value!.Trim();
        if (trimmed.Length != 2 || !char.IsLetter(trimmed[0]) || !char.IsLetter(trimmed[1]))
        {
            return null;
        }

        return trimmed.ToUpperInvariant();
    }
}
