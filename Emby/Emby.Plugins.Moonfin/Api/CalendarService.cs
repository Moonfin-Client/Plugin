using System;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.Calendar;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// The calendar's Remind me button. A reminder fires once, when its episode or movie lands in
    /// the library, through the same notifications as Seerr.
    /// </summary>
    public class CalendarService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public CalendarService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetCalendarRemindersRequest request)
        {
            var store = Plugin.Instance?.CalendarReminders;
            if (store == null) return Json(503, new { error = "The plugin isn't ready" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            return Json(new { reminders = store.List(userId.Value) });
        }

        public async Task<object> Post(SaveCalendarReminderRequest request)
        {
            var store = Plugin.Instance?.CalendarReminders;
            if (store == null) return Json(503, new { error = "The plugin isn't ready" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            var body = await MoonfinJson.ReadBodyAsync<CalendarReminder>(request.RequestStream).ConfigureAwait(false);
            var reminder = CalendarReminders.Normalize(body, out var error);
            if (reminder == null) return Json(400, new { error });

            var stored = store.Upsert(userId.Value, reminder);
            return stored == null ? Json(400, new { error = CalendarReminders.TooManyError }) : Json(stored);
        }

        public object? Delete(DeleteCalendarReminderRequest request)
        {
            var store = Plugin.Instance?.CalendarReminders;
            if (store == null) return Json(503, new { error = "The plugin isn't ready" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "User not authenticated" });

            if (!string.IsNullOrEmpty(request.Key)) store.Remove(userId.Value, request.Key!);
            Request.Response.StatusCode = 204;
            return null;
        }
    }
}
