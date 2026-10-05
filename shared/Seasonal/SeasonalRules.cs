using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonfin.Seasonal;

/// <summary>
/// One holiday the seasonal row can show: when it runs, where, and which TMDB keywords
/// mark a movie as belonging to it. Loaded from the embedded seasonal-holidays.json that
/// both server ports ship.
/// </summary>
public sealed class SeasonalHoliday
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>TMDB vote count a suggestion needs, so a thin holiday can set its own bar.</summary>
    [JsonPropertyName("minVotes")]
    public int MinVotes { get; set; } = 50;

    [JsonPropertyName("windows")]
    public List<SeasonalWindow> Windows { get; set; } = new List<SeasonalWindow>();

    [JsonPropertyName("keywords")]
    public List<SeasonalKeyword> Keywords { get; set; } = new List<SeasonalKeyword>();
}

/// <summary>A date range the holiday is active for, limited to some countries or open to all.</summary>
public sealed class SeasonalWindow
{
    /// <summary>ISO alpha-2 codes, or null when every country observes the window.</summary>
    [JsonPropertyName("countries")]
    public List<string>? Countries { get; set; }

    [JsonPropertyName("start")]
    public DateRule Start { get; set; } = new DateRule();

    [JsonPropertyName("end")]
    public DateRule End { get; set; } = new DateRule();

    /// <summary>
    /// The holiday's day per year as "MM-dd", for holidays that follow another calendar and
    /// have no rule. A year the table leaves out has no window.
    /// </summary>
    [JsonPropertyName("dates")]
    public Dictionary<string, string>? Dates { get; set; }

    public bool AppliesTo(string? country)
    {
        if (Countries == null || Countries.Count == 0)
        {
            return true;
        }

        return country != null && Countries.Any(c => string.Equals(c, country, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A day in some year. "fixed" is a month and day, "nthWeekday" is the nth weekday of a
/// month (the 4th Thursday of November), "easter" is Western Easter Sunday and "dates"
/// reads the window's own table. Every kind can shift by offsetDays.
/// </summary>
public sealed class DateRule
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "fixed";

    [JsonPropertyName("month")]
    public int Month { get; set; }

    [JsonPropertyName("day")]
    public int Day { get; set; }

    [JsonPropertyName("weekday")]
    public string? Weekday { get; set; }

    [JsonPropertyName("n")]
    public int N { get; set; }

    [JsonPropertyName("offsetDays")]
    public int OffsetDays { get; set; }
}

/// <summary>A TMDB keyword, by id for the keyword store and by name for Jellyfin's tags.</summary>
public sealed class SeasonalKeyword
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public static class SeasonalRules
{
    public const string ResourceName = "Moonfin.Seasonal.holidays.json";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<SeasonalHoliday> LoadEmbedded(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded resource " + ResourceName + " is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<SeasonalHoliday> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RulesFile>(json, JsonOptions);
        var holidays = file?.Holidays ?? new List<SeasonalHoliday>();
        foreach (var holiday in holidays)
        {
            if (string.IsNullOrWhiteSpace(holiday.Id))
            {
                throw new InvalidOperationException("A seasonal holiday has no id.");
            }

            if (holiday.Windows.Count == 0)
            {
                throw new InvalidOperationException("Seasonal holiday " + holiday.Id + " has no windows.");
            }

            if (holiday.Windows.Any(w => (w.Start.Kind == "dates" || w.End.Kind == "dates") && (w.Dates == null || w.Dates.Count == 0)))
            {
                throw new InvalidOperationException("Seasonal holiday " + holiday.Id + " uses a dates rule without a dates table.");
            }
        }

        return holidays;
    }

    private sealed class RulesFile
    {
        [JsonPropertyName("holidays")]
        public List<SeasonalHoliday>? Holidays { get; set; }
    }
}
