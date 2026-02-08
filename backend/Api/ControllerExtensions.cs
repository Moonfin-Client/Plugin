using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Moonfin.Server.Api;

public static class ControllerExtensions
{
    public static Guid? GetUserIdFromClaims(this ControllerBase controller)
    {
        var userIdClaim = controller.User.FindFirst("Jellyfin-UserId")?.Value
            ?? controller.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
