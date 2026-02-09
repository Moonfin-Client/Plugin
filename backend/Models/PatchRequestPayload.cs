using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

public class PatchRequestPayload
{
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
