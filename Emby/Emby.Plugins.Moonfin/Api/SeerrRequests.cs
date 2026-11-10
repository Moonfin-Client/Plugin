using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Seerr/Login", "POST")]
    [Authenticated]
    public class SeerrLoginRequest : IReturn<object>
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? AuthType { get; set; }
    }

    [Route("/Moonfin/Seerr/Status", "GET")]
    [Authenticated]
    public class GetSeerrStatusRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Validate", "GET")]
    [Authenticated]
    public class ValidateSeerrSessionRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Logout", "DELETE")]
    [Authenticated]
    public class SeerrLogoutRequest : IReturn<object> { }

    [Route("/Moonfin/Seerr/Radarr/Calendar", "GET")]
    [Authenticated]
    public class GetRadarrCalendarRequest : IReturn<object>
    {
        public string? Start { get; set; }
        public string? End { get; set; }
        public string? Unmonitored { get; set; }
    }

    [Route("/Moonfin/Seerr/Sonarr/Calendar", "GET")]
    [Authenticated]
    public class GetSonarrCalendarRequest : IReturn<object>
    {
        public string? Start { get; set; }
        public string? End { get; set; }
        public string? Unmonitored { get; set; }
    }

    [Route("/Moonfin/Seerr/Radarr/Queue", "GET")]
    [Authenticated]
    public class GetRadarrQueueRequest : IReturn<object>
    {
        public string? TmdbId { get; set; }
    }

    [Route("/Moonfin/Seerr/Sonarr/Queue", "GET")]
    [Authenticated]
    public class GetSonarrQueueRequest : IReturn<object>
    {
        public string? TvdbId { get; set; }
    }

    [Route("/Moonfin/Seerr/Radarr/Queue/{Id}", "DELETE")]
    [Authenticated(Roles = "Admin")]
    public class RemoveRadarrQueueItemRequest : IReturn<object>
    {
        public int Id { get; set; }
    }

    [Route("/Moonfin/Seerr/Sonarr/Queue/{Id}", "DELETE")]
    [Authenticated(Roles = "Admin")]
    public class RemoveSonarrQueueItemRequest : IReturn<object>
    {
        public int Id { get; set; }
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "GET")]
    [Authenticated]
    public class SeerrProxyGetRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "POST")]
    [Authenticated]
    public class SeerrProxyPostRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "PUT")]
    [Authenticated]
    public class SeerrProxyPutRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Seerr/Api/{Path*}", "DELETE")]
    [Authenticated]
    public class SeerrProxyDeleteRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }
}
