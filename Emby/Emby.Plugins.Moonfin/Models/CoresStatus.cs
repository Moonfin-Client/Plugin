using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    public class CoresStatus
    {
        [JsonPropertyName("installed")] public bool Installed { get; set; }
        [JsonPropertyName("downloading")] public bool Downloading { get; set; }
        [JsonPropertyName("state")] public string State { get; set; } = "idle";
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("filesInstalled")] public int FilesInstalled { get; set; }
    }
}
