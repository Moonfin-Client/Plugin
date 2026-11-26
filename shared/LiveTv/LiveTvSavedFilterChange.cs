using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Moonfin.LiveTv;

/// <summary>An intentional atomic change to shared guide shortcuts.</summary>
public sealed class LiveTvSavedFilterChange
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("filter")] public string? Filter { get; set; }
    [JsonPropertyName("filters")] public List<string>? Filters { get; set; }

    public bool IsValid() => GuidePinRules.ValidChange(Action, Filter, Filters);
}
