using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// The seasonal Home row for the calling user. Country is the viewer's ISO alpha-2 code, ZZ
    /// for someone who picked "Other", or empty to fall back to the server's.
    /// </summary>
    [Route("/Moonfin/Seasonal/Row", "GET")]
    [Authenticated]
    public class GetSeasonalRowRequest : IReturn<object>
    {
        public string? Country { get; set; }
    }
}
