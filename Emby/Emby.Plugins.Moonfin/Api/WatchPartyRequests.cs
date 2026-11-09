using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    // The routes themselves live in the shared engine, so each method maps one catch-all onto it.

    [Route("/Moonfin/WatchParty/{Path*}", "GET")]
    [Authenticated]
    public class WatchPartyGetRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }

    [Route("/Moonfin/WatchParty/{Path*}", "POST")]
    [Authenticated]
    public class WatchPartyPostRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/WatchParty/{Path*}", "DELETE")]
    [Authenticated]
    public class WatchPartyDeleteRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }

    [Route("/Moonfin/Admin/WatchParty/{Path*}", "GET")]
    [Authenticated(Roles = "Admin")]
    public class WatchPartyAdminGetRequest : IReturn<object>
    {
        public string? Path { get; set; }
    }

    [Route("/Moonfin/Admin/WatchParty/{Path*}", "POST")]
    [Authenticated(Roles = "Admin")]
    public class WatchPartyAdminPostRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Path { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    /// <summary>The invite page and preview.png, which link previews and new users load signed out.</summary>
    [Route("/Moonfin/Party/{Code}", "GET")]
    [Unauthenticated]
    public class WatchPartyInviteRequest : IReturn<object>
    {
        public string? Code { get; set; }
    }
}
