using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonfin.WatchParty;

/// <summary>The admin's Watch Parties settings, read on every call so changes apply at once.</summary>
public sealed class WatchPartySettings
{
    public bool Enabled { get; set; } = true;

    public bool Chat { get; set; } = true;

    public bool Reactions { get; set; } = true;

    public bool Alerts { get; set; } = true;

    /// <summary>Whether hosts may pick "Everyone can control playback".</summary>
    public bool EveryoneControl { get; set; } = true;

    public int MaxMembers { get; set; } = 20;
}

/// <summary>An item a user can play, with what the room shows about it.</summary>
public sealed class WatchPartyItem
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The server's item type, like Movie or Episode.</summary>
    public string Type { get; set; } = string.Empty;

    public string? SeriesName { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public int? Year { get; set; }

    /// <summary>How long the item runs, or null when the server doesn't know, which never ends a run on its own.</summary>
    public double? RuntimeSeconds { get; set; }

    /// <summary>"Arcane · S2:E1" for an episode, the title otherwise.</summary>
    public string Label => SeriesName is { Length: > 0 } series && Season is int s && Episode is int e
        ? $"{series} · S{s}:E{e}"
        : Title;
}

/// <summary>Who a user is to the room.</summary>
public sealed class WatchPartyProfile
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Server-relative, or null when the user has no picture.</summary>
    public string? AvatarUrl { get; set; }
}

/// <summary>The signed-in user and the device they call from.</summary>
public sealed class WatchPartyCaller
{
    public WatchPartyCaller(string userId, string? deviceId, string? publicBase = null)
    {
        UserId = userId;
        DeviceId = deviceId ?? string.Empty;
        PublicBase = publicBase;
    }

    public string UserId { get; }

    public string DeviceId { get; }

    /// <summary>The server's address for invite links: its configured public one, or the request's.</summary>
    public string? PublicBase { get; }
}

/// <summary>What the server the engine runs in provides.</summary>
public interface IWatchPartyHost
{
    /// <summary>The item, or null when it doesn't exist or the user can't play it.</summary>
    WatchPartyItem? FindItem(string userId, string contentId);

    WatchPartyProfile Profile(string userId);

    /// <summary>A room got its first item. The host sends the alerts, off the caller's thread.</summary>
    void Announce(WatchPartyAnnouncement announcement);
}

/// <summary>A new party, for the users who opted in to alerts.</summary>
public sealed class WatchPartyAnnouncement
{
    public string HostUserId { get; set; } = string.Empty;

    public string HostName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string ContentId { get; set; } = string.Empty;

    public string ContentLabel { get; set; } = string.Empty;

    public int LobbyCount { get; set; }

    /// <summary>Users already in the room, who get no alert.</summary>
    public IReadOnlyCollection<string> MemberUserIds { get; set; } = Array.Empty<string>();

    public string Title => $"{HostName} started a watch party";

    public string Body => $"{ContentLabel} · {LobbyCount} in the lobby";

    public string Route => "/watch-party?code=" + Code;
}

/// <summary>An answer: the status and the body to send as JSON, or no body for 204.</summary>
public sealed class WatchPartyResult
{
    private static readonly Dictionary<string, int> Statuses = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["disabled"] = 403,
        ["not_found"] = 404,
        ["pin_required"] = 403,
        ["wrong_pin"] = 403,
        ["too_many_attempts"] = 429,
        ["full"] = 409,
        ["no_access"] = 403,
        ["not_host"] = 403,
        ["not_allowed"] = 403,
        ["bad_request"] = 400,
    };

    private WatchPartyResult(int status, object? body, int retryAfterSeconds = 0)
    {
        Status = status;
        Body = body;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int Status { get; }

    public object? Body { get; }

    /// <summary>Set on too_many_attempts, for a Retry-After header.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>The error code, or null on success.</summary>
    public string? Code => Body is ErrorDto e ? e.Code : null;

    public static WatchPartyResult Ok(object body) => new WatchPartyResult(200, body);

    public static WatchPartyResult NoContent() => new WatchPartyResult(204, null);

    public static WatchPartyResult Error(string code, string message, int retryAfterSeconds = 0) =>
        new WatchPartyResult(Statuses[code], new ErrorDto(code, message, retryAfterSeconds > 0 ? retryAfterSeconds : (int?)null), retryAfterSeconds);

    public byte[] Json() => Body == null ? Array.Empty<byte>() : WatchPartyJson.Serialize(Body);
}

public sealed class ErrorDto
{
    public ErrorDto(string code, string message, int? retryAfterSeconds)
    {
        Code = code;
        Message = message;
        RetryAfterSeconds = retryAfterSeconds;
    }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("retry_after_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; }
}

public static class WatchPartyJson
{
    private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions();

    private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static byte[] Serialize(object body) => JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), WriteOptions);

    /// <summary>An empty body reads as a blank object. False only when the JSON is broken.</summary>
    public static bool TryRead<T>(byte[]? json, out T value)
        where T : new()
    {
        value = new T();
        if (json == null || json.Length == 0)
        {
            return true;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(json, ReadOptions) ?? new T();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>ISO 8601 UTC with milliseconds, like 2026-11-09T21:04:05.123Z.</summary>
    public static string Time(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public sealed class CreateBody
{
    [JsonPropertyName("room_id")]
    public string? RoomId { get; set; }

    [JsonPropertyName("content_id")]
    public string? ContentId { get; set; }

    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    [JsonPropertyName("guest_control_policy")]
    public string? GuestControlPolicy { get; set; }
}

public sealed class JoinBody
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("room_id")]
    public string? RoomId { get; set; }

    [JsonPropertyName("pin")]
    public string? Pin { get; set; }
}

/// <summary>One body shape for the small host calls, each reading only its own field.</summary>
public sealed class RoomBody
{
    [JsonPropertyName("content_id")]
    public string? ContentId { get; set; }

    [JsonPropertyName("position_seconds")]
    public double? PositionSeconds { get; set; }

    [JsonPropertyName("guest_control_policy")]
    public string? GuestControlPolicy { get; set; }

    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    [JsonPropertyName("voting")]
    public bool? Voting { get; set; }

    [JsonPropertyName("voted")]
    public bool? Voted { get; set; }

    [JsonPropertyName("member_id")]
    public string? MemberId { get; set; }
}

public sealed class MessagesBody
{
    [JsonPropertyName("messages")]
    public List<InboundMessage>? Messages { get; set; }
}

/// <summary>Every client message type's fields in one shape.</summary>
public sealed class InboundMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("client_sent_at")]
    public string? ClientSentAt { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("position_seconds")]
    public double? PositionSeconds { get; set; }

    [JsonPropertyName("is_paused")]
    public bool? IsPaused { get; set; }

    [JsonPropertyName("is_ready")]
    public bool? IsReady { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("command_id")]
    public string? CommandId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("reaction")]
    public string? Reaction { get; set; }
}

public sealed class RoomDto
{
    [JsonPropertyName("room_id")] public string RoomId { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("invite_path")] public string InvitePath { get; set; } = string.Empty;
    [JsonPropertyName("invite_url")] public string? InviteUrl { get; set; }
    [JsonPropertyName("phase")] public string Phase { get; set; } = string.Empty;
    [JsonPropertyName("playback_state")] public string PlaybackState { get; set; } = string.Empty;
    [JsonPropertyName("selection_mode")] public string SelectionMode { get; set; } = string.Empty;
    [JsonPropertyName("selection_revision")] public int SelectionRevision { get; set; }
    [JsonPropertyName("selected_content_id")] public string? SelectedContentId { get; set; }
    [JsonPropertyName("selected_file_id")] public string? SelectedFileId { get; set; }
    [JsonPropertyName("guest_control_policy")] public string GuestControlPolicy { get; set; } = string.Empty;
    [JsonPropertyName("is_paused")] public bool IsPaused { get; set; }
    [JsonPropertyName("anchor_position_seconds")] public double AnchorPositionSeconds { get; set; }
    [JsonPropertyName("anchor_at")] public string AnchorAt { get; set; } = string.Empty;
    [JsonPropertyName("generation")] public long Generation { get; set; }
    [JsonPropertyName("self_role")] public string SelfRole { get; set; } = string.Empty;
    [JsonPropertyName("self_can_control_transport")] public bool SelfCanControlTransport { get; set; }
    [JsonPropertyName("self_member_id")] public string SelfMemberId { get; set; } = string.Empty;
    [JsonPropertyName("attached_session_id")] public string? AttachedSessionId { get; set; }
    [JsonPropertyName("members")] public List<MemberDto> Members { get; set; } = new List<MemberDto>();
    [JsonPropertyName("has_pin")] public bool HasPin { get; set; }
    [JsonPropertyName("content_title")] public string? ContentTitle { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("series_name")] public string? SeriesName { get; set; }
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("episode")] public int? Episode { get; set; }
    [JsonPropertyName("chat_enabled")] public bool ChatEnabled { get; set; }
    [JsonPropertyName("reactions_enabled")] public bool ReactionsEnabled { get; set; }
    [JsonPropertyName("max_members")] public int MaxMembers { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
}

public sealed class MemberDto
{
    [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("is_host")] public bool IsHost { get; set; }
    [JsonPropertyName("is_self")] public bool IsSelf { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("user_id")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
}

public sealed class SummaryDto
{
    [JsonPropertyName("room_id")] public string RoomId { get; set; } = string.Empty;
    [JsonPropertyName("host_name")] public string HostName { get; set; } = string.Empty;
    [JsonPropertyName("member_count")] public int MemberCount { get; set; }
    [JsonPropertyName("members_preview")] public List<string> MembersPreview { get; set; } = new List<string>();
    [JsonPropertyName("phase")] public string Phase { get; set; } = string.Empty;
    [JsonPropertyName("playback_state")] public string PlaybackState { get; set; } = string.Empty;
    [JsonPropertyName("position_seconds")] public double PositionSeconds { get; set; }
    [JsonPropertyName("selected_content_id")] public string? SelectedContentId { get; set; }
    [JsonPropertyName("content_title")] public string? ContentTitle { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("series_name")] public string? SeriesName { get; set; }
    [JsonPropertyName("season")] public int? Season { get; set; }
    [JsonPropertyName("episode")] public int? Episode { get; set; }
    [JsonPropertyName("has_pin")] public bool HasPin { get; set; }
    [JsonPropertyName("guest_control_policy")] public string GuestControlPolicy { get; set; } = string.Empty;
    [JsonPropertyName("selection_mode")] public string SelectionMode { get; set; } = string.Empty;
    [JsonPropertyName("is_member")] public bool IsMember { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
}

public sealed class AdminRoomDto
{
    [JsonPropertyName("room_id")] public string RoomId { get; set; } = string.Empty;
    [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
    [JsonPropertyName("host_name")] public string HostName { get; set; } = string.Empty;
    [JsonPropertyName("phase")] public string Phase { get; set; } = string.Empty;
    [JsonPropertyName("playback_state")] public string PlaybackState { get; set; } = string.Empty;
    [JsonPropertyName("position_seconds")] public double PositionSeconds { get; set; }
    [JsonPropertyName("content_title")] public string? ContentTitle { get; set; }
    [JsonPropertyName("content_label")] public string? ContentLabel { get; set; }
    [JsonPropertyName("has_pin")] public bool HasPin { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("members")] public List<AdminMemberDto> Members { get; set; } = new List<AdminMemberDto>();
}

public sealed class AdminMemberDto
{
    [JsonPropertyName("member_id")] public string MemberId { get; set; } = string.Empty;
    [JsonPropertyName("user_id")] public string UserId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("is_host")] public bool IsHost { get; set; }
    [JsonPropertyName("connected")] public bool Connected { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
}

public sealed class CommandDto
{
    [JsonPropertyName("command_id")] public string CommandId { get; set; } = string.Empty;
    [JsonPropertyName("generation")] public long Generation { get; set; }
    [JsonPropertyName("selection_revision")] public int SelectionRevision { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("position_seconds")] public double PositionSeconds { get; set; }
    [JsonPropertyName("execute_at")] public string ExecuteAt { get; set; } = string.Empty;
    [JsonPropertyName("playback_state")] public string PlaybackState { get; set; } = string.Empty;
    [JsonPropertyName("session_id")] public string? SessionId { get; set; }
    [JsonPropertyName("by_member_id")] public string? ByMemberId { get; set; }
    [JsonPropertyName("by_display_name")] public string? ByDisplayName { get; set; }
    [JsonPropertyName("countdown")] public bool Countdown { get; set; }
}

public sealed class ChatDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("member_id")] public string MemberId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("sent_at")] public string SentAt { get; set; } = string.Empty;
}

public sealed class SuggestionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("content_id")] public string ContentId { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = string.Empty;
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("voted_by_me")] public bool VotedByMe { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
}
