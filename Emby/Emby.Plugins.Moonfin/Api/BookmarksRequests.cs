using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Bookmarks", "GET")]
    [Authenticated]
    public class GetAllBookmarksRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Bookmarks/{ItemId}", "GET")]
    [Authenticated]
    public class GetItemBookmarksRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;
    }

    [Route("/Moonfin/Bookmarks/{ItemId}", "POST")]
    [Authenticated]
    public class SaveItemBookmarksRequest : IReturn<object>, IRequiresRequestStream
    {
        public string ItemId { get; set; } = string.Empty;

        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Bookmarks/{ItemId}/Notes", "POST")]
    [Authenticated]
    public class SaveItemNotesRequest : IReturn<object>, IRequiresRequestStream
    {
        public string ItemId { get; set; } = string.Empty;

        public System.IO.Stream RequestStream { get; set; } = null!;
    }
}
