using System;
using System.Collections.Generic;
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

    event EventHandler<ScheduledTimer>? TimerCreated;

    /// <summary>The id from the server's timer cancellation event.</summary>
    event EventHandler<string>? TimerCancelled;

    /// <summary>The server's Refresh Guide task finished.</summary>
    event EventHandler? GuideRefreshed;

    event EventHandler<LibraryItem>? ItemAdded;

    event EventHandler<LibraryItem>? ItemUpdated;

    event EventHandler<Guid>? ItemRemoved;

    /// <summary>All timers the server knows, including cancelled and completed ones.</summary>
    Task<IReadOnlyList<ScheduledTimer>> GetTimersAsync(CancellationToken cancellationToken);

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

    /// <summary>Schedules a one-off recording of a program with the server's timer defaults.</summary>
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
}
