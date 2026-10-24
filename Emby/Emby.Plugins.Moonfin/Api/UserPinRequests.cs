using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Pin", "GET")]
    [Authenticated]
    public class GetPinRequest : IReturn<object> { }

    [Route("/Moonfin/Pin", "PUT")]
    [Authenticated]
    public class SetPinRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Pin/Verify", "POST")]
    [Authenticated]
    public class VerifyPinRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Pin", "DELETE")]
    [Authenticated]
    public class RemovePinRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Admin/Pins", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAdminPinsRequest : IReturn<object> { }

    [Route("/Moonfin/Users/{UserId}/Pin", "PUT")]
    [Authenticated(Roles = "Admin")]
    public class SetPinForRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? UserId { get; set; }

        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Users/{UserId}/Pin", "DELETE")]
    [Authenticated(Roles = "Admin")]
    public class RemovePinForRequest : IReturn<object>
    {
        public string? UserId { get; set; }
    }

    /// <summary>Body for the PIN endpoints. CurrentPin is the one being replaced or removed.</summary>
    public class PinBody
    {
        public string? Pin { get; set; }

        public string? CurrentPin { get; set; }
    }
}
