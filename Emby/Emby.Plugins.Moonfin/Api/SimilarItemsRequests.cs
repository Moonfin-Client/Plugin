using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Items/{Id}/Similar", "GET")]
    [Authenticated]
    public class GetSimilarItemsRequest : IReturn<object>
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>Defaults to 20 and is capped at 200.</summary>
        public int? Limit { get; set; }

        /// <summary>Comma separated item ids to leave out.</summary>
        public string? ExcludeItemIds { get; set; }
    }
}
