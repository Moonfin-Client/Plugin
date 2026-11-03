using System.Collections.Generic;
using System.Text.Json.Serialization;
using Emby.Plugins.Moonfin.Models;

namespace Emby.Plugins.Moonfin.Api
{
    internal class SaveSettingsBody
    {
        [JsonPropertyName("settings")] public MoonfinUserSettings? Settings { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("mergeMode")] public string? MergeMode { get; set; }
    }

    internal class SaveProfileBody
    {
        [JsonPropertyName("profile")] public MoonfinSettingsProfile? Profile { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    }

    internal class SaveDetailsBlurBody
    {
        [JsonPropertyName("profile")] public string? Profile { get; set; }
        [JsonPropertyName("detailsScreenBlur")] public string? DetailsScreenBlur { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    }

    internal class SaveDetailsOpacityBody
    {
        [JsonPropertyName("profile")] public string? Profile { get; set; }
        [JsonPropertyName("detailsScreenOpacity")] public int? DetailsScreenOpacity { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    }

    internal class ApplyDefaultsBody
    {
        [JsonPropertyName("overwrite")] public bool Overwrite { get; set; }
        [JsonPropertyName("userIds")] public List<string>? UserIds { get; set; }
        [JsonPropertyName("profiles")] public List<string>? Profiles { get; set; }
        [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
    }

    internal class CopySettingsBody
    {
        [JsonPropertyName("fromUserId")] public string? FromUserId { get; set; }
        [JsonPropertyName("toUserIds")] public List<string>? ToUserIds { get; set; }
        [JsonPropertyName("profiles")] public List<string>? Profiles { get; set; }
        [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
        [JsonPropertyName("replace")] public bool Replace { get; set; }
    }

    internal class BroadcastBody
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
