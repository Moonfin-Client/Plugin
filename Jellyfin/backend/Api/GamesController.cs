using System.Net;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Games;
using Moonfin.Romm;
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
    private readonly GamesService _gamesService;
    private readonly CoresService _coresService;
    private readonly GameArtworkReconciliationService _artwork;
    private readonly GameArtworkDeliveryLimiter _artworkDeliveryLimiter;
    private readonly ArcadeCompatibilityService _arcadeCompatibility;
    private readonly RommSource _romm;

    public GamesController(
        GamesService gamesService,
        CoresService coresService,
        GameArtworkReconciliationService artwork,
        GameArtworkDeliveryLimiter artworkDeliveryLimiter,
        ArcadeCompatibilityService arcadeCompatibility,
        RommSource romm)
    {
        _gamesService = gamesService;
        _coresService = coresService;
        _artwork = artwork;
        _artworkDeliveryLimiter = artworkDeliveryLimiter;
        _arcadeCompatibility = arcadeCompatibility;
        _romm = romm;
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

        var systems = (await _artwork.GetSystemsAsync(libraryId, this.GetUserIdFromClaims(), cancellationToken).ConfigureAwait(false)).ToList();

        // One catalog pass for every system on the page. Reading them one at a time re-walked the
        // whole catalog per system, so a library's browse request cost systems x entries.
        var artworkBySystem = await _artwork
            .GetSystemArtworkBatchAsync(libraryId, systems.Select(system => system.Id), cancellationToken)
            .ConfigureAwait(false);
        GameArtworkApi.AttachPreviews(libraryId, systems, artworkBySystem);

        return Ok(systems);
    }

    /// <summary>Advertises the additive, manifest-driven artwork protocol for newer clients.</summary>
    [HttpGet("ArtworkCapabilities")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<GameArtworkCapabilities> GetArtworkCapabilities() => Ok(GameArtworkApi.Capabilities());

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
        var currentGeneration = GameArtworkApi.Generation(artwork.System.Generation);
        Response.Headers["ETag"] = "\"" + currentGeneration + "\"";
        if (string.Equals(generation, currentGeneration, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(GameArtworkApi.Manifest(libraryId, artwork));
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

        if (request.Items.Count > GameArtworkApi.MaxArtworkPriorityItems)
        {
            return BadRequest(new { error = $"At most {GameArtworkApi.MaxArtworkPriorityItems} artwork priority items may be submitted at once." });
        }

        var system = await _artwork.GetSystemArtworkAsync(libraryId, request.SystemId, cancellationToken).ConfigureAwait(false);
        if (system == null)
        {
            return NotFound();
        }

        if (!string.Equals(request.KnownGeneration, GameArtworkApi.Generation(system.System.Generation), StringComparison.Ordinal))
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
            if (item.Roles.Any(role => !GameArtworkApi.IsRole(role) || !seenRoles.Add(role)))
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

        if (!GameArtworkApi.IsRole(role))
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

        var role = GameArtworkApi.NormalizeLegacyRole(type);
        if (type != null && !GameArtworkApi.IsRole(type))
        {
            return BadRequest(new { error = "Artwork type must be boxart, snap, or title." });
        }

        return await _artwork.RequestRefreshAsync(libraryId, gameId, role, cancellationToken).ConfigureAwait(false)
            ? Accepted()
            : NotFound();
    }

    /// <summary>
    /// Administrator-only library-wide repair, run in the background: checks the library's game
    /// metadata for updates, then reopens artwork that never found an original.
    /// </summary>
    [HttpPost("{libraryId}/Artwork/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult RefreshLibraryArtwork([FromRoute] string libraryId, [FromQuery] string? system)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        return _artwork.StartLibraryArtworkRefresh(libraryId, system) switch
        {
            LibraryArtworkRefreshStatus.NotFound => NotFound(),
            LibraryArtworkRefreshStatus.BadSystem => BadRequest(new { error = "Unknown system." }),
            LibraryArtworkRefreshStatus.Conflict => Conflict(new { error = "An artwork refresh for this library is already running." }),
            _ => Accepted(),
        };
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

        return Ok(_gamesService.GetGames(libraryId, system, this.GetUserIdFromClaims()));
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
    public async Task<IActionResult> GetRom(
        [FromRoute] string libraryId,
        [FromRoute] string token,
        CancellationToken cancellationToken,
        [FromRoute] string? fileName = null)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        if (_romm.Owns(libraryId))
        {
            return await ServeRommRomAsync(token, cancellationToken).ConfigureAwait(false);
        }

        // The optional filename gives filename-sensitive emulators (MAME, and the PlayStation core
        // for a .pbp) the original file name in the URL. It's deliberately never used for path
        // resolution or authorization.
        _ = fileName;
        var path = _gamesService.ResolveFilePath(libraryId, token, allowBios: false);
        if (!string.IsNullOrEmpty(path) && GameSystemCoreResolver.IsCueSheet(path))
        {
            var discSet = DiscSet.ForCueSheet(path);
            return discSet == null ? NotFound() : await StreamDiscSetAsync(discSet).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(path) && GamesService.IsArchive(path))
        {
            var core = _gamesService.SystemCoreFor(libraryId, path) ?? string.Empty;
            if (GameSystemCoreResolver.ShouldPreserveArchiveForCore(core, path))
            {
                return StreamFile(path);
            }

            if (GameSystemCoreResolver.ShouldRepackArchiveForCore(core, path))
            {
                var discSet = DiscSet.ForSevenZip(path);
                return discSet == null ? NotFound() : await StreamDiscSetAsync(discSet).ConfigureAwait(false);
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

        var role = GameArtworkApi.NormalizeLegacyRole(type);
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
            GameArtworkApi.WantsFullResolution(full));
        return asset == null
            ? ArtworkNotFound(confirmedMissing: false)
            : await ServeArtworkAsync(asset, versionedUrl: false, cancellationToken).ConfigureAwait(false);
    }

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
        Response.Headers["Cache-Control"] = GameArtworkApi.CacheControl(versionedUrl, asset.IsThumbnail);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(asset.Path, asset.ContentType);
    }

    private IActionResult RetryableArtworkResponse(int statusCode, TimeSpan retryAfter)
    {
        Response.Headers["Retry-After"] = GameArtworkApi.RetryAfterSeconds(retryAfter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["Cache-Control"] = "no-store";
        return StatusCode(statusCode);
    }

    private IActionResult ArtworkNotFound(bool confirmedMissing)
    {
        Response.Headers["Cache-Control"] = confirmedMissing ? "private,max-age=300" : "no-store";
        return NotFound();
    }

    /// <summary>Streams a BIOS file required by a system's core, HEAD included for the same
    /// cache check EmulatorJS runs on ROMs.</summary>
    [HttpGet("{libraryId}/Bios/{token}")]
    [HttpHead("{libraryId}/Bios/{token}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBios(
        [FromRoute] string libraryId,
        [FromRoute] string token,
        CancellationToken cancellationToken)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        if (_romm.Owns(libraryId))
        {
            var romm = _romm.ForUser(this.GetUserIdFromClaims());
            var firmware = romm.FindFirmware(token);
            var client = romm.CreateClient();
            return firmware == null || client == null
                ? NotFound()
                : await ProxyRommAsync((method, range) => client.SendFirmwareAsync(method, firmware, range, cancellationToken), cancellationToken).ConfigureAwait(false);
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

    // Sends a PlayStation disc set as an uncompressed zip written as it goes out, so there's no
    // size limit and every track arrives.
    private async Task<IActionResult> StreamDiscSetAsync(DiscSet discSet)
    {
        Response.ContentType = "application/zip";
        Response.ContentLength = discSet.Length;
        if (HttpMethods.IsHead(Request.Method))
        {
            return new EmptyResult();
        }

        // Kestrel refuses synchronous writes unless they're allowed, and the zip is written that way.
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl != null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        try
        {
            await Task.Run(() => discSet.WriteTo(Response.Body), HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception) when (!Response.HasStarted)
        {
            Response.ContentType = null;
            Response.ContentLength = null;
            return NotFound();
        }
        catch (Exception) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away partway through.
        }

        return new EmptyResult();
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
        var length = GamesService.GetExtractedRomInfo(path)?.Length;
        if (length == null)
        {
            return NotFound();
        }

        Response.ContentType = "application/octet-stream";
        Response.ContentLength = length.Value;
        Response.Headers.AcceptRanges = "bytes";
        return new EmptyResult();
    }

    /// <summary>Checks the address and token in the admin page form, so they can be tried before saving.</summary>
    [HttpPost("Romm/Test")]
    [Authorize(Policy = "RequiresElevation")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RommTestResult>> TestRomm(
        [FromBody] RommTestRequest? request,
        CancellationToken cancellationToken)
    {
        var baseUrl = RommClient.NormalizeBaseUrl(request?.Url);
        var token = RommClient.NormalizeToken(request?.Token);
        if (baseUrl == null)
        {
            return Ok(new RommTestResult { Error = "Enter the RomM address, like http://192.168.1.20:8080." });
        }

        if (token == null)
        {
            return Ok(new RommTestResult { Error = "Enter a RomM client token." });
        }

        try
        {
            var info = await _romm.ClientFor(baseUrl, token).CheckAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new RommTestResult
            {
                Ok = true,
                Version = info.Version,
                PlatformCount = info.PlatformCount,
                SupportedPlatformCount = info.SupportedPlatformCount,
                SupportedRomCount = info.SupportedRomCount,
            });
        }
        catch (Exception ex) when (ex is JsonException or RommRequestException { StatusCode: StatusCodes.Status404NotFound })
        {
            return Ok(new RommTestResult { Error = "That address answered, but not like a RomM server." });
        }
        catch (RommRequestException ex)
        {
            return Ok(new RommTestResult { Error = ex.Message + "." });
        }
        catch (HttpRequestException)
        {
            return Ok(new RommTestResult { Error = "Couldn't reach RomM at that address." });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Ok(new RommTestResult { Error = "RomM didn't answer in time." });
        }
    }

    /// <summary>The signed-in user's link to their own RomM account. A pending link is checked with RomM on each call.</summary>
    [HttpGet("Romm/Account")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RommAccountStatus>> GetRommAccount(CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        return _romm.Accounts == null || !GamesEnabled()
            ? Ok(new RommAccountStatus())
            : Ok(await _romm.Accounts.StatusAsync(userId.Value, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Starts linking the signed-in user's RomM account, which they approve in RomM.</summary>
    [HttpPost("Romm/Account")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RommAccountStatus>> LinkRommAccount(CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        return _romm.Accounts == null || !GamesEnabled()
            ? Ok(new RommAccountStatus())
            : Ok(await _romm.Accounts.StartLinkAsync(userId.Value, cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("Romm/Account")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<RommAccountStatus> UnlinkRommAccount()
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized();
        }

        return Ok(_romm.Accounts?.Unlink(userId.Value) ?? new RommAccountStatus());
    }

    private async Task<IActionResult> ServeRommRomAsync(string gameId, CancellationToken cancellationToken)
    {
        var romm = _romm.ForUser(this.GetUserIdFromClaims());
        var game = romm.FindGame(gameId);
        var client = romm.CreateClient();
        if (game == null || client == null)
        {
            return NotFound();
        }

        if (!game.UnpackArchive && !game.RepackArchive)
        {
            return await ProxyRommAsync((method, range) => client.SendRomAsync(method, game, range, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        // A zipped ROM is unpacked here and a PlayStation .7z is repacked as a zip, the same way
        // a folder library does it. RomM listed the archive's contents, so a HEAD needs no download.
        if (HttpMethods.IsHead(Request.Method))
        {
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = game.ServedSizeBytes;
            Response.Headers.AcceptRanges = "bytes";
            return new EmptyResult();
        }

        var temp = Path.Combine(Path.GetTempPath(), $"moonfin-romm-{Guid.NewGuid():N}{Path.GetExtension(game.Rom.FileName)}");
        try
        {
            var status = await client.DownloadRomAsync(game, temp, GamesService.MaxExtractedRomBytes, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.RequestEntityTooLarge)
            {
                return StatusCode(
                    StatusCodes.Status413PayloadTooLarge,
                    new { error = $"The archive exceeds the {GamesService.MaxExtractedRomBytes / (1024 * 1024)} MB limit for extracted ROMs." });
            }

            if (status != HttpStatusCode.OK)
            {
                return RommFailure(status);
            }

            if (!game.RepackArchive)
            {
                return StreamExtractedRom(temp);
            }

            var discSet = DiscSet.ForSevenZip(temp);
            return discSet == null ? NotFound() : await StreamDiscSetAsync(discSet).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        finally
        {
            try
            {
                System.IO.File.Delete(temp);
            }
            catch (IOException)
            {
                // A temp file left behind is cleared with the rest of the temp folder.
            }
        }
    }

    /// <summary>
    /// Streams a RomM file to the app, passing a byte range through so a client can resume or
    /// probe the size the same way it does against a file on disk.
    /// </summary>
    private async Task<IActionResult> ProxyRommAsync(
        Func<HttpMethod, string?, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var head = HttpMethods.IsHead(Request.Method);
        HttpResponseMessage upstream;
        try
        {
            upstream = await send(head ? HttpMethod.Head : HttpMethod.Get, Request.Headers.Range.ToString()).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }

        using (upstream)
        {
            if (upstream.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            {
                return RommFailure(upstream.StatusCode);
            }

            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = upstream.Content.Headers.ContentLength;
            Response.Headers.AcceptRanges = "bytes";
            if (upstream.Content.Headers.ContentRange != null)
            {
                Response.Headers.ContentRange = upstream.Content.Headers.ContentRange.ToString();
            }

            if (!head)
            {
                await upstream.Content.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
            }

            return new EmptyResult();
        }
    }

    private IActionResult RommFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => NotFound(),
        HttpStatusCode.RequestedRangeNotSatisfiable => StatusCode(StatusCodes.Status416RangeNotSatisfiable),
        _ => StatusCode(StatusCodes.Status502BadGateway),
    };

    private static bool GamesEnabled()
    {
        return MoonfinPlugin.Instance?.Configuration?.GamesEnabled == true;
    }
}

public sealed class RommTestRequest
{
    public string? Url { get; set; }

    public string? Token { get; set; }
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
