using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/CustomRows/Items", "GET")]
    [Authenticated]
    public class GetCustomRowItemsRequest : IReturn<object>
    {
        public string? Source { get; set; }
        public string? Type { get; set; }

        // "params" is a C# keyword-adjacent name but binds fine on Emby (case-insensitive).
        public string? Params { get; set; }

        /// <summary>Bypass the server-side cache and refetch from the upstream source.</summary>
        public bool Refresh { get; set; }
    }
}
