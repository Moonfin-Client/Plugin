using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    public class MessagesService : IService, IRequiresRequest, IHasResultFactory
    {
        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public MessagesService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        public object Get(GetMessagesRequest request)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableSettingsSync != true)
                return Json(503, new { error = "Settings sync is disabled" });

            var userId = AuthHelpers.GetCurrentUserId(Request, _authContext);
            if (userId == null) return Json(401, new { error = "A valid user token is required" });

            // Filtering happens here, not on the client. Sending everything and hiding it in
            // the app would let any user read messages meant for someone else.
            var isAdmin = AuthHelpers.IsCurrentUserAdmin(Request, _authContext);
            var now = DateTime.UtcNow;
            var items = config.Messages
                .Where(m => m.IsVisibleTo(userId.Value, isAdmin, now))
                .OrderByDescending(m => m.Pinned)
                .ThenByDescending(m => m.CreatedUtc)
                .ToList();

            return Json(new { items });
        }

        public object Get(GetAdminMessagesRequest request)
        {
            var messages = Plugin.Instance?.Configuration?.Messages ?? new List<ServerMessage>();

            return Json(new
            {
                items = messages
                    .OrderByDescending(m => m.Pinned)
                    .ThenByDescending(m => m.CreatedUtc)
                    .ToList()
            });
        }

        public async Task<object> Post(SaveMessageRequest request)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null) return Json(503, new { error = "Plugin is not ready" });

            var message = await MoonfinJson.ReadBodyAsync<ServerMessage>(request.RequestStream).ConfigureAwait(false);
            if (message == null) return Json(400, new { error = "A message body is required" });

            message.Sanitize();

            if (message.Title.Length == 0 && message.Body.Length == 0)
                return Json(400, new { error = "A title or a body is required" });

            var messages = config.Messages;
            var existing = messages.FirstOrDefault(m =>
                string.Equals(m.Id, message.Id, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                message.CreatedUtc = existing.CreatedUtc;
                message.CreatedByUserId = existing.CreatedByUserId;
                messages.Remove(existing);
            }
            else
            {
                message.Id = Guid.NewGuid().ToString("N");
                message.CreatedUtc = DateTime.UtcNow;
                message.CreatedByUserId = AuthHelpers.GetCurrentUserId(Request, _authContext)?.ToString("N");
            }

            messages.Add(message);
            ServerMessage.Prune(messages);
            plugin.UpdateConfiguration(config);

            Plugin.Instance?.SettingsService?.BroadcastSystemEvent("messagesChanged");

            // Only push for messages meant to interrupt. An inbox message can wait until the
            // user opens the app.
            if (existing == null && message.Delivery != ServerMessage.DeliveryInbox)
                SendPush(message);

            return Json(new { success = true, item = message });
        }

        public object Delete(DeleteMessageRequest request)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;
            if (plugin == null || config == null) return Json(503, new { error = "Plugin is not ready" });

            var messageId = request.MessageId ?? string.Empty;
            var removed = config.Messages
                .RemoveAll(m => string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));

            if (removed == 0) return Json(404, new { error = "Message not found" });

            plugin.UpdateConfiguration(config);
            Plugin.Instance?.SettingsService?.BroadcastSystemEvent("messagesChanged");

            return Json(new { success = true });
        }

        /// <summary>
        /// Queues a push so the message also reaches users who do not have the app open.
        /// The route tells the app to open the messages window on tap.
        /// </summary>
        private static void SendPush(ServerMessage message)
        {
            // Admin-only messages get no push, since the notification store does not know who
            // is an admin. Admins still see them next time they open the app.
            if (message.Audience == ServerMessage.AudienceAdmins) return;

            var store = Plugin.Instance?.NotificationStore;
            var pushDelivery = Plugin.Instance?.PushDelivery;
            if (store == null || pushDelivery == null) return;

            var title = message.Title.Length > 0 ? message.Title : "Message from your server";

            foreach (var userId in store.GetUsersWithDevices())
            {
                if (message.Audience == ServerMessage.AudienceUsers &&
                    !message.TargetUserIds.Any(id =>
                        Guid.TryParse(id, out var target) && target == userId))
                {
                    continue;
                }

                pushDelivery.QueueToUser(userId, title, message.Body, route: "messages");
            }
        }
    }
}
