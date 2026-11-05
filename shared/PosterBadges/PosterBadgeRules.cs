using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>
    /// A built-in label's id, such as "4k". Its own detection is used and the conditions
    /// are ignored. Empty for a rule the admin wrote.
    /// </summary>
    public string? Preset { get; set; }

    public List<PosterBadgeCondition> Conditions { get; set; } = new();

    // The look. Empty means the admin's default from PosterBadgeDefaults.

    /// <summary>"solid", "glass", "outline", "dark" or "light".</summary>
    public string? Style { get; set; }

    /// <summary>#RRGGBB, or empty to pick black or white from the style and colour.</summary>
    public string? TextColor { get; set; }

    /// <summary>How solid the label's fill is, from 10 to 100. The text stays solid.</summary>
    public int? Opacity { get; set; }

    /// <summary>"small", "medium", "large" or "xlarge".</summary>
    public string? Size { get; set; }

    /// <summary>"topLeft", "topRight", "bottomLeft" or "bottomRight".</summary>
    public string? Corner { get; set; }

    /// <summary>"rounded", "pill" or "square".</summary>
    public string? Shape { get; set; }

    /// <summary>
    /// A Material icon drawn before the text, one of <see cref="PosterBadgeMatcher.Icons"/>.
    /// Empty for none, which is how every label starts. With no text the label is the icon.
    /// </summary>
    public string? Icon { get; set; }

    // Who sees it. Empty means everyone.

    /// <summary>Server user ids allowed to see the label.</summary>
    public List<string> UserIds { get; set; } = new();

    /// <summary>"tv", "desktop" or "mobile".</summary>
    public List<string> Profiles { get; set; } = new();
}

/// <summary>The look a label gets when its rule leaves a setting empty.</summary>
public class PosterBadgeDefaults
{
    public string Style { get; set; } = "solid";

    public int Opacity { get; set; } = 100;

    public string Size { get; set; } = "medium";

    public string Corner { get; set; } = "topLeft";

    public string Shape { get; set; } = "rounded";

    /// <summary>
    /// The most labels a client stacks in one corner. More than a few turns a poster into
    /// a sticker sheet, and the admin's order decides which ones win.
    /// </summary>
    public int MaxPerCorner { get; set; } = 2;
}

/// <summary>
/// One test on an item's file, such as "an audio track's title matches VFQ".
/// </summary>
public class PosterBadgeCondition
{
    /// <summary>
    /// "Path" for the file path, a track type and one of its properties, such as
    /// "Audio.Title" or "Video.Height", or a track type and "Count" or "LanguageCount",
    /// such as "Audio.LanguageCount".
    /// </summary>
    public string Field { get; set; } = "Path";

    /// <summary>
    /// Which tracks of that type have to pass: "any" (at least one), "none" or "every". For
    /// the path and a count there is one value, and "none" turns the test around.
    /// </summary>
    public string Match { get; set; } = PosterBadgeMatcher.Any;

    /// <summary>
    /// Text: "is", "contains", "oneOf" (a comma-separated list) or "matches" (a regular
    /// expression). Numbers: "is", "atLeast", "moreThan", "atMost" or "lessThan".
    /// </summary>
    public string Operator { get; set; } = PosterBadgeMatcher.Contains;

    public string Value { get; set; } = string.Empty;
}

/// <summary>What a client draws for one matched rule, with every default filled in.</summary>
public class PosterBadge
{
    public string Label { get; set; } = string.Empty;

    public string Color { get; set; } = string.Empty;

    /// <summary>Empty when the client picks it.</summary>
    public string TextColor { get; set; } = string.Empty;

    public string Style { get; set; } = "solid";

    public int Opacity { get; set; } = 100;

    public string Size { get; set; } = "medium";

    public string Corner { get; set; } = "topLeft";

    public string Shape { get; set; } = "rounded";

    /// <summary>Empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// The shape clients read. Spelled out because Jellyfin and Emby name JSON properties
    /// differently.
    /// </summary>
    public object ToJson() => new
    {
        label = Label,
        color = Color,
        textColor = TextColor,
        style = Style,
        opacity = Opacity,
        size = Size,
        corner = Corner,
        shape = Shape,
        icon = Icon,
    };
}

/// <summary>
/// Works out which badges an item gets from its path and its tracks.
///
/// A condition on a track looks at every track of that type: "any" holds when one passes,
/// "none" when none does, which is how a rule says a file lacks something, such as a dub
/// from France, and "every" when there is at least one and all of them pass. Each condition
/// is checked on its own, so two conditions on audio can be passed by two different tracks.
/// Rules sharing a label are alternatives: the badge shows once if any of them holds.
/// </summary>
public static class PosterBadgeMatcher
{
    public const string Any = "any";

    public const string None = "none";

    public const string Every = "every";

    public const string Is = "is";

    public const string Contains = "contains";

    public const string OneOf = "oneOf";

    public const string Matches = "matches";

    public const string AtLeast = "atLeast";

    public const string MoreThan = "moreThan";

    public const string AtMost = "atMost";

    public const string LessThan = "lessThan";

    /// <summary>The first version's "does not match". Read as "none" and "matches".</summary>
    public const string NotMatches = "notMatches";

    /// <summary>
    /// The admin writes these patterns, so one that backtracks badly could otherwise stall
    /// every request for a poster row.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Null for a pattern that doesnt compile, so it isnt parsed again each time.</summary>
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new();

    /// <summary>
    /// The rules one viewer gets: enabled, and not limited to other users or devices. A
    /// client that doesnt say its device only gets the rules shown on every device.
    /// </summary>
    public static List<PosterBadgeRule> ForViewer(IEnumerable<PosterBadgeRule>? rules, string? userId, string? profile)
    {
        var viewer = ParseGuid(userId);
        var kept = new List<PosterBadgeRule>();
        if (rules == null)
        {
            return kept;
        }

        foreach (var rule in rules)
        {
            if (rule == null || !rule.Enabled || ShownKey(rule) == null)
            {
                continue;
            }

            if (rule.UserIds != null && rule.UserIds.Count > 0
                && (viewer == null || !rule.UserIds.Exists(id => ParseGuid(id) == viewer)))
            {
                continue;
            }

            if (rule.Profiles != null && rule.Profiles.Count > 0
                && !rule.Profiles.Exists(p => string.Equals(p?.Trim(), profile?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(rule);
        }

        return kept;
    }

    /// <summary>
    /// A short fingerprint of the rules and the defaults. Clients keep labels for the whole
    /// session, and drop them when this changes, so an edit shows without a restart. Built by
    /// hand rather than with string.GetHashCode, which changes on every server start.
    /// </summary>
    public static string Version(IEnumerable<PosterBadgeRule>? rules, PosterBadgeDefaults? defaults)
    {
        var text = new System.Text.StringBuilder();
        void Add(object? value) => text.Append(value).Append('\u001f');

        if (defaults != null)
        {
            Add(defaults.Style); Add(defaults.Opacity); Add(defaults.Size); Add(defaults.Corner); Add(defaults.Shape); Add(defaults.MaxPerCorner);
        }

        foreach (var rule in rules ?? Array.Empty<PosterBadgeRule>())
        {
            if (rule == null)
            {
                continue;
            }

            Add(rule.Label); Add(rule.Color); Add(rule.Enabled); Add(rule.Preset); Add(rule.TextColor); Add(rule.Style);
            Add(rule.Opacity); Add(rule.Size); Add(rule.Corner); Add(rule.Shape); Add(rule.Icon);
            Add(string.Join(",", rule.UserIds ?? new List<string>()));
            Add(string.Join(",", rule.Profiles ?? new List<string>()));
            foreach (var condition in rule.Conditions ?? new List<PosterBadgeCondition>())
            {
                Add(condition?.Field); Add(condition?.Match); Add(condition?.Operator); Add(condition?.Value);
            }

            text.Append('\u001e');
        }

        // FNV-1a, 64 bits.
        var hash = 14695981039346656037UL;
        foreach (var c in text.ToString())
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Ids come dashed or not depending on the server and the client, so they are compared as guids.</summary>
    private static Guid? ParseGuid(string? id) => Guid.TryParse(id?.Trim(), out var guid) ? guid : (Guid?)null;

    /// <summary>
    /// The badges for one item, in the order of the rules. The streams are the server's own
    /// track objects, read by property name, so this works with Jellyfin and Emby alike.
    /// </summary>
    public static List<PosterBadge> Match(
        IEnumerable<PosterBadgeRule>? rules,
        string? path,
        IEnumerable? streams,
        PosterBadgeDefaults? defaults = null)
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

        // Worked out on the first preset rule only, since most items meet several.
        HashSet<string>? presets = null;

        foreach (var rule in rules)
        {
            var key = rule == null ? null : ShownKey(rule);
            if (rule == null || !rule.Enabled || key == null || shown.Contains(key))
            {
                continue;
            }

            bool holds;
            if (!string.IsNullOrWhiteSpace(rule.Preset))
            {
                presets ??= PosterBadgePresets.Detect(path, tracks);
                holds = presets.Contains(rule.Preset!.Trim());
            }
            else
            {
                // A rule with no conditions would badge every poster on the server.
                holds = rule.Conditions != null
                    && rule.Conditions.Count > 0
                    && rule.Conditions.TrueForAll(condition => Holds(condition, path, tracks));
            }

            if (holds)
            {
                shown.Add(key);
                badges.Add(Resolve(rule, rule.Label?.Trim() ?? string.Empty, defaults ?? new PosterBadgeDefaults()));
            }
        }

        return badges;
    }

    private static readonly string[] Styles = { "solid", "glass", "outline", "dark", "light" };

    private static readonly string[] Sizes = { "small", "medium", "large", "xlarge" };

    private static readonly string[] Corners = { "topLeft", "topRight", "bottomLeft", "bottomRight" };

    private static readonly string[] Shapes = { "rounded", "pill", "square" };

    /// <summary>
    /// The icons a label can carry, by their Material name, so every client draws the same
    /// glyph. Brand logos are left out on purpose: they need a licence.
    /// </summary>
    public static readonly string[] Icons = new[]
    {
        "contrast", "hdr_on", "high_quality", "movie", "theaters", "aspect_ratio", "surround_sound",
        "speaker", "graphic_eq", "album", "translate", "subtitles", "record_voice_over", "star", "check_circle",
    }.Concat(PosterBadgeFlags.Codes.Select(code => "flag-" + code)).ToArray();

    /// <summary>The rule's icon when it is one clients know, else null.</summary>
    public static string? IconOf(PosterBadgeRule rule) =>
        Array.Find(Icons, icon => string.Equals(icon, rule.Icon?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What makes two labels the same, so it shows once: the text, or the icon for a label
    /// that has none. Null for a label with neither, which has nothing to draw.
    /// </summary>
    public static string? ShownKey(PosterBadgeRule rule)
    {
        var label = rule.Label?.Trim();
        if (!string.IsNullOrEmpty(label))
        {
            return label;
        }

        var icon = IconOf(rule);
        return icon == null ? null : "icon:" + icon;
    }

    /// <summary>
    /// Fills every empty or unknown setting from the defaults, so clients draw exactly what
    /// they get and an old config or a typo never reaches them.
    /// </summary>
    private static PosterBadge Resolve(PosterBadgeRule rule, string label, PosterBadgeDefaults defaults) => new()
    {
        Label = label,
        Color = Hex(rule.Color) ?? "#2563EB",
        TextColor = Hex(rule.TextColor) ?? string.Empty,
        Style = Pick(Styles, rule.Style, defaults.Style),
        Opacity = Math.Max(10, Math.Min(100, rule.Opacity ?? defaults.Opacity)),
        Size = Pick(Sizes, rule.Size, defaults.Size),
        Corner = Pick(Corners, rule.Corner, defaults.Corner),
        Shape = Pick(Shapes, rule.Shape, defaults.Shape),
        Icon = IconOf(rule) ?? string.Empty,
    };

    private static string Pick(string[] allowed, string? value, string? fallback)
    {
        foreach (var candidate in new[] { value, fallback })
        {
            var found = Array.Find(allowed, a => string.Equals(a, candidate?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                return found;
            }
        }

        return allowed[0];
    }

    private static string? Hex(string? value) =>
        value != null && Regex.IsMatch(value.Trim(), "^#[0-9a-fA-F]{6}$") ? value.Trim().ToUpperInvariant() : null;

    /// <summary>Whether one condition holds for the file. Public for the admin page's test.</summary>
    public static bool Holds(PosterBadgeCondition? condition, string? path, List<object> tracks)
    {
        var field = condition?.Field?.Trim();
        if (condition == null || string.IsNullOrEmpty(field))
        {
            return false;
        }

        var op = condition.Operator?.Trim() ?? string.Empty;
        var match = condition.Match?.Trim() ?? Any;
        if (string.Equals(op, NotMatches, StringComparison.OrdinalIgnoreCase))
        {
            op = Matches;
            match = None;
        }

        // A blank value, a pattern that doesnt compile or a number that isnt one fails
        // the whole condition, so a typo never reads as "no track has it".
        if (!Usable(op, condition.Value))
        {
            return false;
        }

        var values = ValuesOf(field!, path, tracks);
        if (values == null)
        {
            return false;
        }

        var passing = values.FindAll(value => Passes(op, condition.Value, value)).Count;

        if (string.Equals(match, None, StringComparison.OrdinalIgnoreCase))
        {
            return passing == 0;
        }

        if (string.Equals(match, Every, StringComparison.OrdinalIgnoreCase))
        {
            // A file with no track of that type doesnt pass "every audio track is French".
            return values.Count > 0 && passing == values.Count;
        }

        return passing > 0;
    }

    /// <summary>
    /// The values a condition looks at: the path, the number of tracks of a type, or that
    /// property of each track of the type. Null for a field that names nothing.
    /// </summary>
    private static List<string?>? ValuesOf(string field, string? path, List<object> tracks)
    {
        if (string.Equals(field, "Path", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string?> { path };
        }

        var dot = field.IndexOf('.');
        if (dot <= 0 || dot == field.Length - 1)
        {
            return null;
        }

        var trackType = field.Substring(0, dot);
        var property = field.Substring(dot + 1);
        var count = string.Equals(property, "Count", StringComparison.OrdinalIgnoreCase);
        var languageCount = string.Equals(property, "LanguageCount", StringComparison.OrdinalIgnoreCase);
        if (languageCount)
        {
            property = "Language";
        }

        var values = new List<string?>();

        foreach (var track in tracks)
        {
            var type = track.GetType();

            // MediaStream.Type is an enum on both servers, so it is compared by name.
            var typeName = type.GetProperty("Type")?.GetValue(track)?.ToString();
            if (!string.Equals(typeName, trackType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = count ? null : type.GetProperty(property)?.GetValue(track);
            values.Add(value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        if (languageCount)
        {
            var languages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var language in values)
            {
                var key = LanguageKey(language);
                if (key != null)
                {
                    languages.Add(key);
                }
            }

            return new List<string?> { languages.Count.ToString(CultureInfo.InvariantCulture) };
        }

        return count ? new List<string?> { values.Count.ToString(CultureInfo.InvariantCulture) } : values;
    }

    /// <summary>
    /// ISO 639-2 gives some languages two codes, and files use both. The bibliographic one
    /// is read as the terminology one, so "fre" and "fra" are one language.
    /// </summary>
    private static readonly Dictionary<string, string> SameLanguage = new(StringComparer.Ordinal)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho",
        ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu",
        ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa",
        ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym",
    };

    /// <summary>
    /// One key per language, so a French dub and a French audio description count once, and
    /// so do a dub from France and one from Quebec. Untagged tracks dont count.
    /// </summary>
    private static string? LanguageKey(string? language)
    {
        var code = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        // A regional tag such as "fr-CA" is still French.
        var dash = code!.IndexOfAny(new[] { '-', '_' });
        if (dash > 0)
        {
            code = code.Substring(0, dash);
        }

        if (code == "und" || code == "mis" || code == "zxx" || code == "mul")
        {
            return null;
        }

        if (SameLanguage.TryGetValue(code, out var terminology))
        {
            return terminology;
        }

        // Two-letter codes are matched to the three-letter ones .NET knows, so "fr" is "fra".
        if (code.Length == 2)
        {
            try
            {
                // A server without culture data answers "ivl" for every code.
                var name = new CultureInfo(code).ThreeLetterISOLanguageName;
                return string.IsNullOrEmpty(name) || name == "ivl" ? code : name;
            }
            catch (CultureNotFoundException)
            {
                return code;
            }
        }

        return code;
    }

    private static bool Usable(string op, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (string.Equals(op, Matches, StringComparison.OrdinalIgnoreCase))
        {
            return RegexFor(target) != null;
        }

        if (IsNumeric(op))
        {
            return Number(target) != null;
        }

        return string.Equals(op, Is, StringComparison.OrdinalIgnoreCase)
            || string.Equals(op, Contains, StringComparison.OrdinalIgnoreCase)
            || string.Equals(op, OneOf, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(string op) =>
        string.Equals(op, AtLeast, StringComparison.OrdinalIgnoreCase)
        || string.Equals(op, MoreThan, StringComparison.OrdinalIgnoreCase)
        || string.Equals(op, AtMost, StringComparison.OrdinalIgnoreCase)
        || string.Equals(op, LessThan, StringComparison.OrdinalIgnoreCase);

    private static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : (double?)null;

    private static bool Passes(string op, string target, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (IsNumeric(op))
        {
            var number = Number(value);
            var threshold = Number(target)!.Value;
            return number != null && (op.ToLowerInvariant() switch
            {
                "atleast" => number >= threshold,
                "morethan" => number > threshold,
                "atmost" => number <= threshold,
                _ => number < threshold,
            });
        }

        if (string.Equals(op, Is, StringComparison.OrdinalIgnoreCase))
        {
            return SameValue(value!, target);
        }

        if (string.Equals(op, Contains, StringComparison.OrdinalIgnoreCase))
        {
            return value!.IndexOf(target.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (string.Equals(op, OneOf, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Exists(target.Split(','), option => option.Trim().Length > 0 && SameValue(value!, option));
        }

        try
        {
            return RegexFor(target)!.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Numbers compare as numbers, so "6" is "6.0". Text ignores case.</summary>
    private static bool SameValue(string value, string target)
    {
        var a = Number(value);
        var b = Number(target);
        return a != null && b != null
            ? a.Value.Equals(b.Value)
            : string.Equals(value.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);
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
