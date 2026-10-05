using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    /// <summary>
    /// The seasonal Home row for one user. Owned movies come as the same card DTO the browse
    /// endpoints return, suggestions in the custom row item shape with a rating attached.
    /// </summary>
    public class SeasonalRowResponse
    {
        /// <summary>The holiday the row is for, or null when there is no row to show.</summary>
        [JsonPropertyName("holiday")] public string? Holiday { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("items")] public List<object> Items { get; set; } = new List<object>();
        [JsonPropertyName("suggestions")] public List<CustomRowItem> Suggestions { get; set; } = new List<CustomRowItem>();
    }
}
