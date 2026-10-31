using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Moonfin.PosterBadges;

/// <summary>
/// A label the admin wants on posters, shown when every one of its conditions holds.
/// </summary>
public class PosterBadgeRule
{
    /// <summary>The text on the badge, such as "VFQ".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The badge's colour, as #RRGGBB.</summary>
    public string Color { get; set; } = "#2563EB";

    public bool Enabled { get; set; } = true;

    public List<PosterBadgeCondition> Conditions { get; set; } = new();
}

/// <summary>
/// One test on an item's file, such as "an audio track's title matches VFQ".
/// </summary>
public class PosterBadgeCondition
{
    /// <summary>
    /// "Path" for the file path, or a track type and one of its properties, such as
    /// "Audio.Title" or "Video.Height".
    /// </summary>
    public string Field { get; set; } = "Path";

    /// <summary>
    /// "matches" or "notMatches" for a regular expression, "atLeast" for a number.
    /// </summary>
    public string Operator { get; set; } = PosterBadgeMatcher.Matches;

    public string Value { get; set; } = string.Empty;
}

/// <summary>What a client draws for one matched rule.</summary>
public class PosterBadge
{
    public string Label { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;
}

/// <summary>
/// Works out which badges an item gets from its path and its tracks.
///
/// A condition on a track holds when any track of that type passes it, and each condition
/// is checked on its own, so two conditions on audio can be passed by two different tracks.
/// "notMatches" is the opposite: it holds when no track of that type matches, which is how a
/// rule says a file lacks something, such as a dub from France. Rules sharing a label are
/// alternatives: the badge shows once if any of them holds.
/// </summary>
public static class PosterBadgeMatcher
{
    public const string Matches = "matches";

    public const string NotMatches = "notMatches";

    public const string AtLeast = "atLeast";

    /// <summary>
    /// The admin writes these patterns, so one that backtracks badly could otherwise stall
    /// every request for a poster row.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Null for a pattern that doesnt compile, so it isnt parsed again each time.</summary>
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    /// <summary>
    /// The badges for one item, in the order of the rules. The streams are the server's own
    /// track objects, read by property name, so this works with Jellyfin and Emby alike.
    /// </summary>
    public static List<PosterBadge> Match(
        IEnumerable<PosterBadgeRule>? rules,
        string? path,
        IEnumerable? streams)
    {
        var badges = new List<PosterBadge>();
        if (rules == null)
        {
            return badges;
        }

        var tracks = new List<object>();
        if (streams != null)
        {
            foreach (var stream in streams)
            {
                if (stream != null)
                {
                    tracks.Add(stream);
                }
            }
        }

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var label = rule?.Label?.Trim();
            if (rule == null || !rule.Enabled || string.IsNullOrEmpty(label) || shown.Contains(label!))
            {
                continue;
            }

            // A rule with no conditions would badge every poster on the server.
            if (rule.Conditions == null || rule.Conditions.Count == 0)
            {
                continue;
            }

            if (rule.Conditions.TrueForAll(condition => Holds(condition, path, tracks)))
            {
                shown.Add(label!);
                badges.Add(new PosterBadge { Label = label!, Color = rule.Color ?? string.Empty });
            }
        }

        return badges;
    }

    private static bool Holds(PosterBadgeCondition? condition, string? path, List<object> tracks)
    {
        var field = condition?.Field?.Trim();
        if (condition == null || string.IsNullOrEmpty(field))
        {
            return false;
        }

        if (!string.Equals(condition.Operator, NotMatches, StringComparison.OrdinalIgnoreCase))
        {
            return AnyPasses(condition, field!, path, tracks);
        }

        // A pattern that doesnt compile fails here too, so a typo never reads as "matches nothing".
        return RegexFor(condition.Value) != null && !AnyPasses(condition, field!, path, tracks);
    }

    private static bool AnyPasses(PosterBadgeCondition condition, string field, string? path, List<object> tracks)
    {
        if (string.Equals(field, "Path", StringComparison.OrdinalIgnoreCase))
        {
            return Passes(condition, path);
        }

        var dot = field.IndexOf('.');
        if (dot <= 0 || dot == field.Length - 1)
        {
            return false;
        }

        var trackType = field.Substring(0, dot);
        var property = field.Substring(dot + 1);

        foreach (var track in tracks)
        {
            var type = track.GetType();

            // MediaStream.Type is an enum on both servers, so it is compared by name.
            var typeName = type.GetProperty("Type")?.GetValue(track)?.ToString();
            if (!string.Equals(typeName, trackType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = type.GetProperty(property)?.GetValue(track);
            if (Passes(condition, value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Passes(PosterBadgeCondition condition, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (string.Equals(condition.Operator, AtLeast, StringComparison.OrdinalIgnoreCase))
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.TryParse(condition.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
                && number >= threshold;
        }

        var regex = RegexFor(condition.Value);
        if (regex == null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static Regex? RegexFor(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        return RegexCache.GetOrAdd(pattern!, key =>
        {
            try
            {
                return new Regex(key, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
    }
}
