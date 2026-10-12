using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Cinema/ResolveMedia", "GET")]
    [Authenticated]
    public class ResolveCinemaMediaRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;
        public string? ExpectedMediaType { get; set; }
    }
}
