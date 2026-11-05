using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Moonfin.PosterBadges;

/// <summary>What the admin page sends to try its rules on one file, saved or not.</summary>
public class PosterBadgeTestRequest
{
    public string? ItemId { get; set; }

    public List<PosterBadgeRule>? Rules { get; set; }

    public PosterBadgeDefaults? Defaults { get; set; }
}

/// <summary>How one rule fared on the file.</summary>
public class PosterBadgeRuleResult
{
    /// <summary>The rule's place in the list.</summary>
    public int Index { get; set; }

    /// <summary>"shown", "off", "noText", "notMet" or "sameText" (an earlier rule with the same text showed).</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Whether each condition held, in order. Empty for a built-in label.</summary>
    public List<bool> Conditions { get; set; } = new();
}

/// <summary>
/// Explains the rules on one file for the admin page: which labels show, why the others
/// dont, and the track values the conditions read.
/// </summary>
public static class PosterBadgeTest
{
    /// <summary>The track properties a condition can read, plus a few that help explain them.</summary>
    private static readonly string[] TrackProperties =
    {
        "Language", "Title", "Codec", "Profile", "Channels", "Width", "Height",
        "VideoRangeType", "VideoRange", "ExtendedVideoType", "IsDefault", "IsForced",
    };

    public static List<PosterBadgeRuleResult> Explain(IReadOnlyList<PosterBadgeRule>? rules, string? path, IEnumerable? streams)
    {
        var results = new List<PosterBadgeRuleResult>();
        if (rules == null)
        {
            return results;
        }

        var tracks = Tracks(streams);
        var presets = PosterBadgePresets.Detect(path, tracks);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var result = new PosterBadgeRuleResult { Index = i };
            results.Add(result);
            if (rule == null)
            {
                result.Outcome = "noText";
                continue;
            }

            bool holds;
            if (!string.IsNullOrWhiteSpace(rule.Preset))
            {
                holds = presets.Contains(rule.Preset!.Trim());
            }
            else
            {
                foreach (var condition in rule.Conditions ?? new List<PosterBadgeCondition>())
                {
                    result.Conditions.Add(PosterBadgeMatcher.Holds(condition, path, tracks));
                }

                holds = result.Conditions.Count > 0 && result.Conditions.TrueForAll(passed => passed);
            }

            var label = rule.Label?.Trim();
            if (!rule.Enabled)
            {
                result.Outcome = "off";
            }
            else if (string.IsNullOrEmpty(label))
            {
                result.Outcome = "noText";
            }
            else if (!holds)
            {
                result.Outcome = "notMet";
            }
            else if (!shown.Add(label!))
            {
                result.Outcome = "sameText";
            }
            else
            {
                result.Outcome = "shown";
            }
        }

        return results;
    }

    /// <summary>Each track's type and the values the conditions read, leaving out empty ones.</summary>
    public static List<Dictionary<string, object>> DescribeTracks(IEnumerable? streams)
    {
        var described = new List<Dictionary<string, object>>();
        foreach (var track in Tracks(streams))
        {
            var type = track.GetType();
            var row = new Dictionary<string, object>
            {
                ["Type"] = type.GetProperty("Type")?.GetValue(track)?.ToString() ?? string.Empty,
            };

            foreach (var name in TrackProperties)
            {
                var value = type.GetProperty(name)?.GetValue(track);
                var text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                {
                    row[name] = text!;
                }
            }

            described.Add(row);
        }

        return described;
    }

    private static List<object> Tracks(IEnumerable? streams)
    {
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

        return tracks;
    }
}
