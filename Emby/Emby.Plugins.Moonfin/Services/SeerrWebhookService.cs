using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Turns Seerr webhook payloads into per-user Moonfin notifications and delivers them over
    /// the session websocket (foreground clients) plus push for backgrounded ones. MEDIA_PENDING
    /// goes to request approvers, MEDIA_AVAILABLE goes to the original requester, and issue events
    /// go to issue managers and reporters.
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
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, long> _recent = new ConcurrentDictionary<string, long>();
        private const long DebounceWindowMs = 60_000;

        public SeerrWebhookService(
            SeerrSessionService sessionService,
            MoonfinSettingsService settingsService,
            NotificationStore store,
            PushDeliveryService pushDelivery,
            ILogger logger)
        {
            _sessionService = sessionService;
            _settingsService = settingsService;
            _store = store;
            _pushDelivery = pushDelivery;
            _logger = logger;
        }

        private ILibraryManager? LibraryManager => PluginServices.LibraryManager;

        public Task HandleWebhookAsync(JsonElement payload)
        {
            try
            {
                var notificationType = GetString(payload, "notification_type");
                if (string.IsNullOrEmpty(notificationType))
                    return Task.CompletedTask;

                // Snapshot the sessions once. Each EnumerateSessions call re-reads every session
                // file from disk, and the handlers below map users several times per webhook.
                var sessions = _sessionService.EnumerateSessions().ToList();

                switch (notificationType!.ToUpperInvariant())
                {
                    case "MEDIA_PENDING":
                        HandlePending(payload, sessions);
                        break;
                    case "MEDIA_APPROVED":
                        HandleRequesterDecision(payload, "MEDIA_APPROVED", "Request approved", "approved", sessions);
                        break;
                    case "MEDIA_DECLINED":
                        HandleRequesterDecision(payload, "MEDIA_DECLINED", "Request declined", "declined", sessions);
                        break;
                    case "MEDIA_AVAILABLE":
                        HandleAvailable(payload, sessions);
                        break;
                    case "ISSUE_CREATED":
                        HandleIssueCreated(payload, sessions);
                        break;
                    case "ISSUE_COMMENT":
                        HandleIssueComment(payload, sessions);
                        break;
                    case "ISSUE_RESOLVED":
                        HandleIssueStatus(payload, "ISSUE_RESOLVED", "Issue resolved", "resolved", sessions);
                        break;
                    case "ISSUE_REOPENED":
                        HandleIssueStatus(payload, "ISSUE_REOPENED", "Issue reopened", "reopened", sessions);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to handle Seerr webhook", ex);
            }

            return Task.CompletedTask;
        }

        private void HandlePending(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "a title";
            var requester = GetRequester(payload);
            var (tmdbId, _, mediaType) = GetMedia(payload);
            if (string.IsNullOrEmpty(tmdbId))
            {
                _logger.Warn("Seerr webhook dropped: no tmdbId (type MEDIA_PENDING)");
                return;
            }

            var route = $"/seerr/media/{tmdbId}?mediaType={NormalizeMediaType(mediaType)}";
            var title = "New request";
            var body = $"{requester ?? "Someone"} requested {subject}";

            var requesterJellyfinId = GetRequesterJellyfinId(payload, sessions);
            var requestId = GetRequestId(payload);

            foreach (var userId in _store.GetUsersWantingNewRequests())
            {
                if (requesterJellyfinId.HasValue && userId == requesterJellyfinId.Value)
                    continue;

                var session = sessions.FirstOrDefault(s => s.JellyfinUserId == userId);
                if (session == null || !CanManageRequests(session))
                    continue;

                Deliver(userId, tmdbId!, "MEDIA_PENDING", title, body, route, requestId);
            }
        }

        private void HandleAvailable(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "Your request";
            var (tmdbId, tvdbId, mediaType) = GetMedia(payload);
            if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId))
            {
                _logger.Warn("Seerr webhook dropped: no tmdbId (type MEDIA_AVAILABLE)");
                return;
            }

            var targetUserId = GetRequesterJellyfinId(payload, sessions);
            if (targetUserId == null)
            {
                _logger.Warn("Seerr webhook dropped: could not resolve the requester");
                return;
            }

            var prefs = _store.GetPrefs(targetUserId.Value);
            if (!prefs.NotifyOnLibraryAdded)
            {
                _logger.Info("Seerr webhook skipped: user {0} has library-added notifications off", targetUserId.Value);
                return;
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
        }

        // Approved/declined both notify the original requester, gated by their library-added pref.
        private void HandleRequesterDecision(JsonElement payload, string notificationType, string title, string verb, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "Your request";
            var (tmdbId, tvdbId, mediaType) = GetMedia(payload);
            if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId))
            {
                _logger.Warn("Seerr webhook dropped: no tmdbId (type " + notificationType + ")");
                return;
            }

            var targetUserId = GetRequesterJellyfinId(payload, sessions);
            if (targetUserId == null)
            {
                _logger.Warn("Seerr webhook dropped: could not resolve the requester");
                return;
            }

            var prefs = _store.GetPrefs(targetUserId.Value);
            if (!prefs.NotifyOnLibraryAdded)
            {
                _logger.Info("Seerr webhook skipped: user {0} has library-added notifications off", targetUserId.Value);
                return;
            }

            var route = $"/seerr/media/{tmdbId}?mediaType={NormalizeMediaType(mediaType)}";
            var body = $"{subject} was {verb}";

            Deliver(targetUserId.Value, tmdbId ?? tvdbId ?? subject, notificationType, title, body, route);
        }

        // New issues go to everyone who can manage them, except the reporter.
        private void HandleIssueCreated(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "a title";
            var reporter = GetIssueReporterName(payload);
            var body = $"{reporter ?? "Someone"} reported an issue with {subject}";

            NotifyIssueManagers(
                GetIssueReporterJellyfinId(payload, sessions),
                GetIssueId(payload) ?? subject,
                "ISSUE_CREATED",
                "New issue",
                body,
                sessions);
        }

        // A comment notifies the other side of the thread: managers hear from the
        // reporter, the reporter hears from anyone else. The commenter never
        // notifies themselves.
        private void HandleIssueComment(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "a title";
            var issueKey = GetIssueId(payload) ?? subject;
            var reporter = GetIssueReporterName(payload);
            var reporterJellyfinId = GetIssueReporterJellyfinId(payload, sessions);
            var commenter = GetCommenterName(payload);
            var commenterJellyfinId = MapSeerrUsername(commenter, sessions);

            var title = "Issue comment";
            var message = GetCommentMessage(payload);
            var body = string.IsNullOrWhiteSpace(message)
                ? $"{commenter ?? "Someone"} commented on {subject}"
                : $"{commenter ?? "Someone"} on {subject}: {Truncate(message!, 120)}";

            var commenterIsReporter = commenter != null &&
                string.Equals(commenter, reporter, StringComparison.OrdinalIgnoreCase);

            if (commenterIsReporter)
            {
                NotifyIssueManagers(commenterJellyfinId, issueKey, "ISSUE_COMMENT", title, body, sessions);
                return;
            }

            if (reporterJellyfinId == null ||
                (commenterJellyfinId.HasValue && reporterJellyfinId.Value == commenterJellyfinId.Value))
                return;

            if (_store.GetPrefs(reporterJellyfinId.Value).NotifyOnIssues)
                Deliver(reporterJellyfinId.Value, issueKey, "ISSUE_COMMENT", title, body, IssuesRoute);
        }

        private void NotifyIssueManagers(
            Guid? excludeUserId,
            string mediaKey,
            string notificationType,
            string title,
            string body,
            IReadOnlyList<SeerrSession> sessions)
        {
            var seen = new HashSet<Guid>();
            foreach (var session in sessions)
            {
                var userId = session.JellyfinUserId;
                if (excludeUserId.HasValue && userId == excludeUserId.Value)
                    continue;

                if (!seen.Add(userId))
                    continue;

                if (!CanManageIssues(session) || !_store.GetPrefs(userId).NotifyOnIssues)
                    continue;

                Deliver(userId, mediaKey, notificationType, title, body, IssuesRoute);
            }
        }

        // Resolved and reopened notify the reporter.
        private void HandleIssueStatus(JsonElement payload, string notificationType, string title, string verb, IReadOnlyList<SeerrSession> sessions)
        {
            var subject = GetString(payload, "subject") ?? "Your issue";
            var issueId = GetIssueId(payload);
            var reporterJellyfinId = GetIssueReporterJellyfinId(payload, sessions);
            if (reporterJellyfinId == null)
                return;

            // When a comment block rides along, its author performed the action,
            // so the reporter resolving their own issue stays silent.
            var actor = GetCommenterName(payload);
            var actorJellyfinId = MapSeerrUsername(actor, sessions);
            if (actorJellyfinId.HasValue && actorJellyfinId.Value == reporterJellyfinId.Value)
                return;

            if (!_store.GetPrefs(reporterJellyfinId.Value).NotifyOnIssues)
                return;

            var body = $"The issue with {subject} was {verb}";
            Deliver(reporterJellyfinId.Value, issueId ?? subject, notificationType, title, body, IssuesRoute);
        }

        private void Deliver(Guid userId, string mediaKey, string notificationType, string title, string body, string route, string? requestId = null)
        {
            var dedupeKey = $"{userId}:{mediaKey}:{notificationType}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_recent.TryGetValue(dedupeKey, out var last) && now - last < DebounceWindowMs)
                return;

            _recent[dedupeKey] = now;
            PruneRecent(now);

            // A request notification carries the id and a kind marker so the client can show
            // Approve/Deny. Other events keep the plain shape.
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
                if (now - pair.Value > DebounceWindowMs)
                    _recent.TryRemove(pair.Key, out _);
        }

        private static bool CanManageRequests(SeerrSession session)
        {
            if (session.SeerrUserId == OwnerSeerrUserId)
                return true;

            return (session.Permissions & AdminBit) != 0 || (session.Permissions & ManageRequestsBit) != 0;
        }

        private static bool CanManageIssues(SeerrSession session)
        {
            if (session.SeerrUserId == OwnerSeerrUserId)
                return true;

            return (session.Permissions & AdminBit) != 0 || (session.Permissions & ManageIssuesBit) != 0;
        }

        private static string? GetIssueId(JsonElement payload)
        {
            if (payload.TryGetProperty("issue", out var issue) &&
                issue.ValueKind == JsonValueKind.Object)
                return GetString(issue, "issue_id");

            return null;
        }

        private static string? GetIssueReporterName(JsonElement payload)
        {
            if (payload.TryGetProperty("issue", out var issue) &&
                issue.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(issue, "reportedBy_username");
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            return GetString(payload, "notifyuser_username");
        }

        // Map a Seerr internal user id to the Emby user id over an in-memory session snapshot.
        private static Guid? MapSeerrUser(int seerrUserId, IReadOnlyList<SeerrSession> sessions)
        {
            foreach (var s in sessions)
                if (s.SeerrUserId == seerrUserId) return s.JellyfinUserId;
            return null;
        }

        // Map a Seerr username to the Emby user id (case-insensitive) over the snapshot.
        private static Guid? MapSeerrUsername(string? username, IReadOnlyList<SeerrSession> sessions)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            foreach (var s in sessions)
                if (string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase))
                    return s.JellyfinUserId;
            return null;
        }

        // Seerr's webhook exposes no server user id for issue reporters, so this maps
        // the username against a stored session, with the notify-user id fallback.
        private Guid? GetIssueReporterJellyfinId(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var byUsername = MapSeerrUsername(GetIssueReporterName(payload), sessions);
            if (byUsername != null)
                return byUsername;

            if (TryGetInt(payload, "notifyuser_id", out var notifyUserId))
                return MapSeerrUser(notifyUserId, sessions);

            return null;
        }

        private static string? GetCommenterName(JsonElement payload)
        {
            if (payload.TryGetProperty("comment", out var comment) &&
                comment.ValueKind == JsonValueKind.Object)
                return GetString(comment, "commentedBy_username");

            return null;
        }

        private static string? GetCommentMessage(JsonElement payload)
        {
            if (payload.TryGetProperty("comment", out var comment) &&
                comment.ValueKind == JsonValueKind.Object)
                return GetString(comment, "comment_message");

            return null;
        }

        private static string Truncate(string value, int maxLength)
        {
            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength).TrimEnd() + "...";
        }

        private string? ResolveLibraryItemId(string? tmdbId, string? tvdbId)
        {
            var byTmdb = FindByProviderId("Tmdb", tmdbId);
            if (byTmdb != null)
                return byTmdb;

            return FindByProviderId("Tvdb", tvdbId);
        }

        // Looks up a movie or series by a single provider id.
        private string? FindByProviderId(string provider, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            var lm = LibraryManager;
            if (lm == null)
                return null;

            try
            {
                var query = new InternalItemsQuery
                {
                    AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>(provider, value!)
                    },
                    IncludeItemTypes = new[] { "Movie", "Series" },
                    IsVirtualItem = false,
                    Recursive = true,
                    Limit = 1
                };

                var item = lm.GetItemList(query).FirstOrDefault();
                return item?.Id.ToString("N");
            }
            catch (Exception ex)
            {
                _logger.Debug($"Provider-id lookup failed for {provider}={value}: {ex.Message}");
                return null;
            }
        }

        private Guid? GetRequesterJellyfinId(JsonElement payload, IReadOnlyList<SeerrSession> sessions)
        {
            var hasRequest = payload.TryGetProperty("request", out var request) &&
                request.ValueKind == JsonValueKind.Object;

            // Primary: Seerr's KeyMap exposes the requester's Emby user GUID directly, so
            // no session lookup is needed. It may be in 32-char "N" or dashed "D" form.
            if (hasRequest)
            {
                var jellyfinGuid = GetString(request, "requestedBy_jellyfinUserId");
                if (!string.IsNullOrEmpty(jellyfinGuid) && Guid.TryParse(jellyfinGuid, out var parsed))
                    return parsed;
            }

            // Fallback: map the requester's username against a stored session.
            if (hasRequest)
            {
                var username = GetString(request, "requestedBy_username");
                var byUsername = MapSeerrUsername(username, sessions);
                if (byUsername != null)
                    return byUsername;
            }

            // Last resort: the numeric Seerr user id (not emitted by default).
            if (hasRequest && TryGetInt(request, "requestedBy_id", out var requestedById))
            {
                var mapped = MapSeerrUser(requestedById, sessions);
                if (mapped != null)
                    return mapped;
            }

            if (TryGetInt(payload, "notifyuser_id", out var notifyUserId))
            {
                var mapped = MapSeerrUser(notifyUserId, sessions);
                if (mapped != null)
                    return mapped;
            }

            // Availability and decision events set notifyuser rather than a full request block,
            // so map that username against a stored session as a last resort.
            var byNotifyUsername = MapSeerrUsername(
                GetString(payload, "notifyuser_username"), sessions);
            if (byNotifyUsername != null)
                return byNotifyUsername;

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
                    return name;
            }

            return GetString(payload, "notifyuser_username");
        }

        // The Seerr request id lives at request.request_id and may arrive as a number or a string.
        private static string? GetRequestId(JsonElement payload)
        {
            if (payload.TryGetProperty("request", out var request) &&
                request.ValueKind == JsonValueKind.Object)
                return GetString(request, "request_id");

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
                            return season;
                    }
                }
            }

            return null;
        }

        private static string NormalizeMediaType(string? mediaType)
        {
            if (string.IsNullOrEmpty(mediaType))
                return "movie";

            switch (mediaType!.ToLowerInvariant())
            {
                case "tv":
                case "show":
                case "series":
                    return "tv";
                default:
                    return "movie";
            }
        }

        private static string? GetString(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var prop))
            {
                switch (prop.ValueKind)
                {
                    case JsonValueKind.String:
                        return prop.GetString();
                    case JsonValueKind.Number:
                        return prop.ToString();
                    default:
                        return null;
                }
            }

            return null;
        }

        private static bool TryGetInt(JsonElement element, string name, out int value)
        {
            value = 0;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var prop))
                return false;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
                return true;

            return prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out value);
        }
    }
}
