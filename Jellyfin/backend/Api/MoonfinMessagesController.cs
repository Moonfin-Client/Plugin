using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// API controller for admin messages shown to users in the Moonfin app.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class MoonfinMessagesController : ControllerBase
{
    private readonly MoonfinSettingsService _settingsService;
    private readonly NotificationStore _notificationStore;
    private readonly PushDeliveryService _pushDelivery;

    public MoonfinMessagesController(
        MoonfinSettingsService settingsService,
        NotificationStore notificationStore,
        PushDeliveryService pushDelivery)
    {
        _settingsService = settingsService;
        _notificationStore = notificationStore;
        _pushDelivery = pushDelivery;
    }

    /// <summary>
    /// Returns the messages the calling user should see right now.
    /// </summary>
    [HttpGet("Messages")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetMessages()
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config?.EnableSettingsSync != true)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Settings sync is disabled" });
        }

        var userId = this.GetUserIdFromClaims();
        if (userId == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        // Filtering happens here, not on the client. Sending everything and hiding it in the
        // app would let any user read messages meant for someone else.
        var isAdmin = this.IsAdminFromClaims();
        var now = DateTime.UtcNow;
        var items = config.Messages
            .Where(m => m.IsVisibleTo(userId.Value, isAdmin, now))
            .OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.CreatedUtc)
            .ToList();

        return Ok(new { items });
    }

    /// <summary>
    /// Returns every message, including scheduled and expired ones, for the admin panel.
    /// </summary>
    [HttpGet("Admin/Messages")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAdminMessages()
    {
        var messages = MoonfinPlugin.Instance?.Configuration.Messages ?? new List<ServerMessage>();

        return Ok(new
        {
            items = messages
                .OrderByDescending(m => m.Pinned)
                .ThenByDescending(m => m.CreatedUtc)
                .ToList()
        });
    }

    /// <summary>
    /// Creates a message, or replaces one when the ID already exists.
    /// </summary>
    [HttpPost("Admin/Messages")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SaveMessage([FromBody] ServerMessage message)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Plugin is not ready" });
        }

        if (message == null)
        {
            return BadRequest(new { error = "A message body is required" });
        }

        message.Sanitize();

        if (message.Title.Length == 0 && message.Body.Length == 0)
        {
            return BadRequest(new { error = "A title or a body is required" });
        }

        var messages = plugin.Configuration.Messages;
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
            message.CreatedByUserId = this.GetUserIdFromClaims()?.ToString("N");
        }

        messages.Add(message);
        ServerMessage.Prune(messages);
        plugin.SaveConfiguration();

        _settingsService.BroadcastSystemEvent("messagesChanged");

        // Only push for messages meant to interrupt. An inbox message can wait until the user
        // opens the app.
        if (existing == null && message.Delivery != ServerMessage.DeliveryInbox)
        {
            SendPush(message);
        }

        return Ok(new { success = true, item = message });
    }

    /// <summary>
    /// Deletes one message by ID.
    /// </summary>
    [HttpDelete("Admin/Messages/{messageId}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteMessage([FromRoute] string messageId)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Plugin is not ready" });
        }

        var removed = plugin.Configuration.Messages
            .RemoveAll(m => string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return NotFound(new { error = "Message not found" });
        }

        plugin.SaveConfiguration();
        _settingsService.BroadcastSystemEvent("messagesChanged");

        return Ok(new { success = true });
    }

    /// <summary>
    /// Queues a push so the message also reaches users who do not have the app open.
    /// The route tells the app to open the messages window on tap.
    /// </summary>
    private void SendPush(ServerMessage message)
    {
        // Admin-only messages get no push: we cannot tell who is an admin without the user
        // manager, and admins still see them next time they open the app.
        if (message.Audience == ServerMessage.AudienceAdmins)
        {
            return;
        }

        var title = message.Title.Length > 0 ? message.Title : "Message from your server";
        var body = message.Body;

        foreach (var userId in _notificationStore.GetUsersWithDevices())
        {
            if (message.Audience == ServerMessage.AudienceUsers &&
                !message.TargetUserIds.Any(id =>
                    Guid.TryParse(id, out var target) && target == userId))
            {
                continue;
            }

            _pushDelivery.QueueToUser(userId, title, body, route: "messages");
        }
    }
}
