using System;
using System.Collections.Generic;
using System.Globalization;

namespace Moonfin.Tmdb;

/// <summary>
/// The TMDB discover rows Moonbase serves by name, so every app asks for the same query and the
/// filters, dates and region live in one place.
/// </summary>
public static class TmdbDiscoverCharts
{
    private const string ProviderPrefix = "provider_";

    // A service can sit under more than one TMDB provider id across regions, and TMDB reads a pipe as "or".
    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["netflix"] = "8",
        ["prime_video"] = "9|119",
        ["disney_plus"] = "337",
        ["hbo_max"] = "1899|384",
        ["apple_tv"] = "350",
        ["paramount_plus"] = "531|2303",
    };

    /// <summary>The two-letter region an app sent, upper-cased, or US when it sent none or something else.</summary>
    public static string Region(string? region)
    {
        var r = region?.Trim() ?? string.Empty;
        return r.Length == 2 && IsAsciiLetter(r[0]) && IsAsciiLetter(r[1]) ? r.ToUpperInvariant() : "US";
    }

    /// <summary>
    /// The TMDB paths behind a chart, each with its query, or null for a name this server doesn't
    /// know. A streaming service's chart reads its movies and its shows, which the caller merges by
    /// popularity.
    /// </summary>
    public static string[]? Paths(string name, string? region, DateTime today)
    {
        var r = Region(region);
        if (name.StartsWith(ProviderPrefix, StringComparison.Ordinal)
            && Providers.TryGetValue(name.Substring(ProviderPrefix.Length), out var ids))
        {
            var query = $"with_watch_providers={ids}&watch_region={r}&with_watch_monetization_types=flatrate&sort_by=popularity.desc";
            return new[] { "discover/movie?" + query, "discover/tv?" + query };
        }

        return name switch
        {
            "korean_dramas" => new[] { "discover/tv?with_original_language=ko&with_genres=18&sort_by=popularity.desc" },
            "anime" => new[] { "discover/tv?with_original_language=ja&with_genres=16&sort_by=popularity.desc" },
            "new_digital_movies" => new[]
            {
                $"discover/movie?with_release_type=4&region={r}&release_date.gte={Day(today.AddDays(-60))}&release_date.lte={Day(today)}&sort_by=popularity.desc",
            },
            "hidden_gems" => new[]
            {
                "discover/movie?vote_average.gte=7.5&vote_count.gte=300&vote_count.lte=2000&without_genres=99,10770&sort_by=vote_average.desc",
            },
            _ => null,
        };
    }

    /// <summary>The country MDBList's JustWatch charts cover for a region, or US for the rest.</summary>
    public static string JustWatchCountry(string? region)
    {
        var r = Region(region);
        return r is "US" or "GB" or "CA" or "AU" or "DE" ? r : r == "UK" ? "GB" : "US";
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
