using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Library items and Seerr titles that match a plain sentence. Limit is the most results per
    /// list, 20 by default. Debug adds each result's score and the parsed query, for tuning.
    /// </summary>
    [Route("/Moonfin/SmartSearch", "GET")]
    [Authenticated]
    public class GetSmartSearchRequest : IReturn<object>
    {
        public string? Query { get; set; }

        public int? Limit { get; set; }

        public bool Debug { get; set; }
    }
}
