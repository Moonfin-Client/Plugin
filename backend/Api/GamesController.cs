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
[ApiController]
[Route("Moonfin/Games")]
public class GamesController : ControllerBase
{
    private readonly GamesService _gamesService;
    private readonly CoresService _coresService;

    public GamesController(GamesService gamesService, CoresService coresService)
    {
        _gamesService = gamesService;
        _coresService = coresService;
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
    public ActionResult<IEnumerable<GameSystem>> GetSystems([FromRoute] string libraryId)
    {
        if (!GamesEnabled())
        {
            return Ok(Array.Empty<GameSystem>());
        }

        return Ok(_gamesService.GetSystems(libraryId));
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
    public ActionResult<GameDetail> GetGame(
        [FromRoute] string libraryId,
        [FromRoute] string gameId)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var game = _gamesService.GetGame(libraryId, gameId);
        return game == null ? NotFound() : Ok(game);
    }

    /// <summary>
    /// Streams a ROM file. EmulatorJS fetches this via XHR; clients append the Jellyfin
    /// access token as an <c>api_key</c> query parameter so the WebView request authenticates.
    /// </summary>
    [HttpGet("{libraryId}/Rom/{token}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetRom([FromRoute] string libraryId, [FromRoute] string token)
    {
        if (!GamesEnabled())
        {
            return NotFound();
        }

        var path = _gamesService.ResolveFilePath(libraryId, token, allowBios: false);
        return StreamFile(path);
    }

    /// <summary>Streams a BIOS file required by a system's core.</summary>
    [HttpGet("{libraryId}/Bios/{token}")]
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

    private static bool GamesEnabled()
    {
        return MoonfinPlugin.Instance?.Configuration?.GamesEnabled == true;
    }
}
