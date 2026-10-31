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
    }
}
