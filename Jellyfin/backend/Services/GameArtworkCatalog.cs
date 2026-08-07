using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Durable state for the artwork associated with one ROM. Catalogs are deliberately separate from
/// the request-time artwork store: HTTP handlers may read this state, but remote acquisition and
/// thumbnail encoding are owned by the reconciliation workers.
/// </summary>
public enum ArtworkCatalogState
{
    Pending,
    OriginalReady,
    ThumbnailReady,
    Missing,
    Retryable,
}

/// <summary>Which portion of an unfinished entry still needs background work.</summary>
public enum ArtworkCatalogWorkStage
{
    AcquireOriginal,
    DeriveThumbnail,
}

/// <summary>
/// Stable identity of a catalog entry. A ROM fingerprint belongs in the identity rather than only
/// in mutable metadata: replacing a ROM at the same relative path must reopen a previous miss.
/// </summary>
public sealed record ArtworkCatalogKey(
    string LibraryId,
    string NormalizedRomPath,
    string RomFingerprint,
    string Role)
{
    /// <summary>Creates a normalized catalog key from worker/controller inputs.</summary>
    public static ArtworkCatalogKey Create(string libraryId, string relativeRomPath, string romFingerprint, string role) =>
        new(
            NormalizeRequired(libraryId, nameof(libraryId), lowerCase: true),
            NormalizeRelativePath(relativeRomPath),
            NormalizeRequired(romFingerprint, nameof(romFingerprint), lowerCase: true),
            NormalizeRequired(role, nameof(role), lowerCase: true));

    internal string StorageKey => Hash(LibraryId + "\n" + NormalizedRomPath + "\n" + RomFingerprint + "\n" + Role);

    internal string IdentityWithoutFingerprint => LibraryId + "\n" + NormalizedRomPath + "\n" + Role;

    private static string NormalizeRelativePath(string value)
    {
        var supplied = NormalizeRequired(value, nameof(value), lowerCase: false);
        if (Path.IsPathRooted(supplied) || supplied.StartsWith("\\\\", StringComparison.Ordinal) || supplied.Contains(':'))
        {
            throw new ArgumentException("The ROM path must be relative to its library root.", nameof(value));
        }

        var normalized = supplied.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new ArgumentException("The ROM path must stay relative to its library root.", nameof(value));
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            throw new ArgumentException("The ROM path must name a file below its library root.", nameof(value));
        }

        // The Games service treats library paths case-insensitively where the host does, and the
        // catalog is one logical identity across host migrations. Keep the persisted key stable.
        return string.Join('/', parts).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private static string NormalizeRequired(string value, string parameterName, bool lowerCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        return lowerCase ? normalized.ToLowerInvariant() : normalized;
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>A persisted artwork state, including only local file paths and no provider credentials.</summary>
public sealed record ArtworkCatalogEntry
{
    public ArtworkCatalogKey Key { get; init; } = ArtworkCatalogKey.Create("unknown", "unknown", "unknown", "boxart");

    /// <summary>
    /// Current opaque client token for this ROM. This is a projection for catalog-only manifest
    /// reads, not part of the durable artwork identity (which remains the relative path and
    /// fingerprint in <see cref="Key"/>).
    /// </summary>
    public string GameId { get; init; } = string.Empty;

    /// <summary>The system that owns this game; used to invalidate its externally visible manifest.</summary>
    public string SystemId { get; init; } = string.Empty;

    public ArtworkCatalogState State { get; init; } = ArtworkCatalogState.Pending;

    public string? OriginalPath { get; init; }

    public string? ThumbnailPath { get; init; }

    /// <summary>Monotonically increasing asset version for immutable versioned-resource URLs.</summary>
    public long Revision { get; init; }

    /// <summary>Earliest background retry after a transient acquisition/encode failure.</summary>
    public DateTimeOffset? RetryAfterUtc { get; init; }

    /// <summary>Provider/catalog revision that confirmed a definitive missing result.</summary>
    public string? MissingProviderVersion { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>Persisted, externally visible state for one system's artwork manifest.</summary>
public sealed record ArtworkCatalogSystemSnapshot(
    string SystemId,
    long Generation,
    string? InventoryGeneration,
    IReadOnlyList<string> PreviewGameIds)
{
    /// <summary>
    /// Stable, complete traversal order for preview discovery. This is deliberately distinct
    /// from <see cref="PreviewGameIds"/>: a remote miss must not consume one of the four panels.
    /// </summary>
    public IReadOnlyList<string> PreviewCandidateGameIds { get; init; } = Array.Empty<string>();
}

/// <summary>An item workers must reconstruct after a process restart.</summary>
public sealed record ArtworkCatalogWorkItem(
    ArtworkCatalogEntry Entry,
    ArtworkCatalogWorkStage Stage,
    DateTimeOffset? NotBeforeUtc);

/// <summary>Catalog-only projection used by controllers without rescanning ROM directories.</summary>
public sealed record ArtworkCatalogSystemProjection(
    ArtworkCatalogSystemSnapshot System,
    IReadOnlyList<ArtworkCatalogEntry> Entries);

/// <summary>One current reconciliation projection to persist under a library lock.</summary>
public sealed record ArtworkCatalogProjectionEntry(ArtworkCatalogKey Key, string SystemId, string GameId);

/// <summary>Persisted system metadata used by the systems route between reconciliations.</summary>
public sealed record ArtworkCatalogSystemMetadata(string SystemId, string Name, string Core, int GameCount);

/// <summary>
/// One crash-safe JSON catalog per game library. A mutation writes a complete replacement via
/// <see cref="AtomicFile"/>, coalesced over a short debounce window (see
/// <see cref="MarkDirtyUnsafeAsync"/>); startup reads use its backup recovery path, so a stopped
/// process cannot turn a durable missing decision into a corrupt catalog or a repeated remote lookup.
/// </summary>
public sealed class GameArtworkCatalog
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// Longest a mutation may sit in memory before it is written. Every debounced transition is
    /// re-derivable after a crash (see <see cref="MarkDirtyUnsafeAsync"/>), so this window buys
    /// two to three orders of magnitude fewer whole-document rewrites for a bounded amount of
    /// repeated background work.
    /// </summary>
    private static readonly TimeSpan DefaultPersistDebounce = TimeSpan.FromSeconds(2);

    private readonly string _root;
    private readonly ILogger<GameArtworkCatalog>? _logger;
    private readonly TimeSpan _persistDebounce;
    private readonly ConcurrentDictionary<string, Lazy<Task<LibraryCatalog>>> _libraries = new(StringComparer.Ordinal);
    private long _documentWrites;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Creates a catalog below a plugin-owned directory (normally game-artwork-catalog).</summary>
    public GameArtworkCatalog(string root, ILogger<GameArtworkCatalog>? logger = null, TimeSpan? persistDebounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _logger = logger;
        _persistDebounce = persistDebounce ?? DefaultPersistDebounce;
    }

    /// <summary>Whole-document writes performed so far; the debounce's only observable effect.</summary>
    internal long DocumentWriteCount => Interlocked.Read(ref _documentWrites);

    /// <summary>
    /// Plugin-owned catalog directory. The reconciliation service uses its sibling artwork cache
    /// directory to keep controller-served paths contained below plugin data.
    /// </summary>
    internal string RootPath => _root;

    /// <summary>Returns the exact entry for a fingerprint, or null when it has not been cataloged.</summary>
    public async Task<ArtworkCatalogEntry?> GetAsync(ArtworkCatalogKey key, CancellationToken cancellationToken = default)
    {
        key = NormalizeKey(key);
        var library = await GetLibraryAsync(key.LibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return library.Entries.TryGetValue(key.StorageKey, out var entry) ? entry : null;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Returns an entry ready for work. A changed ROM fingerprint removes the old identity, while
    /// a provider-version change is the only way an unchanged confirmed miss becomes pending.
    /// </summary>
    public async Task<ArtworkCatalogEntry> GetOrCreateAsync(
        ArtworkCatalogKey key,
        string systemId,
        string providerVersion,
        string? gameId = null,
        CancellationToken cancellationToken = default)
    {
        key = NormalizeKey(key);
        systemId = NormalizeSystemId(systemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);
        var library = await GetLibraryAsync(key.LibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = false;
            var staleKeys = library.FindByIdentityWithoutFingerprint(key.IdentityWithoutFingerprint)
                .Where(storageKey => !string.Equals(storageKey, key.StorageKey, StringComparison.Ordinal))
                .ToArray();
            foreach (var staleKey in staleKeys)
            {
                library.RemoveEntry(staleKey);
                changed = true;
            }

            if (!library.Entries.TryGetValue(key.StorageKey, out var entry))
            {
                entry = NewPending(key, systemId, gameId: gameId);
                library.SetEntry(key.StorageKey, entry);
                changed = true;
            }
            else if (entry.State == ArtworkCatalogState.Missing &&
                !string.Equals(entry.MissingProviderVersion, providerVersion, StringComparison.Ordinal))
            {
                entry = NewPending(key, systemId, entry.Revision, gameId);
                library.SetEntry(key.StorageKey, entry);
                changed = true;
            }
            else if ((gameId != null && !string.Equals(entry.GameId, gameId, StringComparison.Ordinal)) ||
                !string.Equals(entry.SystemId, systemId, StringComparison.Ordinal))
            {
                entry = entry with { GameId = gameId ?? entry.GameId, SystemId = systemId };
                library.SetEntry(key.StorageKey, entry);
                changed = true;
            }

            if (changed)
            {
                BumpSystemGeneration(library.Document, systemId);
                await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            }

            return entry;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>Records a locally cached original and schedules/retains its thumbnail work.</summary>
    public Task<ArtworkCatalogEntry> MarkOriginalReadyAsync(
        ArtworkCatalogKey key,
        string systemId,
        string originalPath,
        CancellationToken cancellationToken = default) =>
        UpdateEntryAsync(key, systemId, entry => entry with
        {
            State = ArtworkCatalogState.OriginalReady,
            OriginalPath = RequirePath(originalPath, nameof(originalPath)),
            ThumbnailPath = null,
            RetryAfterUtc = null,
            MissingProviderVersion = null,
            Revision = entry.Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken);

    /// <summary>Records the derived local thumbnail and advances the versioned asset revision.</summary>
    public Task<ArtworkCatalogEntry> MarkThumbnailReadyAsync(
        ArtworkCatalogKey key,
        string systemId,
        string thumbnailPath,
        CancellationToken cancellationToken = default) =>
        UpdateEntryAsync(key, systemId, entry => entry with
        {
            State = ArtworkCatalogState.ThumbnailReady,
            ThumbnailPath = RequirePath(thumbnailPath, nameof(thumbnailPath)),
            RetryAfterUtc = null,
            MissingProviderVersion = null,
            Revision = entry.Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken);

    /// <summary>
    /// Persists an authoritative absence. An unchanged ROM/provider pair remains missing across
    /// restarts and must not be scheduled again until refresh, removal/reappearance, or a changed
    /// fingerprint/provider version explicitly reopens it.
    /// </summary>
    public Task<ArtworkCatalogEntry> MarkMissingAsync(
        ArtworkCatalogKey key,
        string systemId,
        string providerVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);
        return UpdateEntryAsync(key, systemId, entry => entry with
        {
            State = ArtworkCatalogState.Missing,
            OriginalPath = null,
            ThumbnailPath = null,
            RetryAfterUtc = null,
            MissingProviderVersion = providerVersion.Trim(),
            Revision = entry.Revision + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    /// <summary>Records retry timing without converting a transient problem into a durable miss.</summary>
    public Task<ArtworkCatalogEntry> MarkRetryableAsync(
        ArtworkCatalogKey key,
        string systemId,
        ArtworkCatalogWorkStage stage,
        DateTimeOffset retryAfterUtc,
        CancellationToken cancellationToken = default) =>
        UpdateEntryAsync(key, systemId, entry => stage == ArtworkCatalogWorkStage.DeriveThumbnail &&
            !string.IsNullOrWhiteSpace(entry.OriginalPath)
            ? entry with
            {
                State = ArtworkCatalogState.OriginalReady,
                ThumbnailPath = null,
                RetryAfterUtc = retryAfterUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }
            : entry with
            {
                State = ArtworkCatalogState.Retryable,
                OriginalPath = null,
                ThumbnailPath = null,
                RetryAfterUtc = retryAfterUtc,
                MissingProviderVersion = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }, cancellationToken);

    /// <summary>Explicit refresh deliberately reopens an entry, including a confirmed miss.</summary>
    public Task<ArtworkCatalogEntry> RequestRefreshAsync(
        ArtworkCatalogKey key,
        string systemId,
        string? gameId = null,
        CancellationToken cancellationToken = default)
    {
        // Normalized up front (not inside UpdateEntryAsync) because the closure below captures
        // this local directly rather than the copy UpdateEntryAsync would normalize internally.
        var normalizedSystemId = NormalizeSystemId(systemId);
        return UpdateEntryAsync(
            key,
            normalizedSystemId,
            entry => NewPending(key, normalizedSystemId, entry.Revision, gameId ?? entry.GameId),
            cancellationToken);
    }

    /// <summary>Removes a ROM identity so a later reappearance starts a fresh catalog lifecycle.</summary>
    public async Task<bool> RemoveAsync(ArtworkCatalogKey key, string systemId, CancellationToken cancellationToken = default)
    {
        key = NormalizeKey(key);
        systemId = NormalizeSystemId(systemId);
        var library = await GetLibraryAsync(key.LibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!library.RemoveEntry(key.StorageKey))
            {
                return false;
            }

            BumpSystemGeneration(library.Document, systemId);
            await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>Reads one system's generation and durable preview choice.</summary>
    public async Task<ArtworkCatalogSystemSnapshot> GetSystemAsync(
        string libraryId,
        string systemId,
        CancellationToken cancellationToken = default)
    {
        systemId = NormalizeSystemId(systemId);
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Snapshot(library.Document.Systems.TryGetValue(systemId, out var system)
                ? system
                : new PersistedSystem { SystemId = systemId });
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Reads the persisted membership and artwork projection for one reconciled system. This is
    /// intentionally catalog-only so request handlers do not walk ROM directories.
    /// </summary>
    public async Task<ArtworkCatalogSystemProjection?> GetSystemProjectionAsync(
        string libraryId,
        string systemId,
        CancellationToken cancellationToken = default)
    {
        var projections = await GetSystemProjectionsAsync(libraryId, new[] { systemId }, cancellationToken).ConfigureAwait(false);
        return projections.TryGetValue(systemId, out var projection) ? projection : null;
    }

    /// <summary>
    /// Reads several systems' projections in one pass over the entry collection. Results are keyed
    /// by the id the caller supplied (callers see filesystem casing, the catalog stores normalized
    /// casing); systems with no persisted record are simply absent. The systems route needs every
    /// system on one page load, and asking for them one at a time re-walked every entry per system,
    /// which is quadratic in the size of the library.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ArtworkCatalogSystemProjection>> GetSystemProjectionsAsync(
        string libraryId,
        IEnumerable<string> systemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemIds);
        var requested = systemIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => (Supplied: id, Normalized: NormalizeSystemId(id)))
            .ToArray();
        var projections = new Dictionary<string, ArtworkCatalogSystemProjection>(StringComparer.Ordinal);
        if (requested.Length == 0)
        {
            return projections;
        }

        var wanted = requested.Select(item => item.Normalized).ToHashSet(StringComparer.Ordinal);
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bySystem = new Dictionary<string, List<ArtworkCatalogEntry>>(StringComparer.Ordinal);
            foreach (var entry in library.Entries.Values)
            {
                // Persisted system ids are normalized on write and on load (MigrateSystemIdCasing),
                // so an ordinal match against the normalized request is exact.
                if (string.IsNullOrWhiteSpace(entry.GameId) || !wanted.Contains(entry.SystemId))
                {
                    continue;
                }

                if (!bySystem.TryGetValue(entry.SystemId, out var entries))
                {
                    entries = new List<ArtworkCatalogEntry>();
                    bySystem.Add(entry.SystemId, entries);
                }

                entries.Add(entry);
            }

            foreach (var (supplied, normalized) in requested)
            {
                if (projections.ContainsKey(supplied) || !library.Document.Systems.TryGetValue(normalized, out var system))
                {
                    continue;
                }

                projections.Add(supplied, new ArtworkCatalogSystemProjection(
                    Snapshot(system),
                    bySystem.TryGetValue(normalized, out var entries) ? entries : Array.Empty<ArtworkCatalogEntry>()));
            }

            return projections;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>Reads reconciled systems without walking configured ROM roots on an HTTP request.</summary>
    public async Task<IReadOnlyList<ArtworkCatalogSystemMetadata>> GetSystemMetadataAsync(
        string libraryId,
        CancellationToken cancellationToken = default)
    {
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return library.Document.Systems.Values
                .Where(system => !string.IsNullOrWhiteSpace(system.Name))
                .OrderBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
                .Select(system => new ArtworkCatalogSystemMetadata(system.SystemId, system.Name, system.Core, system.GameCount))
                .ToArray();
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>Finds one currently projected game-role entry without resolving its ROM path.</summary>
    public async Task<ArtworkCatalogEntry?> GetByGameIdAsync(
        string libraryId,
        string gameId,
        string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        var normalizedRole = ArtworkCatalogKey.Create(libraryId, "placeholder.rom", "placeholder", role).Role;
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return library.FindByGameRole(gameId, normalizedRole);
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Removes entries absent from a completed reconciliation, repairs stale preview IDs, and
    /// returns no-longer-referenced artifact paths for the caller to delete after persistence.
    /// </summary>
    public async Task<IReadOnlyList<string>> PruneAsync(
        string libraryId,
        IEnumerable<ArtworkCatalogKey> activeKeys,
        CancellationToken cancellationToken = default)
    {
        var normalizedLibraryId = NormalizeLibraryId(libraryId);
        var active = activeKeys.Select(NormalizeKey).Select(key => key.StorageKey).ToHashSet(StringComparer.Ordinal);
        var library = await GetLibraryAsync(normalizedLibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = library.Entries
                .Where(pair => !active.Contains(pair.Key))
                .ToArray();
            var changed = removed.Length > 0;
            var affectedSystems = removed.Select(pair => pair.Value.SystemId).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in removed)
            {
                library.RemoveEntry(pair.Key);
            }

            var liveGameIds = library.Entries.Values
                .Where(entry => !string.IsNullOrWhiteSpace(entry.GameId))
                .GroupBy(entry => entry.SystemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.GameId).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var (systemId, system) in library.Document.Systems.ToArray())
            {
                if (!liveGameIds.TryGetValue(systemId, out var games))
                {
                    library.Document.Systems.Remove(systemId);
                    changed = true;
                    continue;
                }

                var previewIds = system.PreviewGameIds.Where(games.Contains).ToList();
                var candidateIds = system.PreviewCandidateGameIds.Where(games.Contains).ToList();
                if (!system.PreviewGameIds.SequenceEqual(previewIds, StringComparer.Ordinal) ||
                    !system.PreviewCandidateGameIds.SequenceEqual(candidateIds, StringComparer.Ordinal))
                {
                    system.PreviewGameIds = previewIds;
                    system.PreviewCandidateGameIds = candidateIds;
                    affectedSystems.Add(systemId);
                    changed = true;
                }
            }

            if (!changed)
            {
                return Array.Empty<string>();
            }

            foreach (var systemId in affectedSystems)
            {
                if (library.Document.Systems.ContainsKey(systemId))
                {
                    BumpSystemGeneration(library.Document, systemId);
                }
            }

            var stillReferenced = library.Entries.Values
                .SelectMany(entry => new[] { entry.OriginalPath, entry.ThumbnailPath })
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphaned = removed
                .SelectMany(pair => new[] { pair.Value.OriginalPath, pair.Value.ThumbnailPath })
                .Where(path => !string.IsNullOrWhiteSpace(path) && !stillReferenced.Contains(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Written now rather than debounced: the caller deletes these files as soon as this
            // returns, so a crash inside the debounce window would leave entries referencing
            // artwork that no longer exists. Prune runs once per reconciliation, so the cost is
            // one write per library rather than one per entry.
            await WriteUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            return orphaned;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Persists a completed reconciliation's current game-id projection in one atomic catalog
    /// replacement, avoiding one full JSON rewrite per game/artwork role.
    /// </summary>
    public async Task SynchronizeProjectionAsync(
        string libraryId,
        IEnumerable<ArtworkCatalogProjectionEntry> projection,
        string providerVersion,
        ArtworkCatalogSystemMetadata? systemMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);
        var normalizedLibraryId = NormalizeLibraryId(libraryId);
        var entries = projection.Select(item => new ArtworkCatalogProjectionEntry(
            NormalizeKey(item.Key),
            NormalizeSystemId(item.SystemId),
            item.GameId.Trim())).ToArray();
        if (systemMetadata != null)
        {
            systemMetadata = systemMetadata with { SystemId = NormalizeSystemId(systemMetadata.SystemId) };
        }

        var library = await GetLibraryAsync(normalizedLibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var affectedSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (systemMetadata != null)
            {
                if (!library.Document.Systems.TryGetValue(systemMetadata.SystemId, out var system))
                {
                    system = new PersistedSystem { SystemId = systemMetadata.SystemId };
                    library.Document.Systems.Add(systemMetadata.SystemId, system);
                }

                if (!string.Equals(system.Name, systemMetadata.Name, StringComparison.Ordinal) ||
                    !string.Equals(system.Core, systemMetadata.Core, StringComparison.Ordinal) ||
                    system.GameCount != systemMetadata.GameCount)
                {
                    affectedSystems.Add(systemMetadata.SystemId);
                }

                system.Name = systemMetadata.Name;
                system.Core = systemMetadata.Core;
                system.GameCount = systemMetadata.GameCount;
            }
            foreach (var item in entries)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.SystemId);
                ArgumentException.ThrowIfNullOrWhiteSpace(item.GameId);
                if (!library.Entries.TryGetValue(item.Key.StorageKey, out var existing))
                {
                    library.SetEntry(item.Key.StorageKey, NewPending(item.Key, item.SystemId, gameId: item.GameId));
                    affectedSystems.Add(item.SystemId);
                    continue;
                }

                ArtworkCatalogEntry updated = existing;
                if (existing.State == ArtworkCatalogState.Missing &&
                    !string.Equals(existing.MissingProviderVersion, providerVersion, StringComparison.Ordinal))
                {
                    updated = NewPending(item.Key, item.SystemId, existing.Revision, item.GameId);
                }
                else if (!string.Equals(existing.GameId, item.GameId, StringComparison.Ordinal) ||
                    !string.Equals(existing.SystemId, item.SystemId, StringComparison.Ordinal))
                {
                    updated = existing with { GameId = item.GameId, SystemId = item.SystemId };
                }

                if (updated != existing)
                {
                    library.SetEntry(item.Key.StorageKey, updated);
                    affectedSystems.Add(existing.SystemId);
                    affectedSystems.Add(item.SystemId);
                }
            }

            if (affectedSystems.Count == 0)
            {
                return;
            }

            foreach (var systemId in affectedSystems)
            {
                BumpSystemGeneration(library.Document, systemId);
            }

            await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Persists the complete deterministic preview-discovery permutation for an inventory
    /// generation without prematurely publishing any panel. Workers walk this list until they
    /// have confirmed up to four artwork-bearing games, then call
    /// <see cref="CommitPreviewSelectionAsync"/>.
    /// </summary>
    public async Task<ArtworkCatalogSystemSnapshot> GetOrCreatePreviewDiscoveryAsync(
        string libraryId,
        string systemId,
        string inventoryGeneration,
        IEnumerable<string> candidateGameIds,
        CancellationToken cancellationToken = default)
    {
        systemId = NormalizeSystemId(systemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryGeneration);
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (library.Document.Systems.TryGetValue(systemId, out var existing) &&
                string.Equals(existing.InventoryGeneration, inventoryGeneration, StringComparison.Ordinal))
            {
                return Snapshot(existing);
            }

            var candidates = candidateGameIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => ArtworkCatalogKey.Hash(library.Document.LibraryId + "\n" + systemId + "\n" + inventoryGeneration + "\n" + id), StringComparer.Ordinal)
                .ToList();
            var system = new PersistedSystem
            {
                SystemId = systemId,
                Generation = (existing?.Generation ?? 0) + 1,
                InventoryGeneration = inventoryGeneration,
                PreviewCandidateGameIds = candidates,
                PreviewGameIds = new List<string>(),
            };
            library.Document.Systems[systemId] = system;
            await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            return Snapshot(system);
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Publishes only artwork-bearing preview panels already confirmed by a worker. The set is
    /// locked to the stored inventory generation and must be an ordered subset of its discovery
    /// permutation.
    /// </summary>
    public async Task<ArtworkCatalogSystemSnapshot> CommitPreviewSelectionAsync(
        string libraryId,
        string systemId,
        string inventoryGeneration,
        IEnumerable<string> confirmedGameIds,
        CancellationToken cancellationToken = default)
    {
        systemId = NormalizeSystemId(systemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryGeneration);
        var library = await GetLibraryAsync(NormalizeLibraryId(libraryId), cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!library.Document.Systems.TryGetValue(systemId, out var system) ||
                !string.Equals(system.InventoryGeneration, inventoryGeneration, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Preview discovery must be initialized before its selection is committed.");
            }

            var confirmed = confirmedGameIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (confirmed.Any(id => !system.PreviewCandidateGameIds.Contains(id, StringComparer.Ordinal)))
            {
                throw new ArgumentException("Preview panels must come from the persisted discovery order.", nameof(confirmedGameIds));
            }

            if (!system.PreviewGameIds.SequenceEqual(confirmed, StringComparer.Ordinal))
            {
                system.PreviewGameIds = confirmed;
                system.Generation++;
                await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            }

            return Snapshot(system);
        }
        finally
        {
            library.Gate.Release();
        }
    }

    /// <summary>
    /// Writes any library whose in-memory state is ahead of its file. Call this on shutdown, and
    /// wherever the on-disk catalog must be authoritative again.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => FlushLoadedLibrariesAsync(cancellationToken);

    /// <summary>Returns work that must be rebuilt into worker queues during plugin startup.</summary>
    public async Task<IReadOnlyList<ArtworkCatalogWorkItem>> GetStartupRecoveryWorkAsync(CancellationToken cancellationToken = default)
    {
        // This reads the files rather than the loaded documents, and the reconciliation service
        // calls it on every pass, not only at startup. Flushing first keeps the file the single
        // source of truth here, so a debounced transition cannot be re-queued as unfinished work.
        await FlushLoadedLibrariesAsync(cancellationToken).ConfigureAwait(false);

        if (!Directory.Exists(_root))
        {
            return Array.Empty<ArtworkCatalogWorkItem>();
        }

        var files = Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        var work = new List<ArtworkCatalogWorkItem>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ReadDocument(file);
            if (document == null)
            {
                continue;
            }

            foreach (var entry in document.Entries.Values)
            {
                switch (entry.State)
                {
                    case ArtworkCatalogState.Pending:
                    case ArtworkCatalogState.Retryable:
                        work.Add(new ArtworkCatalogWorkItem(entry, ArtworkCatalogWorkStage.AcquireOriginal, entry.RetryAfterUtc));
                        break;
                    case ArtworkCatalogState.OriginalReady when string.IsNullOrWhiteSpace(entry.ThumbnailPath):
                        work.Add(new ArtworkCatalogWorkItem(entry, ArtworkCatalogWorkStage.DeriveThumbnail, entry.RetryAfterUtc));
                        break;
                }
            }
        }

        return work;
    }

    internal string GetLibraryPath(string libraryId) => Path.Combine(_root, ArtworkCatalogKey.Hash(NormalizeLibraryId(libraryId)) + ".json");

    private async Task<ArtworkCatalogEntry> UpdateEntryAsync(
        ArtworkCatalogKey key,
        string systemId,
        Func<ArtworkCatalogEntry, ArtworkCatalogEntry> update,
        CancellationToken cancellationToken)
    {
        key = NormalizeKey(key);
        systemId = NormalizeSystemId(systemId);
        var library = await GetLibraryAsync(key.LibraryId, cancellationToken).ConfigureAwait(false);
        await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!library.Entries.TryGetValue(key.StorageKey, out var entry))
            {
                entry = NewPending(key, systemId);
            }

            entry = update(entry);
            library.SetEntry(key.StorageKey, entry);
            BumpSystemGeneration(library.Document, systemId);
            await MarkDirtyUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            library.Gate.Release();
        }
    }

    private Task<LibraryCatalog> GetLibraryAsync(string normalizedLibraryId, CancellationToken cancellationToken)
    {
        var lazy = _libraries.GetOrAdd(
            normalizedLibraryId,
            id => new Lazy<Task<LibraryCatalog>>(() => LoadLibraryAsync(id), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private Task<LibraryCatalog> LoadLibraryAsync(string libraryId)
    {
        var path = GetLibraryPath(libraryId);
        var document = ReadDocument(path) ?? new PersistedCatalog { LibraryId = libraryId };
        document.LibraryId = NormalizeLibraryId(document.LibraryId);
        document.Entries ??= new Dictionary<string, ArtworkCatalogEntry>(StringComparer.Ordinal);
        document.Systems ??= new Dictionary<string, PersistedSystem>(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new LibraryCatalog(path, document));
    }

    private PersistedCatalog? ReadDocument(string path)
    {
        var document = AtomicFile.ReadWithRecovery(path, json =>
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<PersistedCatalog>(json, _jsonOptions);
                if (parsed is not { SchemaVersion: SchemaVersion } || string.IsNullOrWhiteSpace(parsed.LibraryId))
                {
                    return null;
                }

                parsed.LibraryId = NormalizeLibraryId(parsed.LibraryId);
                parsed.Entries ??= new Dictionary<string, ArtworkCatalogEntry>(StringComparer.Ordinal);
                parsed.Systems ??= new Dictionary<string, PersistedSystem>(StringComparer.OrdinalIgnoreCase);
                MigrateSystemIdCasing(parsed, path);
                return parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        });
        return document;
    }

    /// <summary>
    /// Catalogs written before system id normalization existed may hold entries and system keys
    /// that differ only by case (e.g. a directory later renamed from "SNES" to "snes").
    /// Reconciling this on every load keeps the in-memory document internally consistent without a
    /// one-shot migration step. Colliding systems keep the higher-generation (more recently
    /// mutated) copy; nothing is silently dropped without a log entry.
    /// </summary>
    private void MigrateSystemIdCasing(PersistedCatalog parsed, string path)
    {
        foreach (var storageKey in parsed.Entries.Keys.ToArray())
        {
            var entry = parsed.Entries[storageKey];
            if (string.IsNullOrWhiteSpace(entry.SystemId))
            {
                continue;
            }

            var normalized = NormalizeSystemId(entry.SystemId);
            if (!string.Equals(entry.SystemId, normalized, StringComparison.Ordinal))
            {
                parsed.Entries[storageKey] = entry with { SystemId = normalized };
            }
        }

        var normalizedSystems = new Dictionary<string, PersistedSystem>(StringComparer.OrdinalIgnoreCase);
        var loggedCollision = false;
        foreach (var (rawKey, system) in parsed.Systems)
        {
            var normalizedId = NormalizeSystemId(!string.IsNullOrWhiteSpace(system.SystemId) ? system.SystemId : rawKey);
            system.SystemId = normalizedId;

            if (normalizedSystems.TryGetValue(normalizedId, out var existing))
            {
                var keep = system.Generation >= existing.Generation ? system : existing;
                normalizedSystems[normalizedId] = keep;
                if (!loggedCollision)
                {
                    _logger?.LogWarning(
                        "Catalog {Path} had system id '{RawKey}' colliding with an existing entry after case " +
                        "normalization to '{NormalizedId}'. Kept the entry with generation {Generation}; the other was discarded.",
                        path,
                        rawKey,
                        normalizedId,
                        keep.Generation);
                    loggedCollision = true;
                }
            }
            else
            {
                normalizedSystems[normalizedId] = system;
            }
        }

        parsed.Systems = normalizedSystems;
    }

    /// <summary>
    /// Records that a library's in-memory document is ahead of its file, writing it either now or
    /// on a short debounce.
    ///
    /// Durability: a crash loses at most one debounce window of transitions, and every debounced
    /// transition is re-derivable by <see cref="GetStartupRecoveryWorkAsync"/> plus the next
    /// reconciliation pass. A lost ThumbnailReady leaves the entry OriginalReady with a null
    /// ThumbnailPath, which recovery re-queues as DeriveThumbnail; a lost OriginalReady leaves it
    /// Pending/Retryable, which recovery re-queues as AcquireOriginal; a lost Pending/Retryable
    /// leaves an entry reconciliation recreates via <see cref="GetOrCreateAsync"/>. A lost Missing
    /// costs one repeated provider lookup for the affected entries, after which the miss is durable
    /// again. Because the whole document is written at once, a crash always leaves a consistent
    /// point-in-time prefix rather than a mix of old and new entries.
    ///
    /// The two callers whose durability is not re-derivable — <see cref="PruneAsync"/>, whose
    /// caller deletes the artwork files it reports as orphaned, and <see cref="FlushAsync"/> — go
    /// through <see cref="WriteUnsafeAsync"/> directly instead.
    /// </summary>
    private async Task MarkDirtyUnsafeAsync(LibraryCatalog library, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        library.Dirty = true;

        // Leading edge: an isolated mutation (an explicit refresh, a single retry) is written
        // immediately, so only sustained bursts such as initial reconciliation pay the window.
        var sinceLastWrite = library.Clock.Elapsed - (library.LastWriteCompletedAt ?? TimeSpan.Zero);
        if (library.LastWriteCompletedAt == null || sinceLastWrite >= _persistDebounce)
        {
            await WriteUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
            return;
        }

        ScheduleDeferredFlushUnsafe(library, _persistDebounce - sinceLastWrite);
    }

    /// <summary>Starts the one timer that will write whatever has accumulated when the window closes.</summary>
    private void ScheduleDeferredFlushUnsafe(LibraryCatalog library, TimeSpan delay)
    {
        if (library.DeferredFlushScheduled)
        {
            return;
        }

        library.DeferredFlushScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await library.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Cleared inside the gate and before the write: a mutation that arrives while
                    // this write is in flight must schedule its own timer rather than assume ours
                    // still covers it.
                    library.DeferredFlushScheduled = false;
                    if (library.Dirty)
                    {
                        await WriteUnsafeAsync(library, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    library.Gate.Release();
                }
            }
            catch (Exception ex)
            {
                // The document stays dirty, so the next mutation, FlushAsync or shutdown retries it.
                library.DeferredFlushScheduled = false;
                _logger?.LogWarning(ex, "Deferred game artwork catalog write for {Path} failed", library.Path);
            }
        });
    }

    /// <summary>Serializes and atomically replaces one library file. Callers must hold its gate.</summary>
    private async Task WriteUnsafeAsync(LibraryCatalog library, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(library.Document, _jsonOptions);
        Directory.CreateDirectory(_root);
        await AtomicFile.WriteAllTextAsync(library.Path, json, cancellationToken).ConfigureAwait(false);

        // Left dirty on failure so a later flush retries it.
        library.Dirty = false;
        library.LastWriteCompletedAt = library.Clock.Elapsed;
        Interlocked.Increment(ref _documentWrites);
    }

    /// <summary>Writes every loaded library whose in-memory state is ahead of its file.</summary>
    private async Task FlushLoadedLibrariesAsync(CancellationToken cancellationToken)
    {
        foreach (var lazy in _libraries.Values.ToArray())
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            var library = await lazy.Value.ConfigureAwait(false);
            await library.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (library.Dirty)
                {
                    await WriteUnsafeAsync(library, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                library.Gate.Release();
            }
        }
    }

    // Callers are expected to have already normalized systemId via NormalizeSystemId; this trusts
    // that contract rather than re-normalizing, so a caller that forgets is visible in review.
    private static ArtworkCatalogEntry NewPending(ArtworkCatalogKey key, string systemId, long revision = 0, string? gameId = null) => new()
    {
        Key = key,
        GameId = gameId?.Trim() ?? string.Empty,
        SystemId = systemId,
        State = ArtworkCatalogState.Pending,
        Revision = revision,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static void BumpSystemGeneration(PersistedCatalog document, string systemId)
    {
        if (!document.Systems.TryGetValue(systemId, out var system))
        {
            system = new PersistedSystem { SystemId = systemId };
            document.Systems.Add(systemId, system);
        }

        system.Generation++;
    }

    private static ArtworkCatalogSystemSnapshot Snapshot(PersistedSystem system) =>
        new(system.SystemId, system.Generation, system.InventoryGeneration, system.PreviewGameIds.ToArray())
        {
            PreviewCandidateGameIds = system.PreviewCandidateGameIds.ToArray(),
        };

    private static string NormalizeLibraryId(string libraryId) =>
        ArtworkCatalogKey.Create(libraryId, "placeholder.rom", "placeholder", "boxart").LibraryId;

    /// <summary>
    /// Normalizes a system id the same way <see cref="ArtworkCatalogKey"/> normalizes its own
    /// components (FormKC + invariant lowercase), so a "SNES" directory and a "snes" request from
    /// <c>GamesService</c>'s case-insensitive comparison resolve to the same catalog identity.
    /// </summary>
    private static string NormalizeSystemId(string systemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);
        return systemId.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private static ArtworkCatalogKey NormalizeKey(ArtworkCatalogKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ArtworkCatalogKey.Create(key.LibraryId, key.NormalizedRomPath, key.RomFingerprint, key.Role);
    }

    private static string RequirePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return path;
    }

    private sealed class LibraryCatalog
    {
        /// <summary>
        /// Secondary index over the entry collection, which is keyed by an opaque storage hash and
        /// so cannot answer "which entry holds this game's boxart?" without a full scan. It is
        /// derived state held only in memory and rebuilt from the document on load, so it can never
        /// be stale on disk. Every mutation goes through <see cref="SetEntry"/> or
        /// <see cref="RemoveEntry"/> under <see cref="Gate"/> -- that, plus nothing outside this
        /// class touching <c>Document.Entries</c>, is what keeps it from drifting.
        ///
        /// The value is a list because a game id is not unique across ROM fingerprints: when a ROM
        /// is replaced, reconciliation adds the new fingerprint's entry before the next prune drops
        /// the old one, so the two briefly share a (game id, role). Lists hold one key otherwise.
        /// </summary>
        private readonly Dictionary<(string GameId, string Role), List<string>> _entriesByGameRole = new();

        /// <summary>
        /// Secondary index from <see cref="ArtworkCatalogKey.IdentityWithoutFingerprint"/> to every
        /// storage key sharing that identity. <see cref="GetOrCreateAsync"/> uses this to find stale
        /// entries left behind by a ROM fingerprint change without scanning every entry in the
        /// library. Derived, in-memory only, and rebuilt from the document on load; see the identical
        /// contract on <see cref="_entriesByGameRole"/> for why <see cref="SetEntry"/> and
        /// <see cref="RemoveEntry"/> being the only mutators keeps it from drifting.
        ///
        /// The value is a list because multiple fingerprints legitimately share one identity for the
        /// same reason they share one in <see cref="_entriesByGameRole"/>: a ROM replacement adds the
        /// new fingerprint's entry before the next prune drops the old one.
        /// </summary>
        private readonly Dictionary<string, List<string>> _entriesByIdentityWithoutFingerprint = new(StringComparer.Ordinal);

        public LibraryCatalog(string path, PersistedCatalog document)
        {
            Path = path;
            Document = document;
            foreach (var (storageKey, entry) in document.Entries)
            {
                IndexEntry(storageKey, entry);
            }
        }

        public string Path { get; }

        public PersistedCatalog Document { get; }

        /// <summary>
        /// Read-only view of the entry collection. Mutating callers must use <see cref="SetEntry"/>
        /// and <see cref="RemoveEntry"/> so the secondary indexes stay in step.
        /// </summary>
        public IReadOnlyDictionary<string, ArtworkCatalogEntry> Entries => Document.Entries;

        /// <summary>Adds or replaces one entry, re-indexing whatever it replaced.</summary>
        public void SetEntry(string storageKey, ArtworkCatalogEntry entry)
        {
            if (Document.Entries.TryGetValue(storageKey, out var existing))
            {
                UnindexEntry(storageKey, existing);
            }

            Document.Entries[storageKey] = entry;
            IndexEntry(storageKey, entry);
        }

        /// <summary>Removes one entry, returning whether it was present.</summary>
        public bool RemoveEntry(string storageKey)
        {
            if (!Document.Entries.Remove(storageKey, out var removed))
            {
                return false;
            }

            UnindexEntry(storageKey, removed);
            return true;
        }

        /// <summary>
        /// Resolves one game's entry for a role. Game ids and roles are compared ordinally, exactly
        /// as the linear scan this replaces did; a game id that is not indexed has no entry.
        /// </summary>
        public ArtworkCatalogEntry? FindByGameRole(string gameId, string role)
        {
            if (!_entriesByGameRole.TryGetValue((gameId, role), out var storageKeys))
            {
                return null;
            }

            foreach (var storageKey in storageKeys)
            {
                if (Document.Entries.TryGetValue(storageKey, out var entry))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns every currently stored storage key sharing an identity (library, ROM path, and
        /// role, ignoring fingerprint), or an empty sequence when none are indexed.
        /// </summary>
        public IReadOnlyList<string> FindByIdentityWithoutFingerprint(string identityWithoutFingerprint) =>
            _entriesByIdentityWithoutFingerprint.TryGetValue(identityWithoutFingerprint, out var storageKeys)
                ? storageKeys
                : Array.Empty<string>();

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Document has mutations its file does not. Read and written under <see cref="Gate"/>.</summary>
        public bool Dirty { get; set; }

        /// <summary>Whether a timer is already pending to write this library. Guarded by <see cref="Gate"/>.</summary>
        public bool DeferredFlushScheduled { get; set; }

        /// <summary>
        /// When the last write finished, measured on <see cref="Clock"/>. Null until the first
        /// write, which is what makes that first mutation immediate. Timing the debounce from
        /// completion rather than from the start makes it self-limiting on slow storage.
        /// </summary>
        public TimeSpan? LastWriteCompletedAt { get; set; }

        /// <summary>Monotonic clock; the wall clock can step and would break the debounce window.</summary>
        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        private void IndexEntry(string storageKey, ArtworkCatalogEntry entry)
        {
            var identityKey = entry.Key.IdentityWithoutFingerprint;
            if (!_entriesByIdentityWithoutFingerprint.TryGetValue(identityKey, out var identityKeys))
            {
                _entriesByIdentityWithoutFingerprint.Add(identityKey, new List<string>(1) { storageKey });
            }
            else if (!identityKeys.Contains(storageKey, StringComparer.Ordinal))
            {
                identityKeys.Add(storageKey);
            }

            // An entry with no game id is not yet projected to clients and can only be reached by
            // its catalog key, so it is deliberately absent from the game-role index.
            if (string.IsNullOrEmpty(entry.GameId))
            {
                return;
            }

            var indexKey = (entry.GameId, entry.Key.Role);
            if (!_entriesByGameRole.TryGetValue(indexKey, out var storageKeys))
            {
                _entriesByGameRole.Add(indexKey, new List<string>(1) { storageKey });
                return;
            }

            if (!storageKeys.Contains(storageKey, StringComparer.Ordinal))
            {
                storageKeys.Add(storageKey);
            }
        }

        private void UnindexEntry(string storageKey, ArtworkCatalogEntry entry)
        {
            var identityKey = entry.Key.IdentityWithoutFingerprint;
            if (_entriesByIdentityWithoutFingerprint.TryGetValue(identityKey, out var identityKeys))
            {
                identityKeys.Remove(storageKey);
                if (identityKeys.Count == 0)
                {
                    _entriesByIdentityWithoutFingerprint.Remove(identityKey);
                }
            }

            if (string.IsNullOrEmpty(entry.GameId))
            {
                return;
            }

            var indexKey = (entry.GameId, entry.Key.Role);
            if (!_entriesByGameRole.TryGetValue(indexKey, out var storageKeys))
            {
                return;
            }

            storageKeys.Remove(storageKey);
            if (storageKeys.Count == 0)
            {
                _entriesByGameRole.Remove(indexKey);
            }
        }
    }

    private sealed class PersistedCatalog
    {
        public int SchemaVersion { get; set; } = GameArtworkCatalog.SchemaVersion;

        public string LibraryId { get; set; } = string.Empty;

        public Dictionary<string, ArtworkCatalogEntry> Entries { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, PersistedSystem> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PersistedSystem
    {
        public string SystemId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Core { get; set; } = string.Empty;

        public int GameCount { get; set; }

        public long Generation { get; set; }

        public string? InventoryGeneration { get; set; }

        public List<string> PreviewGameIds { get; set; } = new();

        public List<string> PreviewCandidateGameIds { get; set; } = new();
    }
}
