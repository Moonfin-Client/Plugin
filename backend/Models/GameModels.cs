using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

/// <summary>A Jellyfin library recognized as holding retro game ROMs.</summary>
public class GameLibrary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Physical folder roots on disk. Not serialized to clients.</summary>
    [JsonIgnore]
    public List<string> Locations { get; set; } = new();
}

/// <summary>A top-level console/system folder within a game library.</summary>
public class GameSystem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>EmulatorJS core name (e.g. "snes", "gba").</summary>
    [JsonPropertyName("core")]
    public string Core { get; set; } = string.Empty;

    [JsonPropertyName("gameCount")]
    public int GameCount { get; set; }
}

/// <summary>A single game entry in a system.</summary>
public class GameSummary
{
    /// <summary>Opaque token resolving to the ROM file on disk.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("system")]
    public string System { get; set; } = string.Empty;

    [JsonPropertyName("core")]
    public string Core { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;
}

/// <summary>Full detail for a game, including BIOS files needed by its core.</summary>
public class GameDetail : GameSummary
{
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("bios")]
    public List<GameBios> Bios { get; set; } = new();

    // Optional metadata from the libretro database, keyed by ROM CRC. Coverage is uneven, so
    // every field is nullable and omitted from JSON when absent; clients render only what is
    // present.
    [JsonPropertyName("genre")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Genre { get; set; }

    [JsonPropertyName("developer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Developer { get; set; }

    [JsonPropertyName("publisher")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Publisher { get; set; }

    [JsonPropertyName("franchise")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Franchise { get; set; }

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    [JsonPropertyName("year")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Year { get; set; }

    [JsonPropertyName("players")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Players { get; set; }

    [JsonPropertyName("overview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Overview { get; set; }

    [JsonPropertyName("rating")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rating { get; set; }
}

/// <summary>A BIOS file available for a system.</summary>
public class GameBios
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
}
