using MediaBrowser.Controller.Drawing;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Compatibility façade for game artwork acquisition and derived-thumbnail generation.
/// </summary>
public class GameThumbService
{
    /// <summary>The libretro thumbnail folder for a kind of art.</summary>
    public enum ThumbKind
    {
        Boxart,
        Snap,
        Title
    }

    private readonly GameArtworkStore _artworkStore;
    private readonly DerivedThumbnailService _derivedThumbnailService;

    internal event Action<string>? MetadataIndexAvailable
    {
        add
        {
            if (_artworkStore != null)
            {
                _artworkStore.MetadataIndexAvailable += value;
            }
        }
        remove
        {
            if (_artworkStore != null)
            {
                _artworkStore.MetadataIndexAvailable -= value;
            }
        }
    }

    public GameThumbService(
        IHttpClientFactory httpClientFactory,
        ILogger<GameThumbService> logger,
        RdbService rdbService,
        IImageEncoder? imageEncoder = null)
        : this(
            httpClientFactory,
            logger,
            rdbService,
            imageEncoder,
            null,
            null,
            ResolveCacheDirectory())
    {
    }

    // Test seam for pointing the cache dir at a fixture folder, and for shrinking the derived-thumb
    // encode's concurrency cap/wait budget so tests can exercise both deterministically instead of
    // needing real concurrent load or a real multi-second wait. Neither override has a production
    // effect (both are null unless a test passes them).
    internal GameThumbService(
        IHttpClientFactory httpClientFactory,
        ILogger<GameThumbService> logger,
        RdbService rdbService,
        string dataFolderPath,
        IImageEncoder? imageEncoder = null,
        int? encodeConcurrencyForTests = null,
        TimeSpan? derivedThumbBudgetForTests = null)
        : this(
            httpClientFactory,
            logger,
            rdbService,
            imageEncoder,
            encodeConcurrencyForTests,
            derivedThumbBudgetForTests,
            CacheDirectoryFor(dataFolderPath))
    {
    }

    public static ThumbKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "snap" => ThumbKind.Snap,
        "title" => ThumbKind.Title,
        _ => ThumbKind.Boxart
    };

    /// <summary>
    /// The cached file for a game's art, downloading it first when needed. Null when no candidate
    /// platform has the name, or every download failed. This compatibility API deliberately
    /// retains its historical null result for transient provider failures.
    /// </summary>
    public Task<string?> GetThumbPathAsync(
        string core,
        bool coreWasDefaulted,
        string systemName,
        string romPath,
        string? title,
        ThumbKind kind,
        CancellationToken cancellationToken = default) =>
        _artworkStore.GetThumbPathAsync(core, coreWasDefaulted, systemName, romPath, title, kind, cancellationToken);

    /// <summary>
    /// Performs a state-aware artwork lookup for background workers. Unlike <see cref="GetThumbPathAsync"/>,
    /// this preserves the difference between confirmed absence and a retryable provider failure,
    /// and runs on the prewarm budget because no client is waiting on it.
    /// </summary>
    internal Task<GameArtworkLookupResult> LookupThumbAsync(
        string core,
        bool coreWasDefaulted,
        string systemName,
        string romPath,
        string? title,
        ThumbKind kind,
        CancellationToken cancellationToken = default) =>
        _artworkStore.LookupThumbAsync(core, coreWasDefaulted, systemName, romPath, title, kind, cancellationToken, GameArtworkStore.PrewarmRequestBudget);

    /// <summary>
    /// Gets the cached size/format-optimized thumbnail derived from an already-acquired original.
    /// Falls back to the original when encoding is unsupported or unavailable.
    /// </summary>
    public Task<DerivedThumbnail> GetDerivedThumbAsync(string originalPath, CancellationToken cancellationToken) =>
        _derivedThumbnailService.GetDerivedThumbAsync(originalPath, cancellationToken);

    private GameThumbService(
        IHttpClientFactory httpClientFactory,
        ILogger<GameThumbService> logger,
        RdbService rdbService,
        IImageEncoder? imageEncoder,
        int? encodeConcurrencyForTests,
        TimeSpan? derivedThumbBudgetForTests,
        string cacheDirectory)
    {
        _artworkStore = new GameArtworkStore(httpClientFactory, logger, rdbService, cacheDirectory);
        _derivedThumbnailService = new DerivedThumbnailService(
            logger,
            cacheDirectory,
            imageEncoder,
            encodeConcurrencyForTests,
            derivedThumbBudgetForTests);
    }

    private static string ResolveCacheDirectory()
    {
        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");
        return Path.Combine(dataPath, "game_thumbs");
    }

    private static string CacheDirectoryFor(string dataFolderPath)
    {
        ArgumentNullException.ThrowIfNull(dataFolderPath);
        return Path.Combine(dataFolderPath, "game_thumbs");
    }
}

/// <summary>
/// Why <see cref="DerivedThumbnail.Path"/> points where it does. Callers that persist the result
/// (the reconciliation worker) need this to tell a genuine thumbnail apart from the original being
/// served in its place -- and, when it is the original, whether that is because encoding is
/// permanently unsupported for this image (durable) or because the encode simply ran out of its
/// wall-clock budget (transient, and worth retrying).
/// </summary>
public enum DerivedThumbnailOutcome
{
    /// <summary>A real, smaller/re-encoded thumbnail was produced at <see cref="DerivedThumbnail.Path"/>.</summary>
    Encoded,

    /// <summary>
    /// The encode did not finish inside its budget. <see cref="DerivedThumbnail.Path"/> is the
    /// original, served for now, but the encode may still complete in the background -- the caller
    /// should re-check later rather than recording this as a final answer.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The original cannot or should not be encoded (no encoder configured, no supported output
    /// format, a decode-bomb-sized source, or the encoder itself rejected it). This is a durable
    /// answer: retrying will not change it.
    /// </summary>
    Unsupported,
}

/// <summary>A cached thumbnail file, the media type the controller must serve, and why.</summary>
public sealed record DerivedThumbnail(string Path, string ContentType, DerivedThumbnailOutcome Outcome)
{
    internal static DerivedThumbnail Encoded(string path, string contentType) =>
        new(path, contentType, DerivedThumbnailOutcome.Encoded);

    internal static DerivedThumbnail TimedOut(string originalPath) =>
        new(originalPath, "image/png", DerivedThumbnailOutcome.TimedOut);

    internal static DerivedThumbnail Unsupported(string originalPath) =>
        new(originalPath, "image/png", DerivedThumbnailOutcome.Unsupported);
}

/// <summary>State-aware result for artwork acquisition outside the legacy request-path API.</summary>
internal sealed record GameArtworkLookupResult(
    GameArtworkLookupOutcome Outcome,
    string? Path = null,
    TimeSpan? RetryDelay = null,
    bool TimedOut = false)
{
    public static GameArtworkLookupResult Found(string path) => new(GameArtworkLookupOutcome.Found, path);

    public static GameArtworkLookupResult Missing() => new(GameArtworkLookupOutcome.Missing);

    public static GameArtworkLookupResult TransientFailure(TimeSpan? retryDelay = null, bool timedOut = false) =>
        new(GameArtworkLookupOutcome.TransientFailure, RetryDelay: retryDelay, TimedOut: timedOut);

    public static GameArtworkLookupResult MetadataPending() => new(GameArtworkLookupOutcome.MetadataPending);
}

/// <summary>Whether artwork was found, exhaustively confirmed absent, or requires a later retry.</summary>
internal enum GameArtworkLookupOutcome
{
    Found,
    Missing,
    TransientFailure,
    MetadataPending,
}

/// <summary>
/// A thumbnail lookup ran out of its wall-clock budget before it could decide whether art exists.
/// Deliberately distinct from "no art found": the caller must answer with a retryable status, not
/// a 404, or a client will cache the miss and never ask again for a game that does have art.
/// </summary>
public sealed class ThumbLookupTimedOutException : Exception
{
    public ThumbLookupTimedOutException(string romPath, TimeSpan budget)
        : base($"Thumbnail lookup for '{romPath}' exceeded its {budget.TotalSeconds}s budget.")
    {
        RomPath = romPath;
        Budget = budget;
    }

    public string RomPath { get; }

    public TimeSpan Budget { get; }
}
