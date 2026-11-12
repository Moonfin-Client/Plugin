using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/LiveTv/ChannelGroups", "GET")]
    [Authenticated]
    public class GetLiveTvChannelGroups : IReturn<object> { }
}
