using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Route("Moonfin/Seasonal")]
[Produces(MediaTypeNames.Application.Json)]
[Authorize]
public class SeasonalController : ControllerBase
{
    private readonly SeasonalRowService _rows;
    private readonly ILogger<SeasonalController> _logger;

    public SeasonalController(SeasonalRowService rows, ILogger<SeasonalController> logger)
    {
        _rows = rows;
        _logger = logger;
    }

    /// <summary>
    /// The seasonal Home row for the calling user. <paramref name="country"/> is the viewer's
    /// ISO alpha-2 code, ZZ for someone who picked "Other", or empty to fall back to the server's.
    /// </summary>
    [HttpGet("Row")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeasonalRowResponse>> GetRow([FromQuery] string? country, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { Error = "User not authenticated" });
        }

        try
        {
            return Ok(await _rows.BuildAsync(userId.Value, country, DateTime.Now, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build the seasonal row for user {UserId}", userId);
            return Ok(new SeasonalRowResponse());
        }
    }
}
