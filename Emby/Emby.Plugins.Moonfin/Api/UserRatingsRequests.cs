using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/UserRatings/Mine", "GET")]
    [Authenticated]
    public class GetMyUserRatingsRequest : IReturn<object>
    {
    }
}
