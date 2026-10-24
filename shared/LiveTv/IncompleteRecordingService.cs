using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.LiveTv;

/// <summary>
/// Flags Live TV recordings that came out incomplete and, when asked, schedules a later showing of
/// the same episode. Detection is schedule-based: it estimates how much of each file's padded
/// schedule was being written, so it can't vouch for the content itself, and it measures while the
/// recording runs so a post-processing script that moves, cuts or transcodes the file afterwards
/// can't affect the verdict. It deletes nothing unless the delete-incomplete-copies setting is on,
/// and then only tagged copies of an episode once a complete one has recorded.
///
/// All state lives in one JSON file and every read and write goes through one lock. Before asking
/// the server for a timer the chosen showing is saved, so a crash between the two is reconciled
/// against the server's timers rather than scheduling a second one.
/// </summary>
public sealed class IncompleteRecordingService : IDisposable
{
    /// <summary>The state file's name inside the plugin's data folder.</summary>
    public const string StoreFileName = "livetv-incomplete-recordings.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILiveTvRecordingHost _host;
    private readonly IRecordingLog _logger;
    private readonly IStateFile _stateFile;
    private readonly Func<DateTime> _utcNow;
    private readonly string _storePath;
    private readonly Func<IncompleteRecordingSettings> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Requests opened or reopened since the last run, searched without waiting for a guide refresh.
    private readonly HashSet<string> _searchNow = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<string> _cancelledTimers = new();
    private readonly ConcurrentQueue<string> _stoppedTimers = new();
    private readonly ConcurrentDictionary<string, string> _createdTimerPrograms = new(StringComparer.OrdinalIgnoreCase);

    // Nothing this process recorded predates its start.
    private readonly string _processId = Guid.NewGuid().ToString("N");
    private readonly DateTime _processStart;

    // When each timer in its window was last seen not recording, so a late start this process
    // watched counts as not recorded rather than unobserved.
    private readonly Dictionary<string, DateTime> _lastSeenIdle = new(StringComparer.Ordinal);

    // When this process saw each timer created. Nothing it records can be older, so a recording
    // started partway through a showing isn't credited with the part before.
    private readonly ConcurrentDictionary<string, DateTime> _timerCreatedAt = new(StringComparer.OrdinalIgnoreCase);

    private IncompleteRecordingStoreData? _data;
    private IReadOnlyList<ScheduledTimer> _timers = Array.Empty<ScheduledTimer>();
    private DateTime? _timersLoadedAt;

    // Whether the last load succeeded. A timer missing from a cache that failed to load proves nothing.
    private bool _timersCurrent;
    private volatile bool _timersStale = true;
    private bool _searchAll;

    // Written under the lock and read without it, so the watcher never enumerates the store.
    private long _nextRunTicks;

    // What library events can concern, rebuilt under the lock whenever the store changes. Null
    // until the store is loaded, when every event is handled.
    private volatile TrackedItems? _tracked;

    public IncompleteRecordingService(
        ILiveTvRecordingHost host,
        IRecordingLog logger,
        IStateFile stateFile,
        Func<DateTime> utcNow,
        string storePath,
        Func<IncompleteRecordingSettings> settings)
    {
        _host = host;
        _logger = logger;
        _stateFile = stateFile;
        _utcNow = utcNow;
        _storePath = storePath;
        _settings = settings;
        _processStart = Now;
    }

    private DateTime Now => _utcNow();

    public void Dispose() => _lock.Dispose();

    /// <summary>The schedule changed, so the cache reloads on the next run.</summary>
    public void MarkScheduleChanged() => _timersStale = true;

    public void MarkTimerCancelled(string timerId)
    {
        if (!string.IsNullOrEmpty(timerId))
        {
            _cancelledTimers.Enqueue(timerId);
            _stoppedTimers.Enqueue(timerId);
        }

        _timersStale = true;
    }

    public void MarkTimerCreated(ScheduledTimer timer)
    {
        if (string.IsNullOrEmpty(timer.TimerId))
        {
            _timersStale = true;
            return;
        }

        if (timer.ProgramId != null)
        {
            _createdTimerPrograms[timer.TimerId] = timer.ProgramId;
        }

        _timerCreatedAt.TryAdd(timer.TimerId, Now);
        _timersStale = true;
    }

    /// <summary>The guide was refreshed, so the cache reloads and every waiting request searches again.</summary>
    public void MarkGuideRefreshed()
    {
        _timersStale = true;
        _searchAll = true;
    }

    /// <summary>
    /// When the next run is due: every sample interval while a recording window is open, otherwise
    /// the next window's start, the safety-net check or the daily request search, whichever is first.
    /// Safe to call without the lock, since it reads only what the last pass worked out.
    /// </summary>
    public DateTime NextRunAt() =>
        _timersStale ? Now : new DateTime(Interlocked.Read(ref _nextRunTicks), DateTimeKind.Utc);

    /// <summary>Whether a newly added item could be a file an incomplete recording wrote.</summary>
    public bool MayConcernAddedItem(LibraryItem item) =>
        !string.IsNullOrEmpty(item.Path) && (_tracked?.MayHaveWritten(item.Path) ?? true);

    /// <summary>Whether an updated or removed item is attached to an incomplete recording.</summary>
    public bool MayConcernItem(Guid itemId) => _tracked?.ItemIds.Contains(itemId) ?? true;

    // Called under the lock at the end of anything that changes what the next run depends on.
    private void ScheduleNextRun(IncompleteRecordingStoreData data, DateTime now) =>
        Interlocked.Exchange(ref _nextRunTicks, ComputeNextRunAt(data, now).Ticks);

    private DateTime ComputeNextRunAt(IncompleteRecordingStoreData data, DateTime now)
    {
        if (data.Snapshots.Any(s => s.JudgedAt == null))
        {
            return now + IncompleteRecordingRules.SampleInterval;
        }

        var candidates = new List<DateTime>
        {
            (_timersLoadedAt ?? now) + IncompleteRecordingRules.SafetyNetInterval,
        };

        foreach (var timer in _timers.Where(IsLive))
        {
            if (timer.WindowStart <= now && now < timer.WindowEnd)
            {
                return now + IncompleteRecordingRules.SampleInterval;
            }

            if (timer.WindowStart > now)
            {
                candidates.Add(timer.WindowStart);
            }
        }

        if (data.Requests.Any(r => CompleteShowingState.IsOpen(r.State)))
        {
            candidates.Add((data.LastRequestSearchAt ?? now) + IncompleteRecordingRules.RequestSearchFallback);
        }

        return candidates.Min();
    }

    /// <summary>One pass: reload the schedule if needed, sample open windows, judge closed ones and move requests along.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settings().Enabled)
            {
                // Checked again at the safety-net interval, so turning detection back on is picked up.
                _timersStale = false;
                Interlocked.Exchange(ref _nextRunTicks, (Now + IncompleteRecordingRules.SafetyNetInterval).Ticks);
                return;
            }

            var data = Load();
            var now = Now;
            var previousTimers = _timers;

            if (_timersStale || _timersLoadedAt == null || now - _timersLoadedAt >= IncompleteRecordingRules.SafetyNetInterval)
            {
                await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var stale in _lastSeenIdle.Where(e => now - e.Value > TimeSpan.FromDays(1)).Select(e => e.Key).ToList())
            {
                _lastSeenIdle.Remove(stale);
            }

            foreach (var stale in _timerCreatedAt.Where(e => now - e.Value > TimeSpan.FromDays(1)).Select(e => e.Key).ToList())
            {
                _timerCreatedAt.TryRemove(stale, out _);
            }

            var changed = StartSnapshots(data, now);
            changed |= Sample(data, now);
            changed |= TrackScheduledRequests(data, now, previousTimers);
            changed |= await RetryUntagsAsync(data, cancellationToken).ConfigureAwait(false);
            changed |= MarkStoppedRecordings(data);
            changed |= await JudgeClosedAsync(data, now, cancellationToken).ConfigureAwait(false);
            changed |= ApplySettings(data);

            // Without a current list of timers a search can't tell which showings already have
            // one, so it waits for a load that succeeds.
            var searchAll = _searchAll
                || data.LastRequestSearchAt == null
                || now - data.LastRequestSearchAt >= IncompleteRecordingRules.RequestSearchFallback;
            changed |= await SearchAsync(data, now, searchAll, cancellationToken).ConfigureAwait(false);
            if (searchAll && _timersCurrent)
            {
                _searchAll = false;
                data.LastRequestSearchAt = now;
                changed = true;
            }

            changed |= Prune(data, now);

            if (changed)
            {
                Save(data);
            }

            ScheduleNextRun(data, Now);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Attaches a newly added library item to the incomplete recording that wrote its file.</summary>
    public async Task OnItemAddedAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        if (!_settings().Enabled)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (await TryAttachAsync(data, item, Now, cancellationToken).ConfigureAwait(false))
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A tag gone from an item, for any reason, dismisses its record. The tag is never put back.
    /// The event carries the tags from when it was raised, which can predate the tag this service
    /// added since, so the item's current tags decide.
    /// </summary>
    public async Task OnItemUpdatedAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        if (HasIncompleteTag(item.Tags))
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var records = data.Records.Where(r => !r.Dismissed && r.TaggedItemIds.Contains(item.Id)).ToList();
            if (records.Count == 0 || _host.GetTags(item.Id) is not { } tags || HasIncompleteTag(tags))
            {
                return;
            }

            var now = Now;
            var changed = false;
            foreach (var record in records)
            {
                record.Dismissed = true;
                record.DismissedAt = now;
                changed = true;
            }

            if (changed)
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A removed item detaches from its record. A move by a post-processing script and a deletion
    /// by the user look the same here, so both get the same outcome: no badge, and the record waits
    /// out the attach window for the file to reappear renamed, then is dropped. An open request
    /// stays either way.
    /// </summary>
    public async Task OnItemRemovedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var now = Now;
            var changed = false;
            foreach (var record in data.Records.Where(r => r.ItemIds.Contains(itemId)).ToList())
            {
                foreach (var fragment in record.Fragments)
                {
                    fragment.ItemIds.Remove(itemId);
                }

                record.TaggedItemIds.Remove(itemId);
                if (!record.ItemIds.Any() && now - record.JudgedAt >= IncompleteRecordingRules.AttachWindow)
                {
                    data.Records.Remove(record);
                }

                changed = true;
            }

            if (changed)
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The display state of each incomplete item among the given ones that the user can see, or of
    /// every incomplete item the user can see when none are given. Complete items are left out.
    /// </summary>
    public async Task<IReadOnlyList<IncompleteRecordingStateDto>> GetStatesAsync(IEnumerable<Guid>? itemIds, Guid userId, CancellationToken cancellationToken)
    {
        var states = new Dictionary<Guid, IncompleteRecordingStateDto>();
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var ids = itemIds?.ToList() is { Count: > 0 } given
                ? given
                : data.Records.Where(r => !r.Dismissed).SelectMany(r => r.ItemIds).ToList();
            foreach (var itemId in ids.Distinct())
            {
                var record = data.Records.FirstOrDefault(r => !r.Dismissed && r.ItemIds.Contains(itemId));
                if (record == null)
                {
                    continue;
                }

                var request = record.RequestId == null ? null : data.Requests.FirstOrDefault(r => r.Id == record.RequestId);
                var state = IncompleteRecordingRules.DisplayState(request);
                states[itemId] = new IncompleteRecordingStateDto
                {
                    ItemId = itemId.ToString("N"),
                    RecordedMinutes = Math.Round(record.RecordedMinutes),
                    ExpectedMinutes = Math.Round(record.ExpectedMinutes),
                    State = state,
                    ScheduledStart = state == "scheduled" ? request!.ScheduledStart : null,
                    ChannelName = state == "scheduled" ? request!.ScheduledChannelName : null,
                    RecordedAt = state == "recorded" ? request!.RecordedAt : null,
                };
            }
        }
        finally
        {
            _lock.Release();
        }

        // Visibility asks the server about each item, so it is checked after the lock is released.
        var visible = _host.VisibleTo(states.Keys, userId);
        return states.Where(e => visible.Contains(e.Key)).Select(e => e.Value).ToList();
    }

    /// <summary>
    /// Keep as Complete: dismisses the item's record for everyone and removes the tag. An automatic
    /// request opened for it closes too, since the user is satisfied with this copy. An explicit
    /// request someone asked for stays. A timer already scheduled is left alone.
    /// </summary>
    public async Task<bool> DismissAsync(Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var record = data.Records.FirstOrDefault(r => r.ItemIds.Contains(itemId));
            if (record == null || !_host.IsVisibleTo(itemId, userId))
            {
                return false;
            }

            var now = Now;
            record.Dismissed = true;
            record.DismissedAt = now;

            var request = record.RequestId == null ? null : data.Requests.FirstOrDefault(r => r.Id == record.RequestId);
            if (request != null && !request.Explicit && CompleteShowingState.IsOpen(request.State))
            {
                request.State = CompleteShowingState.Dismissed;
                request.PendingProgramId = null;
                request.ClosedAt = now;
            }

            Save(data);

            foreach (var id in record.TaggedItemIds)
            {
                await SetTagSafeAsync(id, false, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Opens an explicit request for a program's episode, or joins the open one, and searches for a
    /// showing at once. Calling it again for the same episode returns the same request.
    /// </summary>
    public async Task<(CompleteShowingRequest? Request, CompleteShowingRefusal Refusal)> RequestCompleteShowingAsync(string programId, Guid userId, CancellationToken cancellationToken)
    {
        // Not a guide program, one a guide refresh just removed, or one the user can't see.
        var program = _host.GetProgram(programId);
        if (program == null || !_host.IsVisibleTo(program.ItemId, userId))
        {
            return (null, CompleteShowingRefusal.NotFound);
        }

        var key = program.Key;
        if (key == null)
        {
            return (null, CompleteShowingRefusal.NoEpisodeNumbers);
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = Load();
            var now = Now;
            var request = FindOpenRequest(data, key);
            if (request == null)
            {
                request = new CompleteShowingRequest { Episode = key, OpenedAt = now };
                data.Requests.Add(request);
                _searchNow.Add(request.Id);
            }

            request.Explicit = true;
            if (_timersStale || _timersLoadedAt == null)
            {
                await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
            }

            await SearchAsync(data, now, false, cancellationToken).ConfigureAwait(false);
            Save(data);
            ScheduleNextRun(data, Now);
            return (request, CompleteShowingRefusal.None);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsLive(ScheduledTimer timer) =>
        timer.Status is "New" or "InProgress";

    private static bool HasIncompleteTag(IEnumerable<string> tags) =>
        tags.Contains(IncompleteRecordingRules.Tag, StringComparer.OrdinalIgnoreCase);

    // A failed load keeps the previous list for sampling, but marks it not current so nothing
    // treats a timer missing from it as gone. It is tried again at the safety-net interval.
    private async Task ReloadTimersAsync(CancellationToken cancellationToken)
    {
        _timersStale = false;
        try
        {
            _timers = await _host.GetTimersAsync(cancellationToken).ConfigureAwait(false);
            _timersCurrent = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not load Live TV timers");
            _timersCurrent = false;
        }

        _timersLoadedAt = Now;
    }

    // A snapshot is taken the first time a timer in its window is seen recording.
    private bool StartSnapshots(IncompleteRecordingStoreData data, DateTime now)
    {
        var changed = false;
        foreach (var timer in _timers.Where(IsLive))
        {
            if (timer.WindowStart > now || now >= timer.WindowEnd + IncompleteRecordingRules.OverrunLimit
                || data.Snapshots.Any(s => s.TimerId == timer.TimerId))
            {
                continue;
            }

            var path = _host.GetActiveRecordingPath(timer.TimerId);
            if (path == null)
            {
                _lastSeenIdle[timer.TimerId] = now;
                continue;
            }

            var details = _host.GetActiveRecordingTimer(path);
            if (details == null)
            {
                continue;
            }

            data.Snapshots.Add(CreateSnapshot(timer, details));
            changed = true;
        }

        return changed;
    }

    private RecordingSnapshot CreateSnapshot(ScheduledTimer timer, ActiveRecordingTimer details)
    {
        var snapshot = new RecordingSnapshot
        {
            TimerId = timer.TimerId,
            ProgramId = timer.ProgramId,
            ApiId = timer.ApiId,
            ChannelName = timer.ChannelName,
            WindowEnd = details.EndDate.AddSeconds(details.PostPaddingSeconds),
            Episode = EpisodeKey.Create(details.SeriesId, details.Name, details.SeasonNumber, details.EpisodeNumber, details.EpisodeTitle),
        };

        if (details.RetryCount == 0)
        {
            snapshot.RuntimeMinutes = (details.EndDate - details.StartDate).TotalMinutes;
            snapshot.PrePaddingMinutes = details.PrePaddingSeconds / 60.0;
            snapshot.PostPaddingMinutes = details.PostPaddingSeconds / 60.0;
            snapshot.WindowStart = details.StartDate.AddSeconds(-details.PrePaddingSeconds);
        }
        else
        {
            // A retry has rewritten the timer's start and dropped its pre-padding, so the runtime
            // comes from the guide and the padding isn't known.
            var program = timer.ProgramId == null ? null : _host.GetProgram(timer.ProgramId);
            var start = program?.StartDate ?? details.StartDate;
            var end = program?.EndDate ?? details.EndDate;
            snapshot.RuntimeMinutes = Math.Max(0, (end - start).TotalMinutes);
            snapshot.WindowStart = start;
        }

        return snapshot;
    }

    // Estimates each file's recording coverage. The interval since the last sample is credited
    // only when it is the same file, in the same process, the file grew, and the gap is short
    // enough to vouch for. A new file or a restart starts a new baseline. Time that can't be
    // observed is counted as unknown, never as recorded.
    private bool Sample(IncompleteRecordingStoreData data, DateTime now)
    {
        var changed = false;
        var minimumGap = IncompleteRecordingRules.SampleInterval * 0.9;
        foreach (var snapshot in data.Snapshots.Where(s => s.JudgedAt == null))
        {
            var path = _host.GetActiveRecordingPath(snapshot.TimerId);
            var open = snapshot.Fragments.LastOrDefault(f => !f.Ended);

            if (open != null && path == open.Path && open.ProcessId == _processId && now - open.LastSampleAt < minimumGap)
            {
                continue;
            }

            if (open != null && (path != open.Path || open.ProcessId != _processId))
            {
                CloseFragment(open, now, snapshot.WindowEnd);
                open = null;
                changed = true;
            }

            if (path == null)
            {
                _lastSeenIdle[snapshot.TimerId] = now;
                continue;
            }

            // A size that can't be read says nothing about whether the file grew.
            var size = _host.GetFileLength(path);
            if (size == null)
            {
                if (open != null)
                {
                    open.UnknownSeconds += (now - open.LastSampleAt).TotalSeconds;
                    open.LastSampleAt = now;
                    changed = true;
                }

                continue;
            }

            if (open == null)
            {
                // The file began after the window opened, after this process started, after the
                // previous file's last sample, and after the last time the timer was seen idle.
                var earliest = Max(snapshot.WindowStart, _processStart);
                if (snapshot.Fragments.Count > 0)
                {
                    earliest = Max(earliest, snapshot.Fragments.Max(f => f.LastSampleAt));
                }

                if (_lastSeenIdle.TryGetValue(snapshot.TimerId, out var idle))
                {
                    earliest = Max(earliest, idle);
                }

                if (_timerCreatedAt.TryGetValue(snapshot.TimerId, out var created))
                {
                    earliest = Max(earliest, created);
                }

                snapshot.Fragments.Add(new RecordingFragment
                {
                    Path = path,
                    ProcessId = _processId,
                    FirstSampleAt = now,
                    LastSampleAt = now,
                    LastSize = size.Value,
                    UnknownSeconds = Math.Max(0, (now - earliest).TotalSeconds),
                });
            }
            else
            {
                var elapsed = (now - open.LastSampleAt).TotalSeconds;
                if (size > open.LastSize)
                {
                    if (elapsed <= IncompleteRecordingRules.MaxCreditedGap.TotalSeconds)
                    {
                        open.CoveredSeconds += elapsed;
                    }
                    else
                    {
                        open.UnknownSeconds += elapsed;
                    }
                }

                open.LastSampleAt = now;
                open.LastSize = size.Value;
            }

            changed = true;
        }

        return changed;
    }

    // The file may have kept growing until the window ended while samples were missed.
    private static void CloseFragment(RecordingFragment fragment, DateTime now, DateTime windowEnd)
    {
        var end = now < windowEnd ? now : windowEnd;
        var tail = Math.Max(0, (end - fragment.LastSampleAt).TotalSeconds);
        fragment.UnknownSeconds += tail;
        fragment.Ended = true;
    }

    private async Task<bool> JudgeClosedAsync(IncompleteRecordingStoreData data, DateTime now, CancellationToken cancellationToken)
    {
        var changed = false;
        var threshold = _settings().ThresholdPercent;
        foreach (var snapshot in data.Snapshots.Where(s => s.JudgedAt == null).ToList())
        {
            if (now < snapshot.WindowEnd)
            {
                continue;
            }

            if (now < snapshot.WindowEnd + IncompleteRecordingRules.OverrunLimit
                && _host.GetActiveRecordingPath(snapshot.TimerId) != null)
            {
                continue;
            }

            foreach (var fragment in snapshot.Fragments.Where(f => !f.Ended))
            {
                CloseFragment(fragment, now, snapshot.WindowEnd);
            }

            _lastSeenIdle.Remove(snapshot.TimerId);
            _timerCreatedAt.TryRemove(snapshot.TimerId, out _);
            snapshot.JudgedAt = now;
            snapshot.Verdict = IncompleteRecordingRules.Classify(snapshot.Fragments, snapshot.ExpectedMinutes, threshold);
            if (snapshot.StoppedByUser && snapshot.Verdict == RecordingVerdict.Incomplete)
            {
                snapshot.Verdict = RecordingVerdict.Unknown;
            }

            changed = true;

            _logger.Info(
                "Live TV recording {TimerId}: best file covered an estimated {Covered:0.#} of {Expected:0.#} scheduled minutes in {Files} file(s), {Verdict}",
                snapshot.TimerId,
                snapshot.BestCoveredMinutes,
                snapshot.ExpectedMinutes,
                snapshot.Fragments.Count,
                snapshot.Verdict);

            switch (snapshot.Verdict)
            {
                case RecordingVerdict.Complete:
                    OnCompleteRecording(data, snapshot, now);
                    await ReleaseOverwrittenCopiesAsync(data, snapshot, cancellationToken).ConfigureAwait(false);
                    await DeleteIncompleteCopiesAsync(data, snapshot, now, cancellationToken).ConfigureAwait(false);
                    break;
                case RecordingVerdict.Incomplete:
                    await OnIncompleteRecordingAsync(data, snapshot, now, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ReleaseOverwrittenCopiesAsync(data, snapshot, cancellationToken).ConfigureAwait(false);
                    OnUnknownRecording(data, snapshot, now);
                    break;
            }
        }

        return changed;
    }

    private static void OnCompleteRecording(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now)
    {
        if (snapshot.Episode == null)
        {
            return;
        }

        var request = FindOpenRequest(data, snapshot.Episode);
        if (request != null)
        {
            request.State = CompleteShowingState.Done;
            request.PendingProgramId = null;
            request.RecordedAt = now;
            request.ClosedAt = now;
        }
    }

    /// <summary>
    /// Jellyfin derives an item's id from its path, so a newer recording written where an
    /// incomplete one was, or remuxed beside it by a post-processing script, is the same item as
    /// the old copy. It belongs to the newer recording now, so it leaves the old record and loses
    /// the tag before anything is deleted. A removal that fails is retried on later runs. Having
    /// left the record, the item can't be deleted meanwhile.
    /// </summary>
    private async Task ReleaseOverwrittenCopiesAsync(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, CancellationToken cancellationToken)
    {
        var paths = snapshot.Fragments
            .Select(f => f.Path)
            .SelectMany(path => _host.FindRenamedSiblings(path).Prepend(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var item = _host.FindItemByPath(path);
            if (item == null)
            {
                continue;
            }

            foreach (var record in data.Records.Where(r => r.ItemIds.Contains(item.Id)))
            {
                foreach (var fragment in record.Fragments)
                {
                    fragment.ItemIds.Remove(item.Id);
                }

                if (record.TaggedItemIds.Remove(item.Id)
                    && !await SetTagSafeAsync(item.Id, false, cancellationToken).ConfigureAwait(false)
                    && !data.PendingUntags.Contains(item.Id))
                {
                    data.PendingUntags.Add(item.Id);
                }
            }
        }
    }

    // An item tagged again by a newer incomplete record keeps its tag, and one that's gone has
    // nothing left to remove.
    private async Task<bool> RetryUntagsAsync(IncompleteRecordingStoreData data, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var itemId in data.PendingUntags.ToList())
        {
            if (data.Records.Any(r => r.TaggedItemIds.Contains(itemId))
                || _host.GetTags(itemId) == null
                || await SetTagSafeAsync(itemId, false, cancellationToken).ConfigureAwait(false))
            {
                data.PendingUntags.Remove(itemId);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// With the setting on, a complete recording deletes the earlier copies of its episode that
    /// carry the incomplete tag, through the server's own delete. Copies kept as complete are dismissed
    /// and untagged, so they stay.
    /// </summary>
    private async Task DeleteIncompleteCopiesAsync(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now, CancellationToken cancellationToken)
    {
        if (!_settings().DeleteIncompleteCopies || snapshot.Episode == null)
        {
            return;
        }

        foreach (var record in data.Records.Where(r => !r.Dismissed && r.Episode != null
            && r.Episode.Matches(snapshot.Episode) && r.JudgedAt < snapshot.WindowStart))
        {
            var kept = 0;
            foreach (var itemId in record.TaggedItemIds.ToList())
            {
                // The stored id is checked again right before deleting: the item has to still carry
                // the tag and its file has to predate the complete recording, so a copy the user
                // kept or a newer file at a reused path is never touched.
                var tags = _host.GetTags(itemId);
                if (tags == null || !HasIncompleteTag(tags) || data.PendingUntags.Contains(itemId))
                {
                    continue;
                }

                var writtenAt = _host.GetFileWrittenAt(itemId);
                if (writtenAt == null || writtenAt >= snapshot.WindowStart)
                {
                    kept++;
                    _logger.Info("Kept {ItemId}: its file changed after the complete copy of {Series} started recording", itemId, snapshot.Episode.SeriesName);
                    continue;
                }

                try
                {
                    if (_host.DeleteItem(itemId))
                    {
                        _logger.Info("Deleted incomplete recording {ItemId} of {Series}, a complete copy recorded", itemId, snapshot.Episode.SeriesName);
                    }
                    else
                    {
                        kept++;
                        _logger.Info("Kept {ItemId}: only episodes are deleted", itemId);
                    }
                }
                catch (Exception ex)
                {
                    kept++;
                    _logger.Warn(ex, "Could not delete incomplete recording {ItemId}", itemId);
                }
            }

            if (kept == 0)
            {
                record.Dismissed = true;
                record.DismissedAt = now;
            }
        }
    }

    // A timer cancelled while its recording ran was stopped on purpose. Jellyfin only cancels a
    // running timer when someone does, so the short file is what they asked for.
    private bool MarkStoppedRecordings(IncompleteRecordingStoreData data)
    {
        var changed = false;
        while (_stoppedTimers.TryDequeue(out var id))
        {
            foreach (var snapshot in data.Snapshots.Where(s => s.JudgedAt == null
                && (string.Equals(s.TimerId, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.ApiId, id, StringComparison.OrdinalIgnoreCase))))
            {
                snapshot.StoppedByUser = true;
                changed = true;
            }
        }

        return changed;
    }

    // Too little was observed to judge. If this was the scheduled showing, the request ends
    // without a verdict rather than retrying on a guess.
    private static void OnUnknownRecording(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now)
    {
        var request = data.Requests.FirstOrDefault(r => r.State == CompleteShowingState.Scheduled
            && string.Equals(r.ScheduledProgramId, snapshot.ProgramId, StringComparison.OrdinalIgnoreCase));
        if (request != null)
        {
            request.State = CompleteShowingState.Unverified;
            request.ClosedAt = now;
        }
    }

    private async Task OnIncompleteRecordingAsync(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now, CancellationToken cancellationToken)
    {
        var record = new IncompleteRecord
        {
            TimerId = snapshot.TimerId,
            RecordedMinutes = snapshot.BestCoveredMinutes,
            ExpectedMinutes = snapshot.ExpectedMinutes,
            Episode = snapshot.Episode,
            Fragments = snapshot.Fragments
                .Select(f => new RecordingFragment
                {
                    Path = f.Path,
                    FirstSampleAt = f.FirstSampleAt,
                    LastSampleAt = f.LastSampleAt,
                    CoveredSeconds = f.CoveredSeconds,
                    UnknownSeconds = f.UnknownSeconds,
                    Ended = true,
                })
                .ToList(),
            JudgedAt = now,
        };
        data.Records.Add(record);

        // Before looking the items up, so one added after the lookup isn't filtered out.
        RefreshTracked(data);

        if (snapshot.Episode != null)
        {
            record.RequestId = LinkRequest(data, snapshot, now)?.Id;
        }

        // The server queues a library refresh when a recording starts and again when it stops,
        // so the items may already be there. Reconcile with them now, and later arrivals come
        // through ItemAdded.
        foreach (var fragment in record.Fragments)
        {
            var item = _host.FindItemByPath(fragment.Path) ?? FindRenamedItem(data, fragment);
            if (item != null)
            {
                await AttachAsync(record, fragment, item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private CompleteShowingRequest? LinkRequest(IncompleteRecordingStoreData data, RecordingSnapshot snapshot, DateTime now)
    {
        var episode = snapshot.Episode!;
        var scheduled = data.Requests.FirstOrDefault(r => r.State == CompleteShowingState.Scheduled
            && string.Equals(r.ScheduledProgramId, snapshot.ProgramId, StringComparison.OrdinalIgnoreCase));
        if (scheduled != null)
        {
            RecordFailedAttempt(scheduled, now);
            return scheduled;
        }

        var open = FindOpenRequest(data, episode);
        if (open != null)
        {
            return open;
        }

        // A request that gave up, that the user cancelled or that already got a complete copy isn't
        // reopened by a later recording.
        var ended = data.Requests
            .Where(r => r.State is CompleteShowingState.GaveUp or CompleteShowingState.CancelledByUser or CompleteShowingState.Done)
            .LastOrDefault(r => r.Episode.Matches(episode));
        if (ended != null)
        {
            return ended;
        }

        if (!_settings().AutoRecordComplete)
        {
            return null;
        }

        var request = new CompleteShowingRequest { Episode = episode, OpenedAt = now };
        data.Requests.Add(request);
        _searchNow.Add(request.Id);
        return request;
    }

    private void RecordFailedAttempt(CompleteShowingRequest request, DateTime now)
    {
        request.FailedAttempts++;
        ClearScheduled(request);
        if (request.FailedAttempts >= IncompleteRecordingRules.MaxFailedAttempts)
        {
            request.State = CompleteShowingState.GaveUp;
            request.ClosedAt = now;
        }
        else
        {
            request.State = CompleteShowingState.Waiting;
            _searchNow.Add(request.Id);
        }
    }

    private static void ClearScheduled(CompleteShowingRequest request)
    {
        request.ScheduledProgramId = null;
        request.ScheduledStart = null;
        request.ScheduledEnd = null;
        request.ScheduledChannelName = null;
    }

    private static void SetScheduled(CompleteShowingRequest request, GuideProgram showing)
    {
        request.State = CompleteShowingState.Scheduled;
        request.PendingProgramId = null;
        request.ScheduledProgramId = showing.Id;
        request.ScheduledStart = showing.StartDate;
        request.ScheduledEnd = showing.EndDate;
        request.ScheduledChannelName = showing.ChannelName;
    }

    private ScheduledTimer? LiveTimerFor(string programId) =>
        _timers.FirstOrDefault(t => IsLive(t) && string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase));

    // Follows each scheduled showing's timer. Cancelled is final, except when the series rule
    // visibly moved the recording to another airing of the episode, which the request then
    // follows. Gone before it aired means a guide change removed it, so the request searches
    // again. Still nothing long after it ended means it never recorded, which uses an attempt.
    private bool TrackScheduledRequests(IncompleteRecordingStoreData data, DateTime now, IReadOnlyList<ScheduledTimer> previousTimers)
    {
        if (!_timersCurrent)
        {
            return false;
        }

        var cancelledPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_cancelledTimers.TryDequeue(out var cancelledId))
        {
            if (_createdTimerPrograms.TryRemove(cancelledId, out var programId))
            {
                cancelledPrograms.Add(programId);
            }

            foreach (var cancelled in previousTimers.Concat(_timers).Where(t =>
                string.Equals(t.TimerId, cancelledId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.ApiId, cancelledId, StringComparison.OrdinalIgnoreCase)))
            {
                if (cancelled.ProgramId != null)
                {
                    cancelledPrograms.Add(cancelled.ProgramId);
                }
            }
        }

        // Creation can be followed by cancellation before a timer ever enters the cache.
        foreach (var created in _createdTimerPrograms)
        {
            if (_timers.Any(t => t.TimerId == created.Key || t.ApiId == created.Key)
                || !data.Requests.Any(r => CompleteShowingState.IsOpen(r.State)
                    && (r.ScheduledProgramId == created.Value || r.PendingProgramId == created.Value)))
            {
                _createdTimerPrograms.TryRemove(created.Key, out _);
            }
        }

        var changed = false;
        foreach (var request in data.Requests.Where(r => r.State == CompleteShowingState.Scheduled).ToList())
        {
            var timer = _timers.FirstOrDefault(t =>
                string.Equals(t.ProgramId, request.ScheduledProgramId, StringComparison.OrdinalIgnoreCase));

            if (timer?.Status == "Cancelled"
                || (request.ScheduledProgramId != null && cancelledPrograms.Contains(request.ScheduledProgramId)))
            {
                var moved = FindAiringsSafe(request.Episode, now)
                    .Where(p => !string.Equals(p.Id, request.ScheduledProgramId, StringComparison.OrdinalIgnoreCase)
                        && p.Key is { } key && request.Episode.Matches(key) && LiveTimerFor(p.Id) != null)
                    .OrderBy(p => p.StartDate)
                    .FirstOrDefault();
                if (moved != null)
                {
                    SetScheduled(request, moved);
                }
                else
                {
                    request.State = CompleteShowingState.CancelledByUser;
                    request.ClosedAt = now;
                }

                changed = true;
            }
            else if (timer == null && request.ScheduledStart > now)
            {
                ClearScheduled(request);
                request.State = CompleteShowingState.Waiting;
                _searchNow.Add(request.Id);
                changed = true;
            }
            else if (request.ScheduledEnd + IncompleteRecordingRules.OverrunLimit + TimeSpan.FromHours(1) < now
                && !data.Snapshots.Any(s => s.JudgedAt == null
                    && string.Equals(s.ProgramId, request.ScheduledProgramId, StringComparison.OrdinalIgnoreCase)))
            {
                RecordFailedAttempt(request, now);
                changed = true;
            }
        }

        return changed;
    }

    // Turning the automatic setting off drops the automatic requests still waiting. Explicit ones,
    // including any an explicit request folded into, stay.
    private bool ApplySettings(IncompleteRecordingStoreData data)
    {
        if (_settings().AutoRecordComplete)
        {
            return false;
        }

        var dropped = data.Requests
            .Where(r => r.State == CompleteShowingState.Waiting && !r.Explicit && r.PendingProgramId == null)
            .Select(r => r.Id)
            .ToHashSet();
        if (dropped.Count == 0)
        {
            return false;
        }

        data.Requests.RemoveAll(r => dropped.Contains(r.Id));
        foreach (var record in data.Records.Where(r => r.RequestId != null && dropped.Contains(r.RequestId)))
        {
            record.RequestId = null;
        }

        return true;
    }

    private IReadOnlyList<GuideProgram> FindAiringsSafe(EpisodeKey episode, DateTime now)
    {
        try
        {
            return _host.FindAirings(episode, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not search the guide for {Series} S{Season}E{Episode}", episode.SeriesName, episode.Season, episode.Episode);
            return Array.Empty<GuideProgram>();
        }
    }

    private async Task<bool> SearchAsync(IncompleteRecordingStoreData data, DateTime now, bool searchAll, CancellationToken cancellationToken)
    {
        if (!_timersCurrent)
        {
            return false;
        }

        var changed = false;
        var askedServer = false;
        foreach (var request in data.Requests.Where(r => r.State == CompleteShowingState.Waiting).ToList())
        {
            if (!searchAll && !_searchNow.Contains(request.Id) && request.PendingProgramId == null)
            {
                continue;
            }

            _searchNow.Remove(request.Id);

            // A showing chosen before a crash or a failed call: if the server made its timer,
            // adopt it, otherwise ask again for the same showing while it is still ahead.
            GuideProgram? showing = null;
            if (request.PendingProgramId != null)
            {
                var pending = _host.GetProgram(request.PendingProgramId);
                if (pending != null && (LiveTimerFor(pending.Id) != null || pending.StartDate > now))
                {
                    showing = pending;
                }
                else
                {
                    request.PendingProgramId = null;
                    changed = true;
                }
            }

            if (showing == null && now - request.OpenedAt >= IncompleteRecordingRules.RequestLifetime)
            {
                request.State = CompleteShowingState.NotFound;
                request.ClosedAt = now;
                changed = true;
                continue;
            }

            if (showing == null)
            {
                // An airing a series rule already records is followed rather than doubled, and one
                // the user cancelled is never picked, since scheduling it would quietly undo that.
                var airings = FindAiringsSafe(request.Episode, now)
                    .Where(p => !_timers.Any(t => t.Status == "Cancelled" && string.Equals(t.ProgramId, p.Id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                showing = IncompleteRecordingRules.ChooseShowing(request.Episode, airings.Where(p => LiveTimerFor(p.Id) != null), now)
                    ?? IncompleteRecordingRules.ChooseShowing(request.Episode, airings, now);
            }

            if (showing == null)
            {
                continue;
            }

            // A showing that already has a timer, whoever made it, is adopted and followed like
            // one the plugin scheduled, and uses an attempt the same way.
            if (LiveTimerFor(showing.Id) == null)
            {
                request.PendingProgramId = showing.Id;
                Save(data);

                var result = await _host.ScheduleProgramAsync(showing.Id, cancellationToken).ConfigureAwait(false);
                askedServer = true;
                if (result == ScheduleResult.Failed)
                {
                    changed = true;
                    continue;
                }
            }

            SetScheduled(request, showing);
            changed = true;

            _logger.Info(
                "Following a later showing of {Series} S{Season}E{Episode} at {Start} on {Channel}",
                request.Episode.SeriesName,
                request.Episode.Season,
                request.Episode.Episode,
                showing.StartDate,
                showing.ChannelName);
        }

        // Flipping a cancelled showing back to New raises no timer event, and a showing the server
        // says is already scheduled may be missing from the cache, so reload here.
        if (askedServer)
        {
            await ReloadTimersAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private static bool Prune(IncompleteRecordingStoreData data, DateTime now)
    {
        var removed = data.Snapshots.RemoveAll(s =>
            s.JudgedAt != null && now - s.JudgedAt >= IncompleteRecordingRules.SnapshotRetention);

        removed += data.Records.RemoveAll(r =>
            (!r.ItemIds.Any() && now - r.JudgedAt >= IncompleteRecordingRules.AttachWindow)
            || (r.Dismissed && now - (r.DismissedAt ?? r.JudgedAt) >= IncompleteRecordingRules.DismissedRecordRetention
                && now - r.JudgedAt >= IncompleteRecordingRules.AttachWindow)
            || now - r.JudgedAt >= IncompleteRecordingRules.RecordRetention);

        var referenced = data.Records.Where(r => r.RequestId != null).Select(r => r.RequestId!).ToHashSet();
        removed += data.Requests.RemoveAll(r =>
            !CompleteShowingState.IsOpen(r.State)
            && r.ClosedAt != null
            && now - r.ClosedAt >= IncompleteRecordingRules.ClosedRequestRetention
            && !referenced.Contains(r.Id));

        return removed > 0;
    }

    // Provenance only: the item's path is one the recording wrote, or the same file renamed in
    // place after the original went away. A copy elsewhere with the same episode numbers could be
    // a separate, complete recording, so it is never attached. Jellyfin reuses a recording's file
    // name once the old file is gone, so a path is only trusted while no later recording has
    // written to it, and after the attach window only for a file that predates the verdict.
    private async Task<bool> TryAttachAsync(IncompleteRecordingStoreData data, LibraryItem item, DateTime now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.Path) || data.Records.Any(r => r.ItemIds.Contains(item.Id)))
        {
            return false;
        }

        foreach (var record in data.Records.Where(r => !r.Dismissed))
        {
            var inWindow = now - record.JudgedAt < IncompleteRecordingRules.AttachWindow;
            foreach (var fragment in record.Fragments)
            {
                var matches = string.Equals(fragment.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                    ? inWindow || item.DateCreated <= record.JudgedAt
                    : inWindow && IsRenamedCopy(fragment, item);
                if (!matches
                    || WrittenByLaterRecording(data, record, fragment.Path)
                    || WrittenByLaterRecording(data, record, item.Path))
                {
                    continue;
                }

                await AttachAsync(record, fragment, item, cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    // Another recording judged after this one, or still running, wrote the path.
    private bool WrittenByLaterRecording(IncompleteRecordingStoreData data, IncompleteRecord record, string path)
    {
        if (data.Snapshots.Any(s => (s.JudgedAt == null || s.JudgedAt > record.JudgedAt)
            && s.Fragments.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))))
        {
            return true;
        }

        // The server adds the item when a recording starts, which can be before a sample sees it.
        return _timers.Any(t => IsLive(t) && t.TimerId != record.TimerId
            && string.Equals(_host.GetActiveRecordingPath(t.TimerId), path, StringComparison.OrdinalIgnoreCase));
    }

    // The same file renamed in place: the original is gone, and it is the only file beside where
    // it was with the same name, created after the recording began.
    private bool IsRenamedCopy(RecordingFragment fragment, LibraryItem item)
    {
        if (fragment.ItemIds.Count > 0 || item.DateCreated < fragment.FirstSampleAt || _host.FileExists(fragment.Path))
        {
            return false;
        }

        var siblings = _host.FindRenamedSiblings(fragment.Path);
        return siblings.Count == 1 && string.Equals(siblings[0], item.Path, StringComparison.OrdinalIgnoreCase);
    }

    private LibraryItem? FindRenamedItem(IncompleteRecordingStoreData data, RecordingFragment fragment)
    {
        if (fragment.ItemIds.Count > 0 || _host.FileExists(fragment.Path))
        {
            return null;
        }

        var siblings = _host.FindRenamedSiblings(fragment.Path);
        var item = siblings.Count == 1 ? _host.FindItemByPath(siblings[0]) : null;
        if (item == null || item.DateCreated < fragment.FirstSampleAt || data.Records.Any(r => r.ItemIds.Contains(item.Id)))
        {
            return null;
        }

        return item;
    }

    private async Task AttachAsync(IncompleteRecord record, RecordingFragment fragment, LibraryItem item, CancellationToken cancellationToken)
    {
        if (fragment.ItemIds.Contains(item.Id))
        {
            return;
        }

        fragment.ItemIds.Add(item.Id);
        if (record.Dismissed || record.TaggedItemIds.Contains(item.Id))
        {
            return;
        }

        if (await SetTagSafeAsync(item.Id, true, cancellationToken).ConfigureAwait(false))
        {
            record.TaggedItemIds.Add(item.Id);
        }
    }

    private async Task<bool> SetTagSafeAsync(Guid itemId, bool present, CancellationToken cancellationToken)
    {
        try
        {
            return await _host.SetTagAsync(itemId, IncompleteRecordingRules.Tag, present, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not update the incomplete tag on {ItemId}", itemId);
            return false;
        }
    }

    private static CompleteShowingRequest? FindOpenRequest(IncompleteRecordingStoreData data, EpisodeKey episode) =>
        data.Requests.FirstOrDefault(r => CompleteShowingState.IsOpen(r.State) && r.Episode.Matches(episode));

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private IncompleteRecordingStoreData Load()
    {
        if (_data != null)
        {
            return _data;
        }

        _data = _stateFile.Read(_storePath, json => JsonSerializer.Deserialize<IncompleteRecordingStoreData>(json, JsonOptions))
            ?? new IncompleteRecordingStoreData();
        RefreshTracked(_data);
        return _data;
    }

    private void RefreshTracked(IncompleteRecordingStoreData data) => _tracked = new TrackedItems(data);

    private void Save(IncompleteRecordingStoreData data)
    {
        RefreshTracked(data);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            _stateFile.Write(_storePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn(ex, "Could not save incomplete recordings to {Path}", _storePath);
        }
    }

    // A library scan raises an event per item, so the watcher drops the ones no record can concern
    // before queuing work behind the lock. Renamed copies share their original's directory and
    // name, so one key per fragment covers both kinds of attachment.
    private sealed class TrackedItems
    {
        private readonly HashSet<string> _stems = new(StringComparer.OrdinalIgnoreCase);

        public TrackedItems(IncompleteRecordingStoreData data)
        {
            foreach (var record in data.Records)
            {
                ItemIds.UnionWith(record.ItemIds);
                ItemIds.UnionWith(record.TaggedItemIds);
                if (!record.Dismissed)
                {
                    _stems.UnionWith(record.Fragments.Select(f => Stem(f.Path)));
                }
            }
        }

        public HashSet<Guid> ItemIds { get; } = new();

        public bool MayHaveWritten(string path) => _stems.Contains(Stem(path));

        private static string Stem(string path) =>
            Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
    }
}

public enum CompleteShowingRefusal
{
    None,
    NotFound,
    NoEpisodeNumbers,
}
