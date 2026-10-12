using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Authorize]
[Route("Moonfin/Cinema")]
public sealed class CinemaController(CinemaMediaResolver resolver, ILogger<CinemaController> logger) : ControllerBase
{
    [HttpGet("ResolveMedia")]
    public async Task<ActionResult<CinemaMediaResolver.Resolution>> ResolveMedia(
        [FromQuery] Guid itemId, [FromQuery] string? expectedMediaType, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (!userId.HasValue || userId == Guid.Empty) return Unauthorized();
        if (itemId == Guid.Empty || expectedMediaType is not (null or "movie" or "tv")) return BadRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var allowFilenameSearch = MoonfinPlugin.Instance?.Configuration.CinemaFilenameSearchEnabled == true;
            var resolution = await resolver.ResolveMediaAsync(itemId, expectedMediaType, timeout.Token,
                allowFilenameSearch).ConfigureAwait(false);
            return Ok(resolution);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Ok(new CinemaMediaResolver.Resolution(null, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Cinema media resolution failed for {ItemId}", itemId);
            return Ok(new CinemaMediaResolver.Resolution(null, null));
        }
    }
}
