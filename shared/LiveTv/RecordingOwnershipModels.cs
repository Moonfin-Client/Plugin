using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moonfin.LiveTv;

/// <summary>
/// A series recording rule as the server holds it. Id is the Live TV API id clients see
/// (SeriesTimerInfoDto.Id) and matches ScheduledTimer.SeriesTimerId. SeriesId is the guide's
/// series id on Emby and always null on Jellyfin, whose public DTO doesn't carry it, so callers
/// must not rely on it.
/// </summary>
public sealed record SeriesRule(string Id, string? SeriesId, string? Name);

/// <summary>A server account, with only what the recording owner features need.</summary>
public sealed record ServerUser(Guid Id, string Name, string? PrimaryImageTag, bool IsDisabled);

/// <summary>
/// Everything the recording owners feature persists, kept in one file. Timers are keyed by the API
/// id clients see (ScheduledTimer.ApiId), series rules by SeriesRule.Id and recordings by their
/// library item id. Each value is the owning users' ids, first owner first.
/// </summary>
public sealed class OwnershipStoreData
{
    /// <summary>
    /// The file's format. A file with a higher version than this build writes is read but never
    /// overwritten (see <see cref="RecordingOwnershipService.CurrentVersion"/>).
    /// </summary>
    public int Version { get; set; } = 1;

    public Dictionary<string, List<Guid>> Timers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<Guid>> SeriesTimers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<Guid, List<Guid>> Items { get; set; } = new();

    /// <summary>The file each owned timer wrote, by timer API id, so the finished item can find its owners.</summary>
    public Dictionary<string, string> TimerPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Owners of recordings removed from the library, kept for a while in case they come back.</summary>
    public Dictionary<Guid, RemovedItem> Removed { get; set; } = new();

    /// <summary>
    /// The service's own id of each owned timer, by API id, so a cancel event carrying the service
    /// id still resolves after the timer has left the server's listing or the server restarted.
    /// </summary>
    public Dictionary<string, string> TimerServiceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each owned timer without a recorded file was first found missing from the server's listing.</summary>
    public Dictionary<string, DateTime> TimersMissingSince { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each owned series rule was first found missing from the server's listing.</summary>
    public Dictionary<string, DateTime> SeriesTimersMissingSince { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Members a newer plugin wrote that this one doesn't know, kept so a save doesn't drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The owners of a recording that left the library, and when it left.</summary>
public sealed class RemovedItem
{
    public List<Guid> Owners { get; set; } = new();

    public DateTime RemovedAt { get; set; }

    /// <summary>Members a newer plugin wrote that this one doesn't know, kept so a save doesn't drop them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>An owner as clients show it: display name and profile image tag.</summary>
public sealed record OwnerInfo(Guid UserId, string Name, string? ImageTag);

public enum OwnedKind
{
    Timer,
    Series,
    Item,
}

/// <summary>What recording or joining did: the timer or rule id, its owners now, and whether it already existed.</summary>
public sealed record OwnershipOutcome(string Id, IReadOnlyList<OwnerInfo> Owners, bool Joined);

/// <summary>
/// A series rule the host created: its API id, or null when it couldn't be created or found, with
/// the server's reason when it refused.
/// </summary>
public readonly record struct SeriesRuleCreation(string? RuleId, string? Error = null)
{
    public static SeriesRuleCreation Failed(string? error) => new(null, string.IsNullOrWhiteSpace(error) ? null : error);
}

/// <summary>
/// A record call's result: the outcome, or null with the server's reason for refusing when it
/// gave one, so the endpoint can pass it on (an Emby Premiere refusal, a tuner conflict).
/// </summary>
public sealed record OwnershipAttempt(OwnershipOutcome? Outcome, string? Error = null)
{
    /// <summary>The server's reason, or the fallback when it gave none.</summary>
    public string ErrorOr(string fallback) => string.IsNullOrWhiteSpace(Error) ? fallback : Error!;
}

/// <summary>
/// Owners per requested id. Ids with no owners the caller may see are left out. RecordingItemIds
/// are the requested items that are Live TV recordings the caller may see, owned or not, so a
/// client can offer to claim an unowned one; null when the server can't tell for some item.
/// </summary>
public sealed record OwnersLookup(
    IReadOnlyDictionary<string, IReadOnlyList<OwnerInfo>> Timers,
    IReadOnlyDictionary<string, IReadOnlyList<OwnerInfo>> SeriesTimers,
    IReadOnlyDictionary<Guid, IReadOnlyList<OwnerInfo>> Items,
    IReadOnlyList<Guid>? RecordingItemIds = null);

/// <summary>Everything a user owns.</summary>
public sealed record MineResult(IReadOnlyList<string> TimerIds, IReadOnlyList<string> SeriesTimerIds, IReadOnlyList<Guid> ItemIds);

/// <summary>
/// The owner calls the incomplete recordings feature makes. They take the owners lock synchronously,
/// so the owners service must never call back into that feature while holding it.
/// </summary>
public interface IRecordingOwners
{
    /// <summary>Everyone who owns any of the given recordings.</summary>
    IReadOnlyList<Guid> OwnersOfItems(IEnumerable<Guid> itemIds);

    /// <summary>Adds owners to a timer, by either of its ids.</summary>
    void AddTimerOwners(string timerId, IEnumerable<Guid> owners);
}
