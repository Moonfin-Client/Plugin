using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Seerr/Login", "POST")]
[Route("/Moonfin/Jellyseerr/Login", "POST")]
    [Authenticated]
    public class SeerrLoginRequest : IReturn<object>
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? AuthType { get; set; }
    }

    [Route("/Moonfin/Seerr/Status", "GET")]
[Route("/Moonfin/Jellyseerr/Status", "GET")]
    [Authenticated]
    public class GetSeerrStatusRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Validate", "GET")]
[Route("/Moonfin/Jellyseerr/Validate", "GET")]
    [Authenticated]
    public class ValidateSeerrSessionRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Logout", "DELETE")]
[Route("/Moonfin/Jellyseerr/Logout", "DELETE")]
    [Authenticated]
    public class SeerrLogoutRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Radarr/Calendar", "GET")]
[Route("/Moonfin/Jellyseerr/Radarr/Calendar", "GET")]
    [Authenticated]
    public class GetRadarrCalendarRequest : IReturn<object>
    {
        public string? Start { get; set; }
        public string? End { get; set; }
    }

    [Route("/Moonfin/Seerr/Sonarr/Calendar", "GET")]
[Route("/Moonfin/Jellyseerr/Sonarr/Calendar", "GET")]
    [Authenticated]
    public class GetSonarrCalendarRequest : IReturn<object>
    {
        public string? Start { get; set; }
        public string? End { get; set; }
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "GET")]
[Route("/Moonfin/Jellyseerr/Api/{Path*}", "GET")]
    [Authenticated]
    public class SeerrProxyGetRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "POST")]
[Route("/Moonfin/Jellyseerr/Api/{Path*}", "POST")]
    [Authenticated]
    public class SeerrProxyPostRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "PUT")]
[Route("/Moonfin/Jellyseerr/Api/{Path*}", "PUT")]
    [Authenticated]
    public class SeerrProxyPutRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "DELETE")]
[Route("/Moonfin/Jellyseerr/Api/{Path*}", "DELETE")]
    [Authenticated]
    public class SeerrProxyDeleteRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }
}
