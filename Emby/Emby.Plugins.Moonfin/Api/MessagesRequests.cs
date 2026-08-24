using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Messages", "GET")]
    [Authenticated]
    public class GetMessagesRequest : IReturn<object> { }

    [Route("/Moonfin/Admin/Messages", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAdminMessagesRequest : IReturn<object> { }

    [Route("/Moonfin/Admin/Messages", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SaveMessageRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Admin/Messages/{MessageId}", "DELETE")]
    [Authenticated(Roles = "Admin")]
    public class DeleteMessageRequest : IReturn<object>
    {
        public string? MessageId { get; set; }
    }
}
