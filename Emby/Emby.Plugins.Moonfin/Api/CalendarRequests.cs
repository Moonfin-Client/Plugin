using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Calendar/Reminders", "GET")]
    [Authenticated]
    public class GetCalendarRemindersRequest : IReturn<object> { }

    [Route("/Moonfin/Calendar/Reminders", "POST")]
    [Authenticated]
    public class SaveCalendarReminderRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Calendar/Reminders/{Key}", "DELETE")]
    [Authenticated]
    public class DeleteCalendarReminderRequest : IReturn<object>
    {
        public string? Key { get; set; }
    }
}
