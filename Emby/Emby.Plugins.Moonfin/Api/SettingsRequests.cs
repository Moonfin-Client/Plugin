using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Ping", "GET")]
    [Authenticated]
    public class GetPingRequest : IReturn<object>
    {
        /// <summary>A device profile name, so defaultSettings carries that device's defaults over Global's.</summary>
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Settings/Stream", "GET")]
    [Authenticated]
    public class StreamSettingsRequest : IReturn<object> { }

    [Route("/Moonfin/Settings", "GET")]
    [Authenticated]
    public class GetMySettingsRequest : IReturn<object> { }

    [Route("/Moonfin/Settings", "HEAD")]
    [Authenticated]
    public class CheckMySettingsRequest : IReturn<object> { }

    [Route("/Moonfin/Settings", "POST")]
    [Authenticated]
    public class SaveMySettingsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Settings", "DELETE")]
    [Authenticated]
    public class DeleteMySettingsRequest : IReturn<object> { }

    [Route("/Moonfin/Settings/{UserId}", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetUserSettingsRequest : IReturn<object>
    {
        public string? UserId { get; set; }
    }

    [Route("/Moonfin/Settings/{UserId}", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SaveUserSettingsRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? UserId { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Settings/Resolved/{Profile}", "GET")]
    [Authenticated]
    public class GetResolvedProfileRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Settings/Profile/{Profile}", "POST")]
    [Authenticated]
    public class SaveMyProfileRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Profile { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Settings/Profile/{Profile}", "DELETE")]
    [Authenticated]
    public class DeleteMyProfileRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Settings/detailsScreenBlur", "GET")]
    [Route("/Moonfin/Settings/detailsScreenBlur/{Profile}", "GET")]
    [Authenticated]
    public class GetDetailsBlurRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Settings/detailsScreenBlur", "POST")]
    [Route("/Moonfin/Settings/detailsScreenBlur/{Profile}", "POST")]
    [Authenticated]
    public class SaveDetailsBlurRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Profile { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Settings/detailsScreenOpacity", "GET")]
    [Route("/Moonfin/Settings/detailsScreenOpacity/{Profile}", "GET")]
    [Authenticated]
    public class GetDetailsOpacityRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Settings/detailsScreenOpacity", "POST")]
    [Route("/Moonfin/Settings/detailsScreenOpacity/{Profile}", "POST")]
    [Authenticated]
    public class SaveDetailsOpacityRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Profile { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Defaults", "GET")]
    [Authenticated]
    public class GetDefaultsRequest : IReturn<object> { }

    [Route("/Moonfin/Admin/PushDefaults", "POST")]
    [Authenticated(Roles = "Admin")]
    public class PushDefaultsRequest : IReturn<object>
    {
        public bool Overwrite { get; set; }
    }

    [Route("/Moonfin/Admin/PushDefaults/{UserId}", "POST")]
    [Authenticated(Roles = "Admin")]
    public class PushDefaultsToUserRequest : IReturn<object>
    {
        public string? UserId { get; set; }
        public bool Overwrite { get; set; }
    }

    [Route("/Moonfin/Admin/ApplyDefaults", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ApplyDefaultsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Admin/CopySettings", "POST")]
    [Authenticated(Roles = "Admin")]
    public class CopySettingsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Broadcast", "POST")]
    [Authenticated(Roles = "Admin")]
    public class BroadcastRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Libraries/CheckWriteAccess", "GET")]
    [Authenticated(Roles = "Admin")]
    public class CheckLibrariesWriteAccessRequest : IReturn<object> { }

    [Route("/Moonfin/Genres", "GET")]
    [Authenticated]
    public class GetGenresRequest : IReturn<object> { }

    [Route("/Moonfin/MediaBar", "GET")]
    [Authenticated]
    public class GetMediaBarRequest : IReturn<object>
    {
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/Seerr/Config", "GET")]
    [Authenticated]
    public class GetSeerrConfigRequest : IReturn<object> { }

    // The web frontend calls this while it is still looking for a server to sign in to, so it has
    // no token yet.
    [Route("/Moonfin/Discovery", "GET")]
    [Route("/Moonfin/Discovery/discover", "GET")]
    [Unauthenticated]
    public class DiscoveryRequest : IReturn<object> { }

    // PascalCase property names (no JsonPropertyName) so MoonfinJson emits LibraryId/LibraryName/
    // FailedPaths verbatim for this report.
    public class LibraryWriteAccessReport
    {
        public string LibraryId { get; set; } = string.Empty;
        public string LibraryName { get; set; } = string.Empty;
        public System.Collections.Generic.List<string> FailedPaths { get; set; } = new System.Collections.Generic.List<string>();
    }
}
