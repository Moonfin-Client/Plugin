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

    /// <summary>
    /// Server-selected artwork for the systems page. Omitted until the artwork
    /// manifest protocol is available so existing clients retain their current
    /// system-card behavior.
    /// </summary>
    [JsonPropertyName("previewArtwork")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameSystemPreviewArtwork? PreviewArtwork { get; set; }
}

/// <summary>Advertises the additive retro-game artwork protocol supported by the server.</summary>
public class GameArtworkCapabilities
{
    /// <summary>
    /// Highest artwork protocol version supported by this server. Version two
    /// adds manifests, versioned assets, system previews, and priority hints.
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("manifest")]
    public bool Manifest { get; set; }

    [JsonPropertyName("versionedAssets")]
    public bool VersionedAssets { get; set; }

    [JsonPropertyName("priorityHints")]
    public bool PriorityHints { get; set; }

    [JsonPropertyName("systemPreviews")]
    public bool SystemPreviews { get; set; }
}

/// <summary>State and version of one server-owned game artwork artifact.</summary>
public class GameArtworkDescriptor
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>Authenticated, versioned local asset path when the artifact is renderable.</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; set; }

    [JsonPropertyName("retryAfterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; set; }

    [JsonPropertyName("refreshAfterSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RefreshAfterSeconds { get; set; }
}

/// <summary>One game's artwork entry within a system-scoped manifest.</summary>
public class GameArtworkManifestEntry
{
    [JsonPropertyName("gameId")]
    public string GameId { get; set; } = string.Empty;

    /// <summary>
    /// Artwork keyed by its semantic role, such as <c>boxart</c>, <c>snap</c>,
    /// or <c>title</c>.
    /// </summary>
    [JsonPropertyName("artwork")]
    public Dictionary<string, GameArtworkDescriptor> Artwork { get; set; } = new();
}

/// <summary>Current artwork state for one system and one externally visible generation.</summary>
public class GameArtworkManifest
{
    [JsonPropertyName("generation")]
    public string Generation { get; set; } = string.Empty;

    [JsonPropertyName("entries")]
    public List<GameArtworkManifestEntry> Entries { get; set; } = new();
}

/// <summary>Ordered client hint that promotes existing artwork work without creating downloads.</summary>
public class GameArtworkPriorityRequest
{
    [JsonPropertyName("systemId")]
    public string SystemId { get; set; } = string.Empty;

    [JsonPropertyName("knownGeneration")]
    public string KnownGeneration { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<GameArtworkPriorityItem> Items { get; set; } = new();
}

/// <summary>One ordered game and its ordered semantic artwork roles in a priority hint.</summary>
public class GameArtworkPriorityItem
{
    [JsonPropertyName("gameId")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = new();
}

/// <summary>One ordered panel in a server-selected system artwork preview.</summary>
public class GameSystemPreviewPanel
{
    [JsonPropertyName("gameId")]
    public string GameId { get; set; } = string.Empty;

    [JsonPropertyName("artwork")]
    public GameArtworkDescriptor Artwork { get; set; } = new();
}

/// <summary>Stable server-selected artwork panels for a system card.</summary>
public class GameSystemPreviewArtwork
{
    [JsonPropertyName("selectionGeneration")]
    public string SelectionGeneration { get; set; } = string.Empty;

    [JsonPropertyName("panels")]
    public List<GameSystemPreviewPanel> Panels { get; set; } = new();
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
    /// <summary>
    /// Explicit per-user player backend selection. Currently <c>emulatorjs</c>; null means
    /// follow the client's native-core preference and normal fallback rules.
    /// </summary>
    [JsonPropertyName("userBackendOverride")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserBackendOverride { get; set; }

    /// <summary>
    /// Whether this server can persist a per-game player choice between the
    /// native libretro host and EmulatorJS. This is independent of arcade-core
    /// compatibility and is emitted even when false so clients can safely
    /// distinguish this plugin from older versions that lack the endpoint.
    /// </summary>
    [JsonPropertyName("backendOverrideSupported")]
    public bool BackendOverrideSupported { get; set; }

    /// <summary>
    /// Explicit per-user arcade core selection. Null means the effective <see cref="GameSummary.Core"/>
    /// is the automatic recommendation.
    /// </summary>
    [JsonPropertyName("userCoreOverride")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserCoreOverride { get; set; }

    /// <summary>
    /// Server-selected core for arcade archives. Kept separate from <see cref="GameSummary.Core"/> so
    /// clients can distinguish the automatic recommendation from a per-user override.
    /// Omitted for non-arcade systems to preserve the existing API contract.
    /// </summary>
    [JsonPropertyName("recommendedCore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecommendedCore { get; set; }

    /// <summary>Validated cores that can load this arcade archive.</summary>
    [JsonPropertyName("availableCores")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AvailableCores { get; set; }

    /// <summary>Human-readable result of the arcade DAT validation.</summary>
    [JsonPropertyName("coreCompatibilityReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoreCompatibilityReason { get; set; }

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
