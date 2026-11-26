using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Authorize(Policy = Policies.LiveTvAccess)]
[Route("Moonfin/LiveTv/ChannelGroups")]
public sealed class LiveTvChannelGroupsController(LiveTvChannelGroupsService groups) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (!userId.HasValue || userId == Guid.Empty) return Unauthorized();
        return Ok(await groups.GetAsync(userId.Value, cancellationToken));
    }
}
