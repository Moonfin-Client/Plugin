using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/PosterBadges/Items", "GET")]
    [Authenticated]
    public class GetPosterBadgesRequest : IReturn<object>
    {
        /// <summary>Comma-separated item ids.</summary>
        public string? Ids { get; set; }

        /// <summary>The client's device: tv, desktop or mobile.</summary>
        public string? Profile { get; set; }
    }

    [Route("/Moonfin/PosterBadges/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class TestPosterBadgesRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/PosterBadges/Presets", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetPosterBadgePresetsRequest : IReturn<object>
    {
    }
}
