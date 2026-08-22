using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Exposes a normalized retro-games model (systems / games / ROM + BIOS streaming) for
/// Moonfin clients. ROM files are read straight off disk from the library's physical roots
/// because they are not indexed by Jellyfin as media items.
/// </summary>
/// <remarks>
/// Accepted risk (M18, reviewed 2026-08-04): per-user library ACL is not enforced on the
/// Games/Artwork endpoints in this controller. Any authenticated user can reach any game
/// library's systems, games, ROM streams, and artwork, regardless of Jellyfin's per-user
/// library access grants. This is pre-existing, was reviewed as part of the MAME emulator
/// prototype branch review, and was consciously accepted rather than fixed as part of that
/// work. Adding a new endpoint to this controller does not change the decision, but it does
/// extend its blast radius - any addition should raise the ACL question with the repo owner
/// rather than silently inheriting the gap.
/// </remarks>
[ApiController]
[Route("Moonfin/Games")]
public class GamesController : ControllerBase
{
    private const int MaxArtworkPriorityItems = 128;
    private static readonly TimeSpan OriginalManifestRefresh = TimeSpan.FromSeconds(30);
    private readonly GamesService _gamesService;
    private readonly CoresService _coresService;
    private readonly GameArtworkReconciliationService _artwork;
    private readonly GameArtworkDeliveryLimiter _artworkDeliveryLimiter;
    private readonly ArcadeCompatibilityService _arcadeCompatibility;

    public GamesController(
        GamesService gamesService,
        CoresService coresService,
        GameArtworkReconciliationService artwork,
        GameArtworkDeliveryLimiter artworkDeliveryLimiter,
        ArcadeCompatibilityService arcadeCompatibility)
    {
        _gamesService = gamesService;
        _coresService = coresService;
        _artwork = artwork;
        _artworkDeliveryLimiter = artworkDeliveryLimiter;
        _arcadeCompatibility = arcadeCompatibility;
    }

    /// <summary>Diagnostic dump for troubleshooting library detection (admin only).</summary>
    [HttpGet("Debug")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Debug()
    {
        return Ok(_gamesService.GetDiagnostics());
    }

    /// <summary>Lists the libraries Moonbase treats as game (ROM) libraries.</summary>
    [HttpGet("Libraries")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<GameLibrary>> GetLibraries()
    {
        if (!GamesEnabled())
        {
            return Ok(Array.Empty<GameLibrary>());
        }

        return Ok(_gamesService.GetGameLibraries());
    }

    /// <summary>Lists the top-level system folders inside a game library.</summary>
    [HttpGet("{libraryId}/Systems")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<GameSystem>>> GetSystems(
        [FromRoute] string libraryId,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return Ok(Array.Empty<GameSystem>());
        }

        var systems = (await _artwork.GetSystemsAsync(libraryId, cancellationToken).ConfigureAwait(false)).ToList();

        // One catalog pass for every system on the page. Reading them one at a time re-walked the
        // whole catalog per system, so a library's browse request cost systems x entries.
        var artworkBySystem = await _artwork
            .GetSystemArtworkBatchAsync(libraryId, systems.Select(system => system.Id), cancellationToken)
            .ConfigureAwait(false);
        foreach (var system in systems)
        {
            if (!artworkBySystem.TryGetValue(system.Id, out var artwork) || artwork.System.PreviewGameIds.Count == 0)
            {
                continue;
            }

            var panels = BuildPreviewPanels(libraryId, artwork);
            if (panels.Count > 0)
            {
                system.PreviewArtwork = new GameSystemPreviewArtwork
                {
                    SelectionGeneration = artwork.System.InventoryGeneration ?? ArtworkGeneration(artwork.System.Generation),
                    Panels = panels,
                };
            }
        }

        return Ok(systems);
    }

    /// <summary>
    /// Projects one system's durable preview choice into renderable panels, in the persisted order.
    /// The renderable boxart entries are indexed once rather than rescanned per panel.
    /// </summary>
    private List<GameSystemPreviewPanel> BuildPreviewPanels(string libraryId, GameArtworkSystemReadResult artwork)
    {
        var renderableBoxart = new Dictionary<string, GameArtworkReadEntry>(StringComparer.Ordinal);
        foreach (var entry in artwork.Entries)
        {
            if (string.Equals(entry.Role, "boxart", StringComparison.Ordinal) && IsRenderable(entry.Entry))
            {
                // TryAdd, not the indexer: the replaced FirstOrDefault took the first match.
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

    /// <summary>Advertises the additive, manifest-driven artwork protocol for newer clients.</summary>
    [HttpGet("ArtworkCapabilities")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GameArtworkCapabilities> GetArtworkCapabilities() => Ok(new GameArtworkCapabilities
    {
        ProtocolVersion = 2,
        Manifest = true,
        VersionedAssets = true,
        PriorityHints = true,
        SystemPreviews = true,
    });

    /// <summary>Returns one system's locally cataloged artwork state without scheduling provider work.</summary>
    [HttpGet("{libraryId}/ArtworkManifest")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetArtworkManifest(
        [FromRoute] string libraryId,
        [FromQuery] string? system,
        [FromQuery] string? generation,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled() || string.IsNullOrWhiteSpace(system))
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        var artwork = await _artwork.GetSystemArtworkAsync(libraryId, system, cancellationToken).ConfigureAwait(false);
        if (artwork == null)
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        // Two conditional-request mechanisms exist here on purpose, for now: the `generation`
        // query parameter (below) is the one an already-shipped old client actually sends and is
        // therefore authoritative; the `ETag` header is emitted for standard-HTTP-cache clients
        // but nothing in this codebase currently reads it back via If-None-Match. Consolidating
        // onto one (dropping the query param once no supported client needs it, or wiring up
        // If-None-Match instead) is deferred: it needs to land together with manifest pagination
        // (Task D8, which changes what a "page" of this response even is) and a separate pending
        // change to when `Generation` is bumped. Removing either mechanism unilaterally before
        // those land would be an unreviewed protocol break for the old client.
        var currentGeneration = ArtworkGeneration(artwork.System.Generation);
        Response.Headers["ETag"] = "\"" + currentGeneration + "\"";
        if (string.Equals(generation, currentGeneration, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

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
        return Ok(new GameArtworkManifest { Generation = currentGeneration, Entries = entries });
    }

    /// <summary>Promotes already cataloged artwork work in client-provided priority order.</summary>
    [HttpPost("{libraryId}/ArtworkPriority")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostArtworkPriority(
        [FromRoute] string libraryId,
        [FromBody] GameArtworkPriorityRequest request,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        if (request == null || string.IsNullOrWhiteSpace(request.SystemId) || request.Items == null)
        {
            return BadRequest(new { error = "A systemId is required." });
        }

        if (request.Items.Count > MaxArtworkPriorityItems)
        {
            return BadRequest(new { error = $"At most {MaxArtworkPriorityItems} artwork priority items may be submitted at once." });
        }

        var system = await _artwork.GetSystemArtworkAsync(libraryId, request.SystemId, cancellationToken).ConfigureAwait(false);
        if (system == null)
        {
            return NotFound();
        }

        if (!string.Equals(request.KnownGeneration, ArtworkGeneration(system.System.Generation), StringComparison.Ordinal))
        {
            return BadRequest(new { error = "The artwork generation is no longer current." });
        }

        // One snapshot for the whole request. Membership and promotion both used to resolve each
        // game against an uncached recursive walk of the ROM library, so a maximal 128-item x
        // 3-role body cost 512 full enumerations -- an authenticated denial of service on a large
        // or network-mounted library. The decisions below are unchanged; only their cost is.
        var lookup = _artwork.CreateGameLookup(libraryId);

        var seenGames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.GameId) || !seenGames.Add(item.GameId) ||
                !system.GameIds.Contains(item.GameId, StringComparer.Ordinal) ||
                !_artwork.IsCurrentGameMember(libraryId, item.GameId, lookup) ||
                item.Roles == null || item.Roles.Count == 0)
            {
                return BadRequest(new { error = "Priority items must name distinct games in the requested system and at least one role." });
            }

            var seenRoles = new HashSet<string>(StringComparer.Ordinal);
            if (item.Roles.Any(role => !IsArtworkRole(role) || !seenRoles.Add(role)))
            {
                return BadRequest(new { error = "Artwork roles must be distinct boxart, snap, or title values." });
            }
        }

        foreach (var item in request.Items)
        {
            foreach (var role in item.Roles)
            {
                await _artwork.PromoteAsync(libraryId, item.GameId, role, lookup, cancellationToken).ConfigureAwait(false);
            }
        }

        return NoContent();
    }

    /// <summary>Serves one revisioned local artwork artifact without changing catalog or queue state.</summary>
    [HttpGet("{libraryId}/Artwork/{gameId}/{role}/{revision}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetArtwork(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        [FromRoute] string role,
        [FromRoute] string revision,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        if (!IsArtworkRole(role))
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        var artwork = await _artwork.GetArtworkAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false);
        if (artwork == null)
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        if (artwork.Entry.State == ArtworkCatalogState.Missing)
        {
            return ArtworkNotFound(confirmedMissing: true);
        }

        // Resolved from the entry already read above: re-resolving here cost a second catalog
        // lookup for every thumbnail in a grid.
        var asset = _artwork.ResolveLocalArtwork(artwork.Entry, revision);
        return asset == null
            ? ArtworkNotFound(confirmedMissing: false)
            : await ServeArtworkAsync(asset, versionedUrl: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Administrator-only repair path that deliberately reopens and schedules one artifact.</summary>
    [HttpPost("{libraryId}/Artwork/{gameId}/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefreshArtwork(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        [FromQuery] string? type,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var role = NormalizeLegacyRole(type);
        if (type != null && !IsArtworkRole(type))
        {
            return BadRequest(new { error = "Artwork type must be boxart, snap, or title." });
        }

        return await _artwork.RequestRefreshAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false)
            ? Accepted()
            : NotFound();
    }

    /// <summary>Lists the games inside a library, optionally filtered to one system.</summary>
    [HttpGet("{libraryId}/Games")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<GameSummary>> GetGames(
        [FromRoute] string libraryId,
        [FromQuery] string? system)
    {
        if (!GamesEnabled())
        {
            return Ok(Array.Empty<GameSummary>());
        }

        return Ok(_gamesService.GetGames(libraryId, system));
    }

    /// <summary>Resolves a single game's full detail (ROM, core, BIOS files).</summary>
    [HttpGet("{libraryId}/Games/{gameId}")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameDetail>> GetGame(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var game = await _gamesService
            .GetGameAsync(libraryId, gameId, this.GetUserIdFromClaims(), cancellationToken)
            .ConfigureAwait(false);
        return game == null ? NotFound() : Ok(game);
    }

    /// <summary>
    /// Sets or clears the current user's explicit arcade emulator choice. A null core returns the
    /// game to its server-selected recommendation.
    /// </summary>
    [HttpPut("{libraryId}/Games/{gameId}/Core")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameDetail>> PutCoreOverride(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        [FromBody] GameCoreOverrideRequest request,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        try
        {
            var game = await _gamesService.SetCoreOverrideAsync(
                libraryId,
                gameId,
                userId.Value,
                request.Core,
                cancellationToken).ConfigureAwait(false);
            return game == null ? NotFound() : Ok(game);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "The requested core is not available for this game." });
        }
    }

    /// <summary>
    /// Sets or clears the current user's explicit player backend. EmulatorJS is a player backend
    /// rather than a libretro core, so this is available for every game system.
    /// </summary>
    [HttpPut("{libraryId}/Games/{gameId}/Backend")]
    [Authorize]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GameDetail>> PutBackendOverride(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        [FromBody] GameBackendOverrideRequest request,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        try
        {
            var game = await _gamesService.SetBackendOverrideAsync(
                libraryId,
                gameId,
                userId.Value,
                request.Backend,
                cancellationToken).ConfigureAwait(false);
            return game == null ? NotFound() : Ok(game);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "The requested game backend is not available." });
        }
    }

    /// <summary>Shows whether the locally pinned FBNeo/MAME DAT snapshots are installed.</summary>
    [HttpGet("ArcadeCompatibility/Status")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<ArcadeCompatibilityStatus> GetArcadeCompatibilityStatus() => Ok(_arcadeCompatibility.GetStatus());

    // Real mame -listxml DATs run 200 MB+; FBNeo's is smaller but still substantial and
    // growing. This is an explicit ceiling rather than disabling the limit outright, so the
    // maximum accepted DAT size is a documented decision instead of an accident. Note: Jellyfin
    // owns the Kestrel host configuration, so a server-level MaxRequestBodySize lower than this
    // value can still override it and cause uploads to fail before this action ever runs.
    private const long MaxDatUploadBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Replaces one local compatibility DAT with an administrator-supplied, version-pinned XML
    /// snapshot. This endpoint deliberately never downloads upstream data itself.
    /// </summary>
    [HttpPut("ArcadeCompatibility/{core}/Dat")]
    [Authorize(Policy = "RequiresElevation")]
    [Consumes("application/xml", "text/xml")]
    [RequestSizeLimit(MaxDatUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxDatUploadBytes)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> PutArcadeCompatibilityDat(
        [FromRoute] string core,
        CancellationToken cancellationToken)
    {
        try
        {
            await _arcadeCompatibility.InstallDatAsync(core, Request.Body, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (EmptyArcadeDatException)
        {
            return BadRequest(new { error = "The uploaded DAT contains no usable game or machine sets." });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "Use 'arcade' for FBNeo or 'mame' for MAME." });
        }
        catch (System.Xml.XmlException)
        {
            return BadRequest(new { error = "The uploaded DAT is not valid XML." });
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (IsRequestTooLarge(ex))
        {
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { error = $"The uploaded DAT exceeds the {MaxDatUploadBytes / (1024 * 1024)} MB limit for this endpoint." });
        }
    }

    /// <summary>
    /// <see cref="Microsoft.AspNetCore.Http.BadHttpRequestException"/> is Kestrel's generic
    /// "something is wrong with this request" exception; it is also thrown for malformed
    /// requests unrelated to size. Its <c>StatusCode</c> is 413 specifically when the body (or a
    /// multipart section) exceeded the configured limit, so that is what distinguishes a
    /// too-large upload from any other bad request here.
    /// </summary>
    private static bool IsRequestTooLarge(Microsoft.AspNetCore.Http.BadHttpRequestException ex) =>
        ex.StatusCode == StatusCodes.Status413PayloadTooLarge;

    /// <summary>
    /// Streams a ROM file. EmulatorJS fetches this via XHR, and clients append the Jellyfin
    /// access token as an <c>ApiKey</c> query parameter so the WebView request authenticates.
    /// HEAD is answered too: EmulatorJS compares the size against its cached copy before
    /// reusing it, and treats a refusal as a fatal error rather than a cache miss.
    /// </summary>
    [HttpGet("{libraryId}/Rom/{token}")]
    [HttpGet("{libraryId}/Rom/{token}/{fileName}")]
    [HttpHead("{libraryId}/Rom/{token}")]
    [HttpHead("{libraryId}/Rom/{token}/{fileName}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public IActionResult GetRom(
        [FromRoute] string libraryId,
        [FromRoute] string token,
        [FromRoute] string? fileName = null)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        // The optional filename gives filename-sensitive emulators (MAME) the original archive
        // name in the URL. It is deliberately never used for path resolution or authorization.
        _ = fileName;
        var path = _gamesService.ResolveFilePath(libraryId, token, allowBios: false);
        if (!string.IsNullOrEmpty(path) && GamesService.IsArchive(path))
        {
            if (_gamesService.ShouldPreserveArchive(libraryId, path))
            {
                return StreamFile(path);
            }

            return HttpMethods.IsHead(Request.Method)
                ? ExtractedRomSize(path)
                : StreamExtractedRom(path);
        }

        return StreamFile(path);
    }

    /// <summary>
    /// Compatibility endpoint for old clients. This is catalog-only: it can promote existing
    /// work, but remote acquisition and thumbnail encoding remain owned by background workers.
    /// </summary>
    [HttpGet("{libraryId}/Thumb/{gameId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetThumb(
        [FromRoute] string libraryId,
        [FromRoute] string gameId,
        [FromQuery] string? type,
        [FromQuery] string? full,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return ArtworkNotFound(confirmedMissing: false);
        }

        var role = NormalizeLegacyRole(type);
        var artwork = await _artwork.GetArtworkAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false);
        if (artwork == null)
        {
            // Startup reconciliation may not have reached this game yet. Preserve legacy
            // availability by creating/promoting local catalog work, but never do provider
            // lookup or encoding on the HTTP request path.
            if (await _artwork.RequestRefreshAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false))
            {
                return RetryableArtworkResponse(StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(5));
            }

            return ArtworkNotFound(confirmedMissing: false);
        }

        if (artwork.Entry.State == ArtworkCatalogState.Missing)
        {
            return ArtworkNotFound(confirmedMissing: true);
        }

        if (artwork.Entry.State == ArtworkCatalogState.Pending)
        {
            await _artwork.PromoteAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false);
            return RetryableArtworkResponse(StatusCodes.Status503ServiceUnavailable, TimeSpan.FromSeconds(5));
        }

        if (artwork.Entry.State == ArtworkCatalogState.Retryable)
        {
            await _artwork.PromoteAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false);
            var retry = artwork.Entry.RetryAfterUtc - DateTimeOffset.UtcNow;
            return RetryableArtworkResponse(StatusCodes.Status504GatewayTimeout, retry.GetValueOrDefault(TimeSpan.FromSeconds(5)));
        }

        // A current thumbnail may be bypassed for old callers that explicitly request the
        // original. Both variants remain local-only and are selected before cache headers.
        var asset = _artwork.ResolveLocalArtwork(
            artwork.Entry,
            artwork.Entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WantsFullResolution(full));
        return asset == null
            ? ArtworkNotFound(confirmedMissing: false)
            : await ServeArtworkAsync(asset, versionedUrl: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True for <c>full=1</c>/<c>full=true</c> (any other non-empty, non-"0"/"false" value also
    /// counts, so this is lenient about exactly how a caller spells "yes"). Missing, empty,
    /// "0", or "false" all mean "give me the default derived thumbnail".
    /// </summary>
    private static bool WantsFullResolution(string? full) =>
        !string.IsNullOrEmpty(full)
        && full != "0"
        && !string.Equals(full, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Serves one local artwork asset. <paramref name="versionedUrl"/> distinguishes
    /// <see cref="GetArtwork"/>'s <c>{revision}</c>-qualified route -- where a stale cache entry
    /// simply stops being addressed once the server advertises a new revision -- from
    /// <see cref="GetThumb"/>'s legacy, unversioned route, where the URL never changes and an
    /// <c>immutable</c> response would tell old clients to never even ask again after an
    /// administrator refreshes the art.
    /// </summary>
    private async Task<IActionResult> ServeArtworkAsync(GameArtworkLocalAsset asset, bool versionedUrl, CancellationToken cancellationToken)
    {
        IDisposable lease;
        try
        {
            lease = await _artworkDeliveryLimiter.AcquireAsync(this.GetUserIdFromClaims(), cancellationToken).ConfigureAwait(false);
        }
        catch (ArtworkDeliveryUnavailableException ex)
        {
            return RetryableArtworkResponse(StatusCodes.Status503ServiceUnavailable, ex.RetryAfter);
        }

        var abortRegistration = HttpContext.RequestAborted.Register(lease.Dispose);
        Response.OnCompleted(() =>
        {
            abortRegistration.Dispose();
            lease.Dispose();
            return Task.CompletedTask;
        });
        Response.Headers["Cache-Control"] = SelectArtworkCacheControl(versionedUrl, asset.IsThumbnail);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(asset.Path, asset.ContentType);
    }

    /// <summary>
    /// Picks the artwork <c>Cache-Control</c> value. Versioned URLs (the <c>{revision}</c>
    /// route) may be cached as <c>immutable</c> because a stale copy simply falls out of use
    /// once the revision segment changes. The legacy, unversioned thumb route has no such
    /// signal, so it gets a short, must-revalidate lifetime instead -- <c>PhysicalFile</c>
    /// already emits a Last-Modified header and honors conditional GETs, so revalidation is a
    /// cheap 304, not a re-fetch.
    /// </summary>
    internal static string SelectArtworkCacheControl(bool versionedUrl, bool isThumbnail) =>
        versionedUrl && isThumbnail
            ? "private,max-age=31536000,immutable"
            : versionedUrl
                ? "private,max-age=7200"
                : "private,max-age=300,must-revalidate";

    private IActionResult RetryableArtworkResponse(int statusCode, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(Math.Max(0, retryAfter.TotalSeconds)));
        Response.Headers["Retry-After"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["Cache-Control"] = "no-store";
        return StatusCode(statusCode);
    }

    private IActionResult ArtworkNotFound(bool confirmedMissing)
    {
        Response.Headers["Cache-Control"] = confirmedMissing ? "private,max-age=300" : "no-store";
        return NotFound();
    }

    private static GameArtworkDescriptor ToDescriptor(string libraryId, string gameId, string role, ArtworkCatalogEntry entry)
    {
        var descriptor = new GameArtworkDescriptor { State = ArtworkState(entry.State) };
        if (IsRenderable(entry))
        {
            var revision = entry.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            descriptor.Revision = revision;
            descriptor.Url = $"/Moonfin/Games/{Uri.EscapeDataString(libraryId)}/Artwork/{Uri.EscapeDataString(gameId)}/{role}/{revision}";
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

    private static bool IsRenderable(ArtworkCatalogEntry entry) =>
        entry.State switch
        {
            ArtworkCatalogState.OriginalReady => !string.IsNullOrWhiteSpace(entry.OriginalPath),
            ArtworkCatalogState.ThumbnailReady => !string.IsNullOrWhiteSpace(entry.ThumbnailPath) || !string.IsNullOrWhiteSpace(entry.OriginalPath),
            _ => false,
        };

    private static string ArtworkState(ArtworkCatalogState state) => state switch
    {
        ArtworkCatalogState.OriginalReady => "originalReady",
        ArtworkCatalogState.ThumbnailReady => "thumbnailReady",
        ArtworkCatalogState.Missing => "missing",
        ArtworkCatalogState.Retryable => "transientFailure",
        _ => "pending",
    };

    private static string ArtworkGeneration(long generation) =>
        "artwork-" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string NormalizeLegacyRole(string? type) =>
        string.Equals(type, "snap", StringComparison.OrdinalIgnoreCase) ? "snap" :
        string.Equals(type, "title", StringComparison.OrdinalIgnoreCase) ? "title" : "boxart";

    private static bool IsArtworkRole(string? role) =>
        string.Equals(role, "boxart", StringComparison.Ordinal) ||
        string.Equals(role, "snap", StringComparison.Ordinal) ||
        string.Equals(role, "title", StringComparison.Ordinal);

    /// <summary>Streams a BIOS file required by a system's core, HEAD included for the same
    /// cache check EmulatorJS runs on ROMs.</summary>
    [HttpGet("{libraryId}/Bios/{token}")]
    [HttpHead("{libraryId}/Bios/{token}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetBios([FromRoute] string libraryId, [FromRoute] string token)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var path = _gamesService.ResolveFilePath(libraryId, token, allowBios: true);
        return StreamFile(path);
    }

    /// <summary>Reports whether self-hosted EmulatorJS cores are installed or downloading.</summary>
    [HttpGet("Cores/Status")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CoresStatus> GetCoresStatus()
    {
        return Ok(_coresService.GetStatus());
    }

    /// <summary>
    /// Admin action: downloads a cores zip (from a configured URL or the plugin's GitHub
    /// release) in the background and installs it. Returns immediately; poll <c>Cores/Status</c>.
    /// </summary>
    [HttpPost("Cores/Install")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult<CoresStatus> InstallCores()
    {
        _coresService.StartInstall();
        return Accepted(_coresService.GetStatus());
    }

    /// <summary>
    /// Admin action: installs cores from an uploaded zip (the raw request body). The upload is
    /// streamed to a temp file, then extracted in the background; poll <c>Cores/Status</c>.
    /// </summary>
    [HttpPost("Cores/Upload")]
    [Authorize(Policy = "RequiresElevation")]
    [DisableRequestSizeLimit]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CoresStatus>> UploadCores(CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"emulatorjs-upload-{Guid.NewGuid():N}.zip");
        await using (var dst = System.IO.File.Create(tempFile))
        {
            await Request.Body.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }

        if (new FileInfo(tempFile).Length == 0)
        {
            try { System.IO.File.Delete(tempFile); } catch { /* ignore */ }
            return BadRequest(new { Error = "Empty upload." });
        }

        _coresService.StartInstallFromFile(tempFile);
        return Accepted(_coresService.GetStatus());
    }

    private IActionResult StreamFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        // enableRangeProcessing lets EmulatorJS resume / seek large ROM downloads.
        return PhysicalFile(path, "application/octet-stream", enableRangeProcessing: true);
    }

    // Unpacks a single-ROM .zip/.7z in memory so the client gets raw ROM bytes, exactly like an
    // unpacked file. MAME ZIPs bypass this and are streamed intact. ROMs only, never BIOS.
    private IActionResult StreamExtractedRom(string path)
    {
        byte[]? rom;
        try
        {
            rom = GamesService.ExtractRomFromArchive(path);
        }
        catch (RomTooLargeException ex)
        {
            // Reachable by any authenticated non-admin user (unlike the admin-only DAT upload
            // path), so this is a hard, documented ceiling rather than an unbounded allocation --
            // see GamesService.MaxExtractedRomBytes. Report it distinctly from the generic
            // extraction-failure 404 below so a legitimately oversized/corrupt archive is
            // diagnosable instead of looking like a missing file.
            return StatusCode(
                StatusCodes.Status413PayloadTooLarge,
                new { error = $"The archive entry exceeds the {ex.MaxBytes / (1024 * 1024)} MB limit for extracted ROMs." });
        }
        catch
        {
            return NotFound();
        }

        if (rom == null || rom.Length == 0)
        {
            return NotFound();
        }

        return File(rom, "application/octet-stream", enableRangeProcessing: true);
    }

    // Answers a HEAD from the archive index, so it never unpacks anything. The length has to
    // match what StreamExtractedRom sends, since that is what the client compares against.
    private IActionResult ExtractedRomSize(string path)
    {
        var length = GamesService.GetExtractedRomLength(path);
        if (length == null)
        {
            return NotFound();
        }

        Response.ContentType = "application/octet-stream";
        Response.ContentLength = length.Value;
        Response.Headers.AcceptRanges = "bytes";
        return new EmptyResult();
    }

    private static bool GamesEnabled()
    {
        return MoonfinPlugin.Instance?.Configuration?.GamesEnabled == true;
    }
}

/// <summary>Request body for a per-user arcade core override.</summary>
public sealed class GameCoreOverrideRequest
{
    public string? Core { get; set; }
}

/// <summary>Request body for a per-user game player backend override.</summary>
public sealed class GameBackendOverrideRequest
{
    public string? Backend { get; set; }
}
