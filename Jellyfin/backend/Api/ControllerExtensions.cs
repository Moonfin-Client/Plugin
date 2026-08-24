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

    /// <summary>
    /// True when the caller is a server admin. This is the same role the "RequiresElevation"
    /// policy checks, so it matches what Jellyfin itself allows.
    /// </summary>
    public static bool IsAdminFromClaims(this ControllerBase controller) =>
        controller.User.IsInRole("Administrator");

    public static string? GetDeviceIdFromClaims(this ControllerBase controller)
    {
        var deviceId = controller.User.FindFirst("Jellyfin-DeviceId")?.Value;
        return string.IsNullOrEmpty(deviceId) ? null : deviceId;
    }
}
