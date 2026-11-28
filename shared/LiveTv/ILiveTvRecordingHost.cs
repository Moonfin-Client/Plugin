using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.LiveTv;

/// <summary>
/// The server calls the incomplete recordings feature makes, behind one small surface so its logic
/// can be tested without a running server.
/// </summary>
public interface ILiveTvRecordingHost
{
    /// <summary>A timer or series timer was created or cancelled.</summary>
    event EventHandler? ScheduleChanged;

    /// <summary>
    /// A timer was created. Only TimerId, ProgramId and (Emby) ApiId are set. TimerId is the id the
    /// server's event carries: on Jellyfin the service's id for the built-in DVR (DefaultLiveTvService)
    /// and the API id for services with new-timer-ids; on Emby the one id it has. Use GetTimersAsync
    /// for the full timer with both ids.
    /// </summary>
    event EventHandler<ScheduledTimer>? TimerCreated;

    /// <summary>
    /// The id from the server's timer cancellation event. Jellyfin: the service's id when the
    /// built-in DVR cancels a timer, the API id when the Live TV manager cancels one for another
    /// service, so match it against both ScheduledTimer.TimerId and ScheduledTimer.ApiId. Emby: the
    /// timer's id, which is both.
    /// </summary>
    event EventHandler<string>? TimerCancelled;

    /// <summary>The server's Refresh Guide task finished.</summary>
    event EventHandler? GuideRefreshed;

    event EventHandler<LibraryItem>? ItemAdded;

    event EventHandler<LibraryItem>? ItemUpdated;

    event EventHandler<Guid>? ItemRemoved;

    event EventHandler<Guid>? UserDeleted;

    /// <summary>
    /// All timers the server knows, including cancelled and completed ones, with both ids set:
    /// TimerId is the service's id and ApiId the id clients see (the same on Emby).
    /// </summary>
    Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken);

    /// <summary>Cancels a timer by the id clients see (ScheduledTimer.ApiId).</summary>
    Task CancelTimerAsync(string apiId, CancellationToken cancellationToken);

    /// <summary>The file a timer is writing right now, or null when it isn't recording.</summary>
    string? GetActiveRecordingPath(string timerId);

    ActiveRecordingTimer? GetActiveRecordingTimer(string path);

    /// <summary>Current size of a file, or null when it doesn't exist or can't be read.</summary>
    long? GetFileLength(string path);

    GuideProgram? GetProgram(string programId);

    /// <summary>Guide airings of a series starting after a time, on every channel.</summary>
    IReadOnlyList<GuideProgram> FindAirings(EpisodeKey episode, DateTime after);

    /// <summary>Guide programs with a name starting after a time, on every channel.</summary>
    IReadOnlyList<GuideProgram> FindProgramsByName(string name, DateTime after);

    /// <summary>All series rules the server knows, by the API id clients see.</summary>
    Task<IReadOnlyList<SeriesRule>> GetSeriesTimersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a series rule from the server's timer defaults for a program, with the client's
    /// settings applied by property name. Returns the new rule's API id (SeriesRule.Id), or a null
    /// id when it couldn't be created or found, with the server's reason when it refused.
    /// </summary>
    Task<SeriesRuleCreation> CreateSeriesTimerAsync(string programId, IReadOnlyDictionary<string, JsonElement>? overrides, CancellationToken cancellationToken);

    /// <summary>
    /// Schedules a one-off recording of a program with the server's timer defaults. The result
    /// carries the timer's service id and API id, or on failure the server's reason when it gave one.
    /// </summary>
    Task<ScheduleResult> ScheduleProgramAsync(string programId, CancellationToken cancellationToken);

    LibraryItem? FindItemByPath(string path);

    bool FileExists(string path);

    /// <summary>
    /// Files beside a recording with the same name and another extension: what a post-processing
    /// script leaves when it remuxes or transcodes in place.
    /// </summary>
    IReadOnlyList<string> FindRenamedSiblings(string path);

    /// <summary>Whether a user may see a library item or guide program, parental limits and channel access included.</summary>
    bool IsVisibleTo(Guid itemId, Guid userId);

    /// <summary>The ones among the given items a user may see, resolving the user once.</summary>
    ISet<Guid> VisibleTo(IReadOnlyCollection<Guid> itemIds, Guid userId);

    /// <summary>An item's tags as they are now, or null when it no longer exists.</summary>
    IReadOnlyList<string>? GetTags(Guid itemId);

    /// <summary>Adds or removes a tag. False when the item no longer exists.</summary>
    Task<bool> SetTagAsync(Guid itemId, string tag, bool present, CancellationToken cancellationToken);

    /// <summary>When an item's file was last written, or null when the item or its file is gone.</summary>
    DateTime? GetFileWrittenAt(Guid itemId);

    /// <summary>
    /// Deletes an episode and its file the way the server's own Delete does. False when the item is
    /// something other than an episode, which is left alone.
    /// </summary>
    bool DeleteItem(Guid itemId);

    /// <summary>A user, or null when there is no such user or the user can't be read right now.</summary>
    ServerUser? GetUser(Guid userId);

    /// <summary>
    /// Whether a user account exists: false only when the server definitely has no such user,
    /// null when it can't tell (the lookup failed or this server version can't be asked). Owners
    /// are only ever dropped on false, never on a failed read.
    /// </summary>
    bool? UserExists(Guid userId);

    /// <summary>
    /// Whether a library item is a Live TV recording: true when it is under the server's recording
    /// folders, false when it isn't (or doesn't exist), null when this server can't tell. The user
    /// is the one asking, for servers that can only list recordings as a user.
    /// </summary>
    bool? IsLiveTvRecording(Guid itemId, Guid userId);
}
