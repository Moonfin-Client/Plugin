using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Notifications/Prefs", "GET")]
    [Authenticated]
    public class GetNotificationPrefsRequest : IReturn<object> { }

    [Route("/Moonfin/Notifications/Prefs", "POST")]
    [Authenticated]
    public class SaveNotificationPrefsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Notifications/Register", "POST")]
    [Authenticated]
    public class RegisterDeviceRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Notifications/Register", "DELETE")]
    [Authenticated]
    public class UnregisterDeviceRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Notifications/WebhookInfo", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetWebhookInfoRequest : IReturn<object> { }

    [Route("/Moonfin/Notifications/Reprovision", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ReprovisionWebhookRequest : IReturn<object> { }

    // Inbound Seerr webhook. Seerr posts this with no Emby token, so the route has to answer
    // without one and the shared secret below is what vouches for the caller.
    [Route("/Moonfin/Seerr/Webhook", "POST")]
    [Route("/Moonfin/Jellyseerr/Webhook", "POST")]
    [Unauthenticated]
    public class SeerrWebhookRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? Secret { get; set; }
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    /// <summary>Body for saving notification preferences.</summary>
    public class NotificationPrefsBody
    {
        public bool NotifyOnNewRequests { get; set; }
        public bool NotifyOnLibraryAdded { get; set; }

        // Nullable so payloads from clients that predate the field keep the stored value.
        public bool? NotifyOnIssues { get; set; }
        public bool? NotifyOnNewMedia { get; set; }
    }

    /// <summary>Body for registering or removing a push device.</summary>
    public class DeviceRegistrationBody
    {
        public string? Token { get; set; }
        public string? Platform { get; set; }
        public string? DeviceId { get; set; }
    }
}
