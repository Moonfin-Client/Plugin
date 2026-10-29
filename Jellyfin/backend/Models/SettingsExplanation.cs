using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

/// <summary>
/// What one user gets on one device type, setting by setting, for the admin preview.
/// </summary>
public class SettingsExplanation
{
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = string.Empty;

    /// <summary>False when the user never synced, so everything comes from the defaults.</summary>
    [JsonPropertyName("hasSettingsFile")]
    public bool HasSettingsFile { get; set; }

    [JsonPropertyName("lastUpdated")]
    public long? LastUpdated { get; set; }

    [JsonPropertyName("lastUpdatedBy")]
    public string? LastUpdatedBy { get; set; }

    [JsonPropertyName("fields")]
    public List<SettingExplanation> Fields { get; set; } = new();
}

/// <summary>
/// One setting: the value the user gets, the layer it comes from, and what each layer holds.
/// </summary>
public class SettingExplanation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>device, global, default (the admin defaults) or none (the app's own default).</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "none";

    [JsonPropertyName("effective")]
    public JsonNode? Effective { get; set; }

    [JsonPropertyName("device")]
    public JsonNode? Device { get; set; }

    [JsonPropertyName("global")]
    public JsonNode? Global { get; set; }

    [JsonPropertyName("default")]
    public JsonNode? Default { get; set; }

    /// <summary>The device value only repeats what global or the defaults would give.</summary>
    [JsonPropertyName("deviceRedundant")]
    public bool DeviceRedundant { get; set; }

    /// <summary>The global value only repeats the admin default.</summary>
    [JsonPropertyName("globalRedundant")]
    public bool GlobalRedundant { get; set; }
}

/// <summary>
/// How many settings one user has stored in each profile, for the admin overview.
/// </summary>
public class SettingsOverviewEntry
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("hasSettingsFile")]
    public bool HasSettingsFile { get; set; }

    [JsonPropertyName("lastUpdated")]
    public long? LastUpdated { get; set; }

    [JsonPropertyName("lastUpdatedBy")]
    public string? LastUpdatedBy { get; set; }

    [JsonPropertyName("desktop")]
    public int Desktop { get; set; }

    [JsonPropertyName("mobile")]
    public int Mobile { get; set; }

    [JsonPropertyName("tv")]
    public int Tv { get; set; }

    [JsonPropertyName("global")]
    public int Global { get; set; }

    /// <summary>How many stored values only repeat what the layers below give.</summary>
    [JsonPropertyName("repeated")]
    public int Repeated { get; set; }
}
