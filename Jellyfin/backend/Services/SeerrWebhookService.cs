using System.Collections.Concurrent;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Turns Seerr webhook payloads into per-user Moonfin notifications and delivers
/// them over the SSE stream, then also as FCM push for backgrounded clients.
/// </summary>
public class SeerrWebhookService
{
    private const int ManageRequestsBit = 16;
    private const int ManageIssuesBit = 1048576;
    private const int AdminBit = 2;
    private const int OwnerSeerrUserId = 1;

    private const string IssuesRoute = "/seerr/requests?tab=issues";

    private readonly SeerrSessionService _sessionService;
    private readonly MoonfinSettingsService _settingsService;
    private readonly NotificationStore _store;
    private readonly PushDeliveryService _pushDelivery;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SeerrWebhookService> _logger;

    private readonly ConcurrentDictionary<string, long> _recent = new();
    private static readonly long DebounceWindowMs = 60_000;

    public SeerrWebhookService(
        SeerrSessionService sessionService,
        MoonfinSettingsService settingsService,
        NotificationStore store,
        PushDeliveryService pushDelivery,
        ILibraryManager libraryManager,
        ILogger<SeerrWebhookService> logger)
    {
        _sessionService = sessionService;
        _settingsService = settingsService;
        _store = store;
        _pushDelivery = pushDelivery;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task HandleWebhookAsync(JsonElement payload)
    {
        try
        {
            var notificationType = GetString(payload, "notification_type");
            if (string.IsNullOrEmpty(notificationType))
            {
                return;
            }

            _logger.LogInformation("Seerr webhook received: {Type}", notificationType);

            switch (notificationType.ToUpperInvariant())
            {
                case "MEDIA_PENDING":
                    await HandlePendingAsync(payload);
                    break;
                case "MEDIA_APPROVED":
                    await HandleRequesterDecisionAsync(payload, "MEDIA_APPROVED", "Request approved", "approved");
                    break;
                case "MEDIA_DECLINED":
                    await HandleRequesterDecisionAsync(payload, "MEDIA_DECLINED", "Request declined", "declined");
                    break;
                case "MEDIA_AVAILABLE":
                    await HandleAvailableAsync(payload);
                    break;
                case "ISSUE_CREATED":
                    await HandleIssueCreatedAsync(payload);
                    break;
                case "ISSUE_COMMENT":
                    await HandleIssueCommentAsync(payload);
                    break;
                case "ISSUE_RESOLVED":
                    await HandleIssueStatusAsync(payload, "ISSUE_RESOLVED", "Issue resolved", "resolved");
                    break;
                case "ISSUE_REOPENED":
                    await HandleIssueStatusAsync(payload, "ISSUE_REOPENED", "Issue reopened", "reopened");
                    break;
                default:
                    _logger.LogDebug("Ignoring Seerr webhook type {Type}", notificationType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Seerr webhook");
        }
    }

    private Task HandlePendingAsync(JsonElement payload)
    {
        var subject = GetString(payload, "subject") ?? "a title";
        var requester = GetRequester(payload);
        var (tmdbId, _, mediaType) = GetMedia(payload);
        if (string.IsNullOrEmpty(tmdbId))
        {
            _logger.LogWarning("Seerr webhook dropped: no tmdbId (type MEDIA_PENDING)");
            return Task.CompletedTask;
        }

        var route = $"/seerr/media/{tmdbId}?mediaType={NormalizeMediaType(mediaType)}";
        var title = "New request";
        var body = $"{requester ?? "Someone"} requested {subject}";

        var requesterJellyfinId = GetRequesterJellyfinId(payload);
        var requestId = GetRequestId(payload);

        foreach (var userId in _store.GetUsersWantingNewRequests())
        {
            if (requesterJellyfinId.HasValue && userId == requesterJellyfinId.Value)
            {
                continue;
            }

            var session = _sessionService.EnumerateSessions()
                .FirstOrDefault(s => s.JellyfinUserId == userId);
            if (session == null || !CanManageRequests(session))
            {
                continue;
            }

            Deliver(userId, tmdbId, "MEDIA_PENDING", title, body, route, requestId);
        }

        return Task.CompletedTask;
    }

    private Task HandleAvailableAsync(JsonElement payload)
    {
        var subject = GetString(payload, "subject") ?? "Your request";
        var (tmdbId, tvdbId, mediaType) = GetMedia(payload);
        if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId))
        {
            _logger.LogWarning("Seerr webhook dropped: no tmdbId (type MEDIA_AVAILABLE)");
            return Task.CompletedTask;
        }

        var targetUserId = GetRequesterJellyfinId(payload);
        if (targetUserId == null)
        {
            _logger.LogWarning("Seerr webhook dropped: could not resolve the requester");
            return Task.CompletedTask;
        }

        var prefs = _store.GetPrefs(targetUserId.Value);
        if (!prefs.NotifyOnLibraryAdded)
        {
            _logger.LogInformation("Seerr webhook skipped: user {UserId} has library-added notifications off", targetUserId.Value);
            return Task.CompletedTask;
        }

        var jellyfinItemId = ResolveLibraryItemId(tmdbId, tvdbId);
        var route = jellyfinItemId != null
            ? $"/item/{jellyfinItemId}"
            : $"/seerr/media/{tmdbId}?mediaType={NormalizeMediaType(mediaType)}";

        var season = GetSeason(payload);
        var body = season.HasValue
            ? $"Season {season.Value} of {subject} is now available"
            : $"{subject} is now available";

        Deliver(targetUserId.Value, tmdbId ?? tvdbId ?? subject, "MEDIA_AVAILABLE", "Now available", body, route);
        return Task.CompletedTask;
    }

    // Approved/declined both notify the original requester, gated by their library-added pref.
    private Task HandleRequesterDecisionAsync(JsonElement payload, string notificationType, string title, string verb)
    {
        var subject = GetString(payload, "subject") ?? "Your request";
        var (tmdbId, tvdbId, mediaType) = GetMedia(payload);
        if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId))
        {
            _logger.LogWarning("Seerr webhook dropped: no tmdbId (type {Type})", notificationType);
            return Task.CompletedTask;
        }

        var targetUserId = GetRequesterJellyfinId(payload);
        if (targetUserId == null)
        {
            _logger.LogWarning("Seerr webhook dropped: could not resolve the requester");
            return Task.CompletedTask;
        }

        var prefs = _store.GetPrefs(targetUserId.Value);
        if (!prefs.NotifyOnLibraryAdded)
        {
            _logger.LogInformation("Seerr webhook skipped: user {UserId} has library-added notifications off", targetUserId.Value);
            return Task.CompletedTask;
        }

        var route = $"/seerr/media/{tmdbId}?mediaType={NormalizeMediaType(mediaType)}";
        var body = $"{subject} was {verb}";

        Deliver(targetUserId.Value, tmdbId ?? tvdbId ?? subject, notificationType, title, body, route);
        return Task.CompletedTask;
    }

    // New issues go to everyone who can manage them, except the reporter.
    private Task HandleIssueCreatedAsync(JsonElement payload)
    {
        var subject = GetString(payload, "subject") ?? "a title";
        var reporter = GetIssueReporterName(payload);
        var body = $"{reporter ?? "Someone"} reported an issue with {subject}";

        NotifyIssueManagers(
            GetIssueReporterJellyfinId(payload),
            GetIssueId(payload) ?? subject,
            "ISSUE_CREATED",
            "New issue",
            body);

        return Task.CompletedTask;
    }

    // A comment notifies the other side of the thread: managers hear from the
    // reporter, the reporter hears from anyone else. The commenter never
    // notifies themselves.
    private Task HandleIssueCommentAsync(JsonElement payload)
    {
        var subject = GetString(payload, "subject") ?? "a title";
        var issueKey = GetIssueId(payload) ?? subject;
        var reporter = GetIssueReporterName(payload);
        var reporterJellyfinId = GetIssueReporterJellyfinId(payload);
        var commenter = GetCommenterName(payload);
        var commenterJellyfinId = _sessionService.GetJellyfinUserForSeerrUsername(commenter);

        var title = "Issue comment";
        var message = GetCommentMessage(payload);
        var body = string.IsNullOrWhiteSpace(message)
            ? $"{commenter ?? "Someone"} commented on {subject}"
            : $"{commenter ?? "Someone"} on {subject}: {Truncate(message, 120)}";

        var commenterIsReporter = commenter != null &&
            string.Equals(commenter, reporter, StringComparison.OrdinalIgnoreCase);

        if (commenterIsReporter)
        {
            NotifyIssueManagers(commenterJellyfinId, issueKey, "ISSUE_COMMENT", title, body);
            return Task.CompletedTask;
        }

        if (reporterJellyfinId == null ||
            (commenterJellyfinId.HasValue && reporterJellyfinId.Value == commenterJellyfinId.Value))
        {
            return Task.CompletedTask;
        }

        if (_store.GetPrefs(reporterJellyfinId.Value).NotifyOnIssues)
        {
            Deliver(reporterJellyfinId.Value, issueKey, "ISSUE_COMMENT", title, body, IssuesRoute);
        }

        return Task.CompletedTask;
    }

    private void NotifyIssueManagers(
        Guid? excludeUserId,
        string mediaKey,
        string notificationType,
        string title,
        string body)
    {
        var seen = new HashSet<Guid>();
        foreach (var session in _sessionService.EnumerateSessions())
        {
            var userId = session.JellyfinUserId;
            if (excludeUserId.HasValue && userId == excludeUserId.Value)
            {
                continue;
            }

            if (!seen.Add(userId))
            {
                continue;
            }

            if (!CanManageIssues(session) || !_store.GetPrefs(userId).NotifyOnIssues)
            {
                continue;
            }

            Deliver(userId, mediaKey, notificationType, title, body, IssuesRoute);
        }
    }

    // Resolved and reopened notify the reporter.
    private Task HandleIssueStatusAsync(JsonElement payload, string notificationType, string title, string verb)
    {
        var subject = GetString(payload, "subject") ?? "Your issue";
        var issueId = GetIssueId(payload);
        var reporterJellyfinId = GetIssueReporterJellyfinId(payload);
        if (reporterJellyfinId == null)
        {
            return Task.CompletedTask;
        }

        // When a comment block rides along, its author performed the action,
        // so the reporter resolving their own issue stays silent.
        var actor = GetCommenterName(payload);
        var actorJellyfinId = _sessionService.GetJellyfinUserForSeerrUsername(actor);
        if (actorJellyfinId.HasValue && actorJellyfinId.Value == reporterJellyfinId.Value)
        {
            return Task.CompletedTask;
        }

        if (!_store.GetPrefs(reporterJellyfinId.Value).NotifyOnIssues)
        {
            return Task.CompletedTask;
        }

        var body = $"The issue with {subject} was {verb}";
        Deliver(reporterJellyfinId.Value, issueId ?? subject, notificationType, title, body, IssuesRoute);
        return Task.CompletedTask;
    }

    private void Deliver(Guid userId, string mediaKey, string notificationType, string title, string body, string route, string? requestId = null)
    {
        var dedupeKey = $"{userId}:{mediaKey}:{notificationType}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_recent.TryGetValue(dedupeKey, out var last) && now - last < DebounceWindowMs)
        {
            return;
        }

        _recent[dedupeKey] = now;
        PruneRecent(now);

        // A request notification carries the id and a kind marker so the client can show
        // Approve/Deny; other events keep the plain shape.
        var json = requestId != null
            ? JsonSerializer.Serialize(new
            {
                type = "seerrNotification",
                title,
                body,
                route,
                requestId,
                kind = "request"
            })
            : JsonSerializer.Serialize(new
            {
                type = "seerrNotification",
                title,
                body,
                route
            });

        _settingsService.NotifyUser(userId, json);
        _pushDelivery.QueueToUser(userId, title, body, route, requestId);
    }

    private void PruneRecent(long now)
    {
        foreach (var pair in _recent)
        {
            if (now - pair.Value > DebounceWindowMs)
            {
                _recent.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool CanManageRequests(SeerrSession session)
    {
        if (session.SeerrUserId == OwnerSeerrUserId)
        {
            return true;
        }

        return (session.Permissions & AdminBit) != 0 || (session.Permissions & ManageRequestsBit) != 0;
    }

    private static bool CanManageIssues(SeerrSession session)
    {
        if (session.SeerrUserId == OwnerSeerrUserId)
        {
            return true;
        }

        return (session.Permissions & AdminBit) != 0 || (session.Permissions & ManageIssuesBit) != 0;
    }

    private static string? GetIssueId(JsonElement payload)
    {
        if (payload.TryGetProperty("issue", out var issue) &&
            issue.ValueKind == JsonValueKind.Object)
        {
            return GetString(issue, "issue_id");
        }

        return null;
    }

    private static string? GetIssueReporterName(JsonElement payload)
    {
        if (payload.TryGetProperty("issue", out var issue) &&
            issue.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(issue, "reportedBy_username");
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return GetString(payload, "notifyuser_username");
    }

    // Seerr's webhook exposes no Jellyfin id for issue reporters, so this maps
    // the username against a stored session, with the notify-user id fallback.
    private Guid? GetIssueReporterJellyfinId(JsonElement payload)
    {
        var byUsername = _sessionService.GetJellyfinUserForSeerrUsername(
            GetIssueReporterName(payload));
        if (byUsername != null)
        {
            return byUsername;
        }

        if (TryGetInt(payload, "notifyuser_id", out var notifyUserId))
        {
            return _sessionService.GetJellyfinUserForSeerrUser(notifyUserId);
        }

        return null;
    }

    private static string? GetCommenterName(JsonElement payload)
    {
        if (payload.TryGetProperty("comment", out var comment) &&
            comment.ValueKind == JsonValueKind.Object)
        {
            return GetString(comment, "commentedBy_username");
        }

        return null;
    }

    private static string? GetCommentMessage(JsonElement payload)
    {
        if (payload.TryGetProperty("comment", out var comment) &&
            comment.ValueKind == JsonValueKind.Object)
        {
            return GetString(comment, "comment_message");
        }

        return null;
    }

    private static string Truncate(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..maxLength].TrimEnd()}...";
    }

    private string? ResolveLibraryItemId(string? tmdbId, string? tvdbId)
    {
        var byTmdb = FindByProviderId(MetadataProvider.Tmdb, tmdbId);
        if (byTmdb != null)
        {
            return byTmdb;
        }

        return FindByProviderId(MetadataProvider.Tvdb, tvdbId);
    }

    private string? FindByProviderId(MetadataProvider provider, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var query = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string>
                {
                    [provider.ToString()] = value
                },
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true,
                Limit = 1
            };

            var item = _libraryManager.GetItemList(query).FirstOrDefault();
            return item?.Id.ToString("N");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Provider-id lookup failed for {Provider}={Value}", provider, value);
            return null;
        }
    }

    private Guid? GetRequesterJellyfinId(JsonElement payload)
    {
        var hasRequest = payload.TryGetProperty("request", out var request) &&
            request.ValueKind == JsonValueKind.Object;

        // Primary: Seerr's KeyMap exposes the requester's Jellyfin user GUID directly, so
        // no session lookup is needed. It may be in 32-char "N" or dashed "D" form.
        if (hasRequest)
        {
            var jellyfinGuid = GetString(request, "requestedBy_jellyfinUserId");
            if (!string.IsNullOrEmpty(jellyfinGuid) && Guid.TryParse(jellyfinGuid, out var parsed))
            {
                return parsed;
            }
        }

        // Fallback: map the requester's username against a stored session.
        if (hasRequest)
        {
            var username = GetString(request, "requestedBy_username");
            var byUsername = _sessionService.GetJellyfinUserForSeerrUsername(username);
            if (byUsername != null)
            {
                return byUsername;
            }
        }

        // Last resort: the numeric Seerr user id (not emitted by default).
        if (hasRequest && TryGetInt(request, "requestedBy_id", out var requestedById))
        {
            var mapped = _sessionService.GetJellyfinUserForSeerrUser(requestedById);
            if (mapped != null)
            {
                return mapped;
            }
        }

        if (TryGetInt(payload, "notifyuser_id", out var notifyUserId))
        {
            var mapped = _sessionService.GetJellyfinUserForSeerrUser(notifyUserId);
            if (mapped != null)
            {
                return mapped;
            }
        }

        // Availability and decision events set notifyuser rather than a full request block,
        // so map that username against a stored session as a last resort.
        var byNotifyUsername = _sessionService.GetJellyfinUserForSeerrUsername(
            GetString(payload, "notifyuser_username"));
        if (byNotifyUsername != null)
        {
            return byNotifyUsername;
        }

        return null;
    }

    private static string? GetRequester(JsonElement payload)
    {
        if (payload.TryGetProperty("request", out var request) &&
            request.ValueKind == JsonValueKind.Object)
        {
            var name = GetString(request, "requestedBy_username")
                ?? GetString(request, "requestedBy_displayname");
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        return GetString(payload, "notifyuser_username");
    }

    // The Seerr request id lives at request.request_id and may arrive as a number or a string.
    private static string? GetRequestId(JsonElement payload)
    {
        if (payload.TryGetProperty("request", out var request) &&
            request.ValueKind == JsonValueKind.Object)
        {
            return GetString(request, "request_id");
        }

        return null;
    }

    private static (string? TmdbId, string? TvdbId, string? MediaType) GetMedia(JsonElement payload)
    {
        if (payload.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            return (
                GetString(media, "tmdbId"),
                GetString(media, "tvdbId"),
                GetString(media, "media_type"));
        }

        return (null, null, null);
    }

    private static int? GetSeason(JsonElement payload)
    {
        if (payload.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in extra.EnumerateArray())
            {
                var name = GetString(entry, "name");
                if (string.Equals(name, "Requested Seasons", StringComparison.OrdinalIgnoreCase))
                {
                    var value = GetString(entry, "value");
                    if (int.TryParse(value, out var season))
                    {
                        return season;
                    }
                }
            }
        }

        return null;
    }

    private static string NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return "movie";
        }

        return mediaType.ToLowerInvariant() switch
        {
            "tv" or "show" or "series" => "tv",
            _ => "movie"
        };
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Number => prop.ToString(),
                _ => null
            };
        }

        return null;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
        {
            return true;
        }

        return prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value);
    }
}
