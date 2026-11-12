using System.Globalization;

namespace Moonfin.Games;

/// <summary>The artwork protocol's response shapes and checks, the same on every server.</summary>
public static class GameArtworkApi
{
    public const int MaxArtworkPriorityItems = 128;

    private static readonly TimeSpan OriginalManifestRefresh = TimeSpan.FromSeconds(30);

    /// <summary>What the artwork protocol supports, for apps that check before using it.</summary>
    public static GameArtworkCapabilities Capabilities() => new GameArtworkCapabilities
    {
        ProtocolVersion = 3,
        Manifest = true,
        VersionedAssets = true,
        PriorityHints = true,
        SystemPreviews = true,
        Dimensions = true,
    };

    /// <summary>One system's manifest, grouped by game with each role's descriptor.</summary>
    public static GameArtworkManifest Manifest(string libraryId, GameArtworkSystemReadResult artwork)
    {
        var entries = artwork.Entries
            .GroupBy(entry => entry.GameId, StringComparer.Ordinal)
            .Select(group => new GameArtworkManifestEntry
            {
                GameId = group.Key,
                Artwork = group.ToDictionary(
                    entry => entry.Role,
                    entry => ToDescriptor(libraryId, entry.GameId, entry.Role, entry.Entry),
                    StringComparer.Ordinal),
            })
            .ToList();
        return new GameArtworkManifest { Generation = Generation(artwork.System.Generation), Entries = entries };
    }

    /// <summary>
    /// Projects one system's durable preview choice into renderable panels, in the persisted order.
    /// The renderable boxart entries are indexed once rather than rescanned per panel.
    /// </summary>
    public static List<GameSystemPreviewPanel> PreviewPanels(string libraryId, GameArtworkSystemReadResult artwork)
    {
        var renderableBoxart = new Dictionary<string, GameArtworkReadEntry>(StringComparer.Ordinal);
        foreach (var entry in artwork.Entries)
        {
            if (string.Equals(entry.Role, "boxart", StringComparison.Ordinal) && IsRenderable(entry.Entry))
            {
                // TryAdd keeps each game's first boxart entry.
                renderableBoxart.TryAdd(entry.GameId, entry);
            }
        }

        var panels = new List<GameSystemPreviewPanel>();
        foreach (var gameId in artwork.System.PreviewGameIds)
        {
            if (panels.Count == 4)
            {
                break;
            }

            if (renderableBoxart.TryGetValue(gameId, out var entry))
            {
                panels.Add(new GameSystemPreviewPanel
                {
                    GameId = entry.GameId,
                    Artwork = ToDescriptor(libraryId, entry.GameId, entry.Role, entry.Entry),
                });
            }
        }

        return panels;
    }

    /// <summary>Adds each game's cataloged box art, read in one catalog pass for the whole row.</summary>
    public static void AttachBoxart(
        string libraryId,
        IEnumerable<RecentGame> games,
        IReadOnlyDictionary<string, GameArtworkSystemReadResult> artworkBySystem)
    {
        foreach (var game in games)
        {
            var entry = artworkBySystem.TryGetValue(game.System, out var artwork)
                ? artwork.Entries.FirstOrDefault(e => e.Role == "boxart" && string.Equals(e.GameId, game.Id, StringComparison.Ordinal))
                : null;
            if (entry != null)
            {
                game.Boxart = ToDescriptor(libraryId, game.Id, entry.Role, entry.Entry);
            }
        }
    }

    /// <summary>Adds each system's preview panels, read in one catalog pass for the whole page.</summary>
    public static void AttachPreviews(
        string libraryId,
        IEnumerable<GameSystem> systems,
        IReadOnlyDictionary<string, GameArtworkSystemReadResult> artworkBySystem)
    {
        foreach (var system in systems)
        {
            if (!artworkBySystem.TryGetValue(system.Id, out var artwork) || artwork.System.PreviewGameIds.Count == 0)
            {
                continue;
            }

            var panels = PreviewPanels(libraryId, artwork);
            if (panels.Count > 0)
            {
                system.PreviewArtwork = new GameSystemPreviewArtwork
                {
                    SelectionGeneration = artwork.System.InventoryGeneration ?? Generation(artwork.System.Generation),
                    Panels = panels,
                };
            }
        }
    }

    public static GameArtworkDescriptor ToDescriptor(string libraryId, string gameId, string role, ArtworkCatalogEntry entry)
    {
        var descriptor = new GameArtworkDescriptor { State = State(entry.State) };
        if (IsRenderable(entry))
        {
            var revision = entry.Revision.ToString(CultureInfo.InvariantCulture);
            descriptor.Revision = revision;
            descriptor.Url = $"/Moonfin/Games/{Uri.EscapeDataString(libraryId)}/Artwork/{Uri.EscapeDataString(gameId)}/{role}/{revision}";
            if (entry.Width is > 0 && entry.Height is > 0)
            {
                descriptor.Width = entry.Width;
                descriptor.Height = entry.Height;
            }
        }

        if (entry.State == ArtworkCatalogState.OriginalReady)
        {
            var refresh = entry.RetryAfterUtc is { } retryAfter && retryAfter > DateTimeOffset.UtcNow
                ? retryAfter - DateTimeOffset.UtcNow
                : OriginalManifestRefresh;
            descriptor.RefreshAfterSeconds = Math.Max(1, (int)Math.Ceiling(refresh.TotalSeconds));
        }
        else if (entry.State == ArtworkCatalogState.Retryable && entry.RetryAfterUtc is { } retryAfter)
        {
            descriptor.RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(Math.Max(0, (retryAfter - DateTimeOffset.UtcNow).TotalSeconds)));
        }
        else if (entry.State == ArtworkCatalogState.Pending)
        {
            descriptor.RefreshAfterSeconds = 5;
        }

        return descriptor;
    }

    /// <summary>
    /// Picks the artwork Cache-Control value. Versioned URLs (the revision route) may be cached as
    /// immutable because a stale copy simply falls out of use once the revision segment changes.
    /// The legacy, unversioned thumb route has no such signal, so it gets a short, must-revalidate
    /// lifetime instead.
    /// </summary>
    public static string CacheControl(bool versionedUrl, bool isThumbnail) =>
        "private,max-age=" + ((long)CacheDuration(versionedUrl, isThumbnail).TotalSeconds).ToString(CultureInfo.InvariantCulture)
            + (versionedUrl && isThumbnail ? ",immutable" : versionedUrl ? string.Empty : ",must-revalidate");

    /// <summary>The max-age in <see cref="CacheControl"/>, for servers that take caching as a duration.</summary>
    public static TimeSpan CacheDuration(bool versionedUrl, bool isThumbnail) =>
        TimeSpan.FromSeconds(versionedUrl && isThumbnail ? 31536000 : versionedUrl ? 7200 : 300);

    /// <summary>
    /// True for full=1 or full=true. Any other non-empty value that isn't "0" or "false" also
    /// counts. Missing, empty, "0" or "false" mean the default derived thumbnail.
    /// </summary>
    public static bool WantsFullResolution(string? full) =>
        !string.IsNullOrEmpty(full)
        && full != "0"
        && !string.Equals(full, "false", StringComparison.OrdinalIgnoreCase);

    public static string Generation(long generation) =>
        "artwork-" + generation.ToString(CultureInfo.InvariantCulture);

    public static string NormalizeLegacyRole(string? type) =>
        string.Equals(type, "snap", StringComparison.OrdinalIgnoreCase) ? "snap" :
        string.Equals(type, "title", StringComparison.OrdinalIgnoreCase) ? "title" : "boxart";

    public static bool IsRole(string? role) =>
        string.Equals(role, "boxart", StringComparison.Ordinal) ||
        string.Equals(role, "snap", StringComparison.Ordinal) ||
        string.Equals(role, "title", StringComparison.Ordinal);

    /// <summary>Seconds for a Retry-After header, at least one.</summary>
    public static int RetryAfterSeconds(TimeSpan retryAfter) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, retryAfter.TotalSeconds)));

    private static bool IsRenderable(ArtworkCatalogEntry entry) =>
        entry.State switch
        {
            ArtworkCatalogState.OriginalReady => !string.IsNullOrWhiteSpace(entry.OriginalPath),
            ArtworkCatalogState.ThumbnailReady => !string.IsNullOrWhiteSpace(entry.ThumbnailPath) || !string.IsNullOrWhiteSpace(entry.OriginalPath),
            _ => false,
        };

    private static string State(ArtworkCatalogState state) => state switch
    {
        ArtworkCatalogState.OriginalReady => "originalReady",
        ArtworkCatalogState.ThumbnailReady => "thumbnailReady",
        ArtworkCatalogState.Missing => "missing",
        ArtworkCatalogState.Retryable => "transientFailure",
        _ => "pending",
    };
}
