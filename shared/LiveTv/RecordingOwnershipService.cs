using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.LiveTv;

/// <summary>
/// Tracks which users own each Live TV timer, series rule and finished recording. The server stores
/// no user on any of them, so ownership is recorded when a user schedules or claims something
/// through the plugin and follows the recording from timer to library item.
///
/// All state lives in one JSON file and every read and write goes through one lock, the same
/// pattern as <see cref="IncompleteRecordingService"/>. Each finished recording also carries one
/// "Recorded by name" tag per owner, a readable backup the store can be rebuilt from when the file
/// and its backup are both lost. Owners are only ever added by a timer moving between series rules;
/// nothing is forgotten because it is missing at the moment, only on the server's cancel and removal
/// events or once it is gone and its recording has been handed over.
///
/// Which owners a recording file carries follows one rule: while a timer is recording to a path, that
/// timer's owners (none, for a timer nobody owns through Moonfin) replace the owners of the library
/// item at that path. The server derives an item's id from its path, so a rerun recorded to a deleted
/// recording's file comes back as the same item, and nothing can un-claim an owner, so the recording
/// timer is the only authority on who owns the new file. Each sync notes the file every in-progress
/// timer is writing. When a timer stops, its note is kept for <see cref="PostRecordingAllowance"/>
/// so an item that only reaches the library afterwards still gets its owners, and the first time
/// the item is found the owners are applied once more and the note dropped, so a claim made on the
/// finished recording is never undone. An item that appears at a path no timer is recording to gets
/// back the owners it had when it left the library, if that was within the retention (a storage
/// outage), and otherwise keeps whatever the store has.
/// </summary>
public sealed class RecordingOwnershipService : IRecordingOwners, IDisposable
{
    /// <summary>The state file's name inside the plugin's data folder.</summary>
    public const string StoreFileName = "livetv-recording-owners.json";

    /// <summary>
    /// The store format this build writes. A file with a higher version came from a newer plugin
    /// (this one is running after a downgrade): it is read, but never saved over, so the newer
    /// plugin finds its data intact after an upgrade. Changes made meanwhile last until restart.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The fixed, non-localized start of each owner tag, so a rebuild can parse it.</summary>
    public const string TagPrefix = RecordingOwnerTags.Prefix;

    /// <summary>Whether a tag is one of the owner tags this feature stamps on recordings.</summary>
    public static bool IsOwnerTag(string? tag) => RecordingOwnerTags.IsOwnerTag(tag);

    /// <summary>An item's tags without the owner tags (see <see cref="RecordingOwnerTags.Without"/>).</summary>
    public static IReadOnlyList<string> WithoutOwnerTags(IEnumerable<string>? tags) => RecordingOwnerTags.Without(tags);

    /// <summary>How long a removed recording's owners are kept in case the item comes back.</summary>
    public static readonly TimeSpan RemovedRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// How long after a timer stops recording (or after its window ends, if later) the file it wrote
    /// is still handed the timer's owners when the item for it first turns up in the library: the
    /// item may only be added once the recording is done, or after a post-processing step.
    /// </summary>
    public static readonly TimeSpan PostRecordingAllowance = TimeSpan.FromHours(1);

    private const string StatusCancelled = "Cancelled";
    private const string StatusCompleted = "Completed";
    private const string StatusInProgress = "InProgress";

    // Matches AtomicFile.BackupPath in both servers' IStateFile implementations.
    private const string BackupSuffix = ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ILiveTvRecordingHost _host;
    private readonly IRecordingLog _logger;
    private readonly IStateFile _stateFile;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string> _storePathProvider;
    private readonly Func<bool> _enabled;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Everything below is guarded by _lock.

    // Any timer id, service or API kind, to the API id the store keys timers by. Events carry
    // either kind, so each is resolved against both. Refreshed whenever the timers are listed.
    private readonly Dictionary<string, string> _apiIds = new(StringComparer.OrdinalIgnoreCase);

    // The timers as last listed, by API id.
    private readonly Dictionary<string, ScheduledTimer> _timers = new(StringComparer.OrdinalIgnoreCase);

    // Each user's name as last seen, so a rename knows which tag to replace and a deleted user's
    // tags can still be found.
    private readonly Dictionary<Guid, string> _userNames = new();

    // Owned timers whose service id the last listing couldn't map. Listing again for a cancel
    // only helps when an owned timer has become unmapped since.
    private readonly HashSet<string> _unmappedAtLastListing = new(StringComparer.OrdinalIgnoreCase);

    // The service's own id of each timer noted recording, by API id, so a timer that has dropped
    // out of a listing can still be asked whether it is writing a file.
    private readonly Dictionary<string, string> _recordingServiceIds = new(StringComparer.OrdinalIgnoreCase);

    private OwnershipStoreData? _data;

    // Set when the store was rebuilt from tags: the unusable file must be copied aside before the
    // first save overwrites it.
    private bool _preserveUnusable;

    // Set when the store came from a newer plugin, which must find it as it left it.
    private bool _readOnly;

    // A save that failed (storage away for a moment); the next sync writes the store again.
    private bool _unsaved;

    // Whether the store's items have been checked against the library since this start or since
    // the feature was last off; removals while it was off were never seen.
    private bool _reconciled;

    // The stand-in a call gets while a rebuild from tags couldn't run. It is never saved, so it
    // can't take the place of the owners the rebuild will find.
    private OwnershipStoreData? _unbuilt;
    private volatile bool _recordingInProgress;
    private long _nextTimerStartTicks;

    public RecordingOwnershipService(
        ILiveTvRecordingHost host,
        IRecordingLog log,
        IStateFile stateFile,
        Func<DateTime> now,
        string storePath,
        Func<bool> enabled)
        : this(host, log, stateFile, now, () => storePath, enabled)
    {
    }

    /// <summary>
    /// As the main constructor, but the store path is asked for each time it is needed, so a
    /// host whose data folder isn't known yet can throw instead of the file landing somewhere
    /// it must not.
    /// </summary>
    public RecordingOwnershipService(
        ILiveTvRecordingHost host,
        IRecordingLog log,
        IStateFile stateFile,
        Func<DateTime> now,
        Func<string> storePathProvider,
        Func<bool> enabled)
    {
        _host = host;
        _logger = log;
        _stateFile = stateFile;
        _utcNow = now;
        _storePathProvider = storePathProvider;
        _enabled = enabled;
    }

    private string _storePath => _storePathProvider();

    /// <summary>Whether any timer was recording at the last sync, so the watcher can sync often enough to catch its file.</summary>
    public bool HasRecordingInProgress => _recordingInProgress && Enabled;

    /// <summary>
    /// The earliest recording window start, at the last listing, that began no more than ten
    /// minutes before that listing and isn't cancelled; null when there is none. The watcher syncs
    /// often around it, since a recording's file is only captured while a sync sees it in progress.
    /// </summary>
    public DateTime? NextTimerStart
    {
        get
        {
            var ticks = Interlocked.Read(ref _nextTimerStartTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private bool Enabled => _enabled();

    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Records a program for a user: joins the timer that already records it, or schedules one.
    /// Null when the feature is off or the server couldn't schedule it.
    /// </summary>
    public async Task<OwnershipOutcome?> RecordProgramAsync(string programId, Guid userId, CancellationToken ct) =>
        (await TryRecordProgramAsync(programId, userId, ct).ConfigureAwait(false)).Outcome;

    /// <summary>As <see cref="RecordProgramAsync"/>, with the server's reason when it refused to schedule.</summary>
    public async Task<OwnershipAttempt> TryRecordProgramAsync(string programId, Guid userId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(programId))
        {
            return new OwnershipAttempt(null);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            var existing = FindLiveTimerForProgram(await RefreshTimersAsync(ct).ConfigureAwait(false), programId);
            if (existing != null)
            {
                var joinedId = ApiIdOf(existing);
                AddOwners(data.Timers, joinedId, new[] { userId });
                Track(data, existing);
                Save(data);
                await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
                _logger.Info("User {UserId} joined the recording of program {ProgramId} (timer {TimerId})", userId, programId, joinedId);
                return new OwnershipAttempt(new OwnershipOutcome(joinedId, ToOwnerInfos(data.Timers, joinedId), true));
            }

            var result = await _host.ScheduleProgramAsync(programId, ct).ConfigureAwait(false);
            if (result.Outcome == ScheduleOutcome.Failed)
            {
                _logger.Warn(null, "Could not schedule program {ProgramId} for user {UserId}", programId, userId);
                return new OwnershipAttempt(null, result.Error);
            }

            // A timer someone else made between the listing and the schedule call, or one the
            // server returned no ids for: either way the listing now has it.
            var timers = await RefreshTimersAsync(ct).ConfigureAwait(false);
            var timer = result.ApiId != null && _timers.TryGetValue(result.ApiId, out var byApi)
                ? byApi
                : result.TimerId != null && _apiIds.TryGetValue(result.TimerId, out var mapped) && _timers.TryGetValue(mapped, out var byService)
                    ? byService
                    : FindLiveTimerForProgram(timers, programId);
            var timerId = result.ApiId ?? (timer != null ? ApiIdOf(timer) : null);
            if (timerId == null)
            {
                _logger.Warn(null, "Scheduled program {ProgramId} for user {UserId} but could not find its timer, so it has no owner", programId, userId);
                return new OwnershipAttempt(null);
            }

            var joined = result.Outcome == ScheduleOutcome.AlreadyScheduled;
            AddOwners(data.Timers, timerId, new[] { userId });
            if (timer != null)
            {
                Track(data, timer);
            }
            else if (!string.IsNullOrEmpty(result.TimerId))
            {
                RememberServiceId(data, timerId, result.TimerId!);
            }

            Save(data);
            await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
            _logger.Info("User {UserId} {Action} program {ProgramId} (timer {TimerId})", userId, joined ? "joined the recording of" : "scheduled", programId, timerId);
            return new OwnershipAttempt(new OwnershipOutcome(timerId, ToOwnerInfos(data.Timers, timerId), joined));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Records a program's series for a user: joins the rule that already records the series,
    /// ignoring the settings, or creates one with them. Null when the feature is off, the program
    /// is unknown or the server couldn't create the rule.
    /// </summary>
    public async Task<OwnershipOutcome?> RecordSeriesAsync(string programId, Guid userId, IReadOnlyDictionary<string, JsonElement>? settings, CancellationToken ct) =>
        (await TryRecordSeriesAsync(programId, userId, settings, ct).ConfigureAwait(false)).Outcome;

    /// <summary>As <see cref="RecordSeriesAsync"/>, with the server's reason when it refused to create the rule.</summary>
    public async Task<OwnershipAttempt> TryRecordSeriesAsync(string programId, Guid userId, IReadOnlyDictionary<string, JsonElement>? settings, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(programId))
        {
            return new OwnershipAttempt(null);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            var program = _host.GetProgram(programId);
            if (program == null)
            {
                _logger.Warn(null, "Could not record the series of program {ProgramId} for user {UserId}: the program isn't in the guide", programId, userId);
                return new OwnershipAttempt(null);
            }

            var timers = await RefreshTimersAsync(ct).ConfigureAwait(false);
            var rules = await _host.GetSeriesTimersAsync(ct).ConfigureAwait(false);
            var ruleId = FindRuleForSeries(program, timers, rules);
            var joined = ruleId != null;
            if (ruleId == null)
            {
                var creation = await _host.CreateSeriesTimerAsync(programId, settings, ct).ConfigureAwait(false);
                ruleId = creation.RuleId;
                if (ruleId == null)
                {
                    _logger.Warn(null, "Could not create a series rule from program {ProgramId} for user {UserId}", programId, userId);
                    return new OwnershipAttempt(null, creation.Error);
                }

                // The new rule's timers exist now; list them so its owners reach them right away.
                timers = await RefreshTimersAsync(ct).ConfigureAwait(false);
            }

            AddOwners(data.SeriesTimers, ruleId, new[] { userId });
            ApplySeriesOwners(data, timers);
            Save(data);
            await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
            _logger.Info("User {UserId} {Action} series rule {RuleId} from program {ProgramId}", userId, joined ? "joined" : "created", ruleId, programId);
            return new OwnershipAttempt(new OwnershipOutcome(ruleId, ToOwnerInfos(data.SeriesTimers, ruleId), joined));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Adds a user to the owners of an existing timer, series rule or recording, and for a
    /// recording stamps the user's tag. Null when the feature is off or the object doesn't exist.
    /// </summary>
    public async Task<IReadOnlyList<OwnerInfo>?> ClaimAsync(OwnedKind kind, string id, Guid userId, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(id))
        {
            return null;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            switch (kind)
            {
                case OwnedKind.Timer:
                {
                    var timer = await FindTimerAsync(id, ct).ConfigureAwait(false);
                    // Lookup only answers for a timer whose program the caller can see, so a claim
                    // neither adds them to nor shows them the owners of one they can't.
                    if (timer == null
                        || timer.Status == StatusCancelled
                        || timer.ProgramId == null
                        || _host.GetProgram(timer.ProgramId) is not { } program
                        || !_host.IsVisibleTo(program.ItemId, userId))
                    {
                        return null;
                    }

                    var timerId = ApiIdOf(timer);
                    AddOwners(data.Timers, timerId, new[] { userId });
                    Track(data, timer);
                    Save(data);
                    await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
                    _logger.Info("User {UserId} claimed timer {TimerId}", userId, timerId);
                    return ToOwnerInfos(data.Timers, timerId);
                }

                case OwnedKind.Series:
                {
                    var rules = await _host.GetSeriesTimersAsync(ct).ConfigureAwait(false);
                    var rule = rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    var ruleTimers = rule == null ? null : await RefreshTimersAsync(ct).ConfigureAwait(false);
                    // Like a timer claim, a rule is only open to a caller who can see something it
                    // records, so a claim can't join or reveal the owners of a rule they can't see.
                    if (rule == null || !CanSeeRule(rule, ruleTimers!, userId))
                    {
                        return null;
                    }

                    AddOwners(data.SeriesTimers, rule.Id, new[] { userId });
                    ApplySeriesOwners(data, ruleTimers!);
                    Save(data);
                    await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
                    _logger.Info("User {UserId} claimed series rule {RuleId}", userId, rule.Id);
                    return ToOwnerInfos(data.SeriesTimers, rule.Id);
                }

                case OwnedKind.Item:
                {
                    // Only a Live TV recording can be claimed. A server that can't tell is refused
                    // too, so the claim never lets a caller stamp an ordinary library item.
                    if (!Guid.TryParse(id, out var itemId) || _host.GetTags(itemId) == null || IsRecording(itemId, userId) != true)
                    {
                        return null;
                    }

                    // A recording still being written takes its timer's owners at every sync, so the
                    // caller joins the timer instead; one whose timer has stopped first takes the
                    // owners it was waiting to be handed, so the claim is added to them, not lost.
                    var recordedBy = FindEntryForItem(data, itemId);
                    if (recordedBy == null)
                    {
                        // A recording that started since the last sync isn't noted yet; a fresh
                        // listing notes it, so the claim goes to its timer and survives the next sync.
                        await RefreshTimersAsync(ct).ConfigureAwait(false);
                        recordedBy = FindEntryForItem(data, itemId);
                    }

                    if (recordedBy != null && !data.TimerPathsUntil.ContainsKey(recordedBy))
                    {
                        AddOwners(data.Timers, recordedBy, new[] { userId });
                        await ApplyTimerOwnersAsync(data, itemId, recordedBy, ct).ConfigureAwait(false);
                        Save(data);
                        _logger.Info("User {UserId} claimed recording {ItemId}, which timer {TimerId} is still recording; they own the timer now", userId, itemId, recordedBy);
                        return ToOwnerInfos(data.Items, itemId);
                    }

                    if (recordedBy != null)
                    {
                        await ApplyTimerOwnersAsync(data, itemId, recordedBy, ct).ConfigureAwait(false);
                        FinishEntry(data, recordedBy);
                    }

                    AddOwners(data.Items, itemId, new[] { userId });
                    Save(data);
                    await StampTagsAsync(itemId, new[] { userId }, ct).ConfigureAwait(false);
                    _logger.Info("User {UserId} claimed recording {ItemId}", userId, itemId);
                    return ToOwnerInfos(data.Items, itemId);
                }

                default:
                    return null;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Owners of the given timers, rules and recordings, for the ones the caller may see. Disabled
    /// users and ids without owners are left out. Also lists which of the given items are Live TV
    /// recordings the caller may see, owned or not, or null when the server can't tell for one.
    /// </summary>
    public async Task<OwnersLookup> LookupAsync(
        IReadOnlyCollection<string> timerIds,
        IReadOnlyCollection<string> seriesTimerIds,
        IReadOnlyCollection<Guid> itemIds,
        Guid callerId,
        CancellationToken ct)
    {
        var timerOwners = new Dictionary<string, IReadOnlyList<OwnerInfo>>(StringComparer.OrdinalIgnoreCase);
        var seriesOwners = new Dictionary<string, IReadOnlyList<OwnerInfo>>(StringComparer.OrdinalIgnoreCase);
        var itemOwners = new Dictionary<Guid, IReadOnlyList<OwnerInfo>>();
        if (!Enabled)
        {
            return new OwnersLookup(timerOwners, seriesOwners, itemOwners);
        }

        // Needs no owner state, so it is worked out before taking the lock.
        var lookup = new OwnersLookup(timerOwners, seriesOwners, itemOwners, RecordingsVisibleTo(itemIds, callerId));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();

            var ownedItems = itemIds.Distinct().Where(data.Items.ContainsKey).ToList();
            if (ownedItems.Count > 0)
            {
                foreach (var itemId in _host.VisibleTo(ownedItems, callerId))
                {
                    AddIfAny(itemOwners, itemId, ToOwnerInfos(data.Items, itemId));
                }
            }

            var ownedTimers = timerIds.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(id => data.Timers.ContainsKey(Resolve(id)) || !_apiIds.ContainsKey(id))
                .ToList();
            if (ownedTimers.Count > 0)
            {
                // A timer the last listing didn't have may be new; list once more before giving up on it.
                if (ownedTimers.Any(id => !_apiIds.ContainsKey(id)))
                {
                    await RefreshTimersAsync(ct).ConfigureAwait(false);
                }

                foreach (var id in ownedTimers)
                {
                    var key = Resolve(id);
                    if (data.Timers.ContainsKey(key)
                        && _timers.TryGetValue(key, out var timer)
                        && timer.ProgramId != null
                        && _host.GetProgram(timer.ProgramId) is { } program
                        && _host.IsVisibleTo(program.ItemId, callerId))
                    {
                        AddIfAny(timerOwners, id, ToOwnerInfos(data.Timers, key));
                    }
                }
            }

            // Anyone who can use Live TV can list every series rule, so a rule the server lists is
            // answered for whoever asks.
            var ownedRules = seriesTimerIds.Distinct(StringComparer.OrdinalIgnoreCase).Where(data.SeriesTimers.ContainsKey).ToList();
            if (ownedRules.Count > 0)
            {
                var listed = (await _host.GetSeriesTimersAsync(ct).ConfigureAwait(false))
                    .Select(r => r.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var id in ownedRules.Where(listed.Contains))
                {
                    AddIfAny(seriesOwners, id, ToOwnerInfos(data.SeriesTimers, id));
                }
            }

            return lookup;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Every timer, rule and recording that lists the user as an owner.</summary>
    public async Task<MineResult> GetMineAsync(Guid userId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new MineResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Guid>());
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            return new MineResult(
                data.Timers.Where(e => e.Value.Contains(userId)).Select(e => e.Key).ToList(),
                data.SeriesTimers.Where(e => e.Value.Contains(userId)).Select(e => e.Key).ToList(),
                data.Items.Where(e => e.Value.Contains(userId)).Select(e => e.Key).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Brings the store in line with the server: copies series owners onto their timers, notes the
    /// file each in-progress timer is writing and gives it the timer's owners, hands finished
    /// recordings their timer's owners once, turns owned items the library no longer has into
    /// tombstones, forgets timers and rules the server is done with, and purges old tombstones.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            _reconciled = false;
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            var changed = false;
            var timers = await RefreshTimersAsync(ct).ConfigureAwait(false);
            var rules = await _host.GetSeriesTimersAsync(ct).ConfigureAwait(false);

            var now = _utcNow();
            var cutoff = now - RemovedRetention;

            changed |= RekeyServiceIds(data);
            changed |= ApplySeriesOwners(data, timers);

            foreach (var timer in timers)
            {
                changed |= Track(data, timer);
            }

            // A store left behind while the feature was off (or the plugin wasn't running) may own
            // items the library has lost. Checked once, not on every pass: removals while it runs
            // arrive as events.
            if (!_reconciled)
            {
                changed |= ReconcileItems(data, now);
                _reconciled = true;
            }

            // Jellyfin adds a recording's item when the recording starts, usually before the file
            // is noted here, so the item's ItemAdded found no entry for it. Give it its timer's
            // owners now, and hand over the recordings that have finished.
            changed |= await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);

            // A timer with a recorded file is forgotten along with that file's entry, once its item
            // has taken the owners or the entry lapses. One without a recorded file is forgotten
            // only after the server has left it out for the whole retention, since a listing can
            // miss timers while a Live TV service is failing.
            foreach (var key in data.Timers.Keys.ToList())
            {
                if (data.TimerPaths.ContainsKey(key) || _timers.ContainsKey(key))
                {
                    changed |= data.TimersMissingSince.Remove(key);
                }
                else if (!data.TimersMissingSince.TryGetValue(key, out var missingSince))
                {
                    data.TimersMissingSince[key] = now;
                    changed = true;
                }
                else if (missingSince <= cutoff)
                {
                    data.Timers.Remove(key);
                    _logger.Info("Forgot the owners of timer {TimerId}, which the server hasn't listed since {Since}", key, missingSince);
                    changed = true;
                }
            }

            var listedRules = rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in data.SeriesTimers.Keys.ToList())
            {
                if (listedRules.Contains(key))
                {
                    changed |= data.SeriesTimersMissingSince.Remove(key);
                }
                else if (!data.SeriesTimersMissingSince.TryGetValue(key, out var missingSince))
                {
                    data.SeriesTimersMissingSince[key] = now;
                    changed = true;
                }
                else if (missingSince <= cutoff)
                {
                    data.SeriesTimers.Remove(key);
                    _logger.Info("Forgot the owners of series rule {RuleId}, which the server hasn't listed since {Since}", key, missingSince);
                    changed = true;
                }
            }

            changed |= PruneSideTables(data);

            foreach (var entry in data.Removed.Where(e => e.Value.RemovedAt < cutoff).ToList())
            {
                data.Removed.Remove(entry.Key);
                changed = true;
            }

            foreach (var user in _host.GetUsers())
            {
                // Only users not seen yet: a rename's old name is kept until its event arrives.
                if (!_userNames.ContainsKey(user.Id))
                {
                    _userNames[user.Id] = user.Name;
                }
            }

            if (changed || _unsaved)
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
    /// A timer was cancelled, which is for everyone, so its owners are forgotten. A recording that
    /// was stopped partway still reaches the library, so a timer that was writing a file (noted at
    /// an earlier sync, or passed as <paramref name="activePath"/>, which the watcher reads because
    /// the server raises the cancel while the recording is still running) has that file kept for
    /// <see cref="PostRecordingAllowance"/>: an item already in the library for it takes the timer's
    /// owners at once, and one that arrives later takes them then. This holds for a timer nobody
    /// owns too, whose recording then ends up with no owners.
    /// </summary>
    public async Task OnTimerCancelledAsync(string timerId, string? activePath, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(timerId))
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (!_apiIds.ContainsKey(timerId) && NeedsListingToResolve(data, timerId))
            {
                await RefreshTimersAsync(ct).ConfigureAwait(false);
            }

            var key = Resolve(timerId);
            var until = _utcNow() + PostRecordingAllowance;
            if (!string.IsNullOrEmpty(activePath))
            {
                // The sync may already hold this recording under the timer's API id.
                key = data.TimerPaths
                    .Where(e => string.Equals(e.Value, activePath, StringComparison.OrdinalIgnoreCase) && !data.TimerPathsUntil.ContainsKey(e.Key))
                    .Select(e => e.Key)
                    .FirstOrDefault() ?? key;
                data.TimerPaths[key] = activePath!;
                data.TimerPathsUntil[key] = until;
                Save(data);
                _logger.Info("Cancelled timer {TimerId} was recording {Path}; its owners go to that recording", key, activePath);
            }
            else if (data.TimerPaths.ContainsKey(key))
            {
                if (!data.TimerPathsUntil.ContainsKey(key))
                {
                    data.TimerPathsUntil[key] = until;
                    Save(data);
                }
            }
            else
            {
                if (!data.Timers.ContainsKey(key))
                {
                    return;
                }

                data.Timers.Remove(key);
                PruneSideTables(data);
                Save(data);
                _logger.Info("Forgot the owners of cancelled timer {TimerId}", key);
                return;
            }

            await ApplyRecordingsAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>As <see cref="OnTimerCancelledAsync(string, string, CancellationToken)"/> with no file known.</summary>
    public Task OnTimerCancelledAsync(string timerId, CancellationToken ct) => OnTimerCancelledAsync(timerId, null, ct);

    /// <summary>
    /// A library item arrived. If a timer is recording to its path (or recorded to it and hasn't
    /// handed it over yet) the item takes that timer's owners, replacing any it had. Otherwise a
    /// recording that left the library within the retention gets its owners back (a storage outage
    /// or rescan), and anything else keeps the owners the store already holds for it. Each owner's
    /// tag is stamped on it.
    /// </summary>
    public async Task OnItemAddedAsync(LibraryItem item, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            var key = FindEntryForPath(data, item.Path);
            if (key != null)
            {
                var changed = await ApplyTimerOwnersAsync(data, item.Id, key, ct).ConfigureAwait(false);
                if (data.TimerPathsUntil.ContainsKey(key))
                {
                    FinishEntry(data, key);
                    Save(data);
                }

                if (changed)
                {
                    _logger.Info("Recording {ItemId} from timer {TimerId} took its owners", item.Id, key);
                }

                return;
            }

            if (!data.Removed.TryGetValue(item.Id, out var removed))
            {
                return;
            }

            data.Removed.Remove(item.Id);
            if (removed.RemovedAt < _utcNow() - RemovedRetention)
            {
                Save(data);
                _logger.Info("Recording {ItemId} came back to the library after its owners were kept for {Days} days; it has no owners", item.Id, RemovedRetention.TotalDays);
                return;
            }

            // A rerun that is given a wrong tombstone here is put right by the next sync, while
            // its timer is recording.
            AddOwners(data.Items, item.Id, removed.Owners);
            Save(data);
            _logger.Info("Recording {ItemId} came back to the library; restored its {Count} owner(s)", item.Id, removed.Owners.Count);
            await StampTagsAsync(item.Id, removed.Owners, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A recording left the library. Its owners are kept as a tombstone in case it comes back.</summary>
    public async Task OnItemRemovedAsync(Guid itemId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            if (!data.Items.TryGetValue(itemId, out var owners))
            {
                return;
            }

            data.Items.Remove(itemId);
            data.Removed[itemId] = new RemovedItem { Owners = owners, RemovedAt = _utcNow() };
            Save(data);
            _logger.Info("Recording {ItemId} left the library; keeping its owners for {Days} days in case it comes back", itemId, RemovedRetention.TotalDays);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A user changed. A rename swaps the user's tag on every recording they own that still has it.</summary>
    public async Task OnUserUpdatedAsync(ServerUser user, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            var known = _userNames.TryGetValue(user.Id, out var oldName);
            _userNames[user.Id] = user.Name;
            if (!known || string.Equals(oldName, user.Name, StringComparison.Ordinal))
            {
                return;
            }

            var oldTag = TagPrefix + oldName;
            var newTag = TagPrefix + user.Name;
            var renamed = 0;
            foreach (var itemId in data.Items.Where(e => e.Value.Contains(user.Id)).Select(e => e.Key).ToList())
            {
                // A tag removed outside Moonfin stays gone.
                if (_host.GetTags(itemId)?.Contains(oldTag, StringComparer.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                if (!SharesName(data.Items[itemId], user.Id, oldName!))
                {
                    await SetTagAsync(itemId, oldTag, false, ct).ConfigureAwait(false);
                }

                if (await SetTagAsync(itemId, newTag, true, ct).ConfigureAwait(false))
                {
                    renamed++;
                }
            }

            _logger.Info("User {UserId} was renamed from {OldName} to {NewName}; updated the tag on {Count} recording(s)", user.Id, oldName, user.Name, renamed);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A user was deleted: they leave every owner set and their tags are removed.</summary>
    public async Task OnUserDeletedAsync(Guid userId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = Load();
            _userNames.TryGetValue(userId, out var name);
            var ownedItems = data.Items.Where(e => e.Value.Contains(userId)).Select(e => e.Key).ToList();

            var changed = RemoveOwner(data.Timers, userId);
            changed |= RemoveOwner(data.SeriesTimers, userId);
            changed |= RemoveOwner(data.Items, userId);
            foreach (var entry in data.Removed.ToList())
            {
                if (entry.Value.Owners.Remove(userId))
                {
                    changed = true;
                    if (entry.Value.Owners.Count == 0)
                    {
                        data.Removed.Remove(entry.Key);
                    }
                }
            }

            if (changed)
            {
                Save(data);
            }

            if (name != null)
            {
                var tag = TagPrefix + name;
                foreach (var itemId in ownedItems)
                {
                    var remaining = data.Items.TryGetValue(itemId, out var owners) ? owners : null;
                    if (remaining == null || !SharesName(remaining, userId, name))
                    {
                        await SetTagAsync(itemId, tag, false, ct).ConfigureAwait(false);
                    }
                }
            }
            else if (ownedItems.Count > 0)
            {
                _logger.Warn(null, "Deleted user {UserId} owned {Count} recording(s) but their name is unknown, so their tags were left", userId, ownedItems.Count);
            }

            _userNames.Remove(userId);
            if (changed)
            {
                _logger.Info("Removed deleted user {UserId} from every recording owner list", userId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> OwnersOfItems(IEnumerable<Guid> itemIds)
    {
        if (!Enabled)
        {
            return Array.Empty<Guid>();
        }

        _lock.Wait();
        try
        {
            var data = Load();
            var result = new List<Guid>();
            foreach (var itemId in itemIds)
            {
                if (data.Items.TryGetValue(itemId, out var owners))
                {
                    result.AddRange(owners.Where(o => !result.Contains(o)));
                }
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void AddTimerOwners(string timerId, IEnumerable<Guid> owners)
    {
        if (!Enabled || string.IsNullOrEmpty(timerId))
        {
            return;
        }

        _lock.Wait();
        try
        {
            // An id the last listing didn't have is kept as given; the next sync's listing maps
            // service ids, and Jellyfin's schedule call hands back the API id.
            // A deleted user lingering in a request's owners is never put back on a timer; one
            // whose lookup failed is kept, since a failed read doesn't mean the user is gone.
            var data = Load();
            if (AddOwners(data.Timers, Resolve(timerId), owners.Where(o => !UserIsGone(o)).ToList()))
            {
                Save(data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string ApiIdOf(ScheduledTimer timer) =>
        string.IsNullOrEmpty(timer.ApiId) ? timer.TimerId : timer.ApiId!;

    private static ScheduledTimer? FindLiveTimerForProgram(IReadOnlyList<ScheduledTimer> timers, string programId) =>
        timers.FirstOrDefault(t => t.Status != StatusCancelled && t.Status != StatusCompleted && string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase));

    private static bool AddOwners<TKey>(Dictionary<TKey, List<Guid>> map, TKey key, IEnumerable<Guid> owners)
        where TKey : notnull
    {
        var added = false;
        foreach (var owner in owners)
        {
            if (owner == Guid.Empty)
            {
                continue;
            }

            if (!map.TryGetValue(key, out var list))
            {
                list = new List<Guid>();
                map[key] = list;
            }

            if (!list.Contains(owner))
            {
                list.Add(owner);
                added = true;
            }
        }

        return added;
    }

    private static bool RemoveOwner<TKey>(Dictionary<TKey, List<Guid>> map, Guid owner)
        where TKey : notnull
    {
        var changed = false;
        foreach (var entry in map.ToList())
        {
            if (entry.Value.Remove(owner))
            {
                changed = true;
                if (entry.Value.Count == 0)
                {
                    map.Remove(entry.Key);
                }
            }
        }

        return changed;
    }

    private static void AddIfAny<TKey>(Dictionary<TKey, IReadOnlyList<OwnerInfo>> map, TKey key, IReadOnlyList<OwnerInfo> owners)
        where TKey : notnull
    {
        if (owners.Count > 0)
        {
            map[key] = owners;
        }
    }

    // The API id for any timer id: from the listings seen, else from the service ids stored with
    // owned timers (which outlive the listing and a restart), else the id itself.
    private string Resolve(string timerId)
    {
        if (_apiIds.TryGetValue(timerId, out var apiId))
        {
            return apiId;
        }

        if (_data != null)
        {
            foreach (var entry in _data.TimerServiceIds)
            {
                if (string.Equals(entry.Value, timerId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Key;
                }
            }
        }

        return timerId;
    }

    // Lists the timers. The id map is merged rather than replaced, so a timer that leaves the
    // listing (Jellyfin drops a cancelled one-off) still resolves from its service id; a mapping
    // is pruned only once its timer is neither listed nor owned.
    private async Task<IReadOnlyList<ScheduledTimer>> RefreshTimersAsync(CancellationToken ct)
    {
        var timers = await _host.GetTimersAsync(ct).ConfigureAwait(false);
        _timers.Clear();
        foreach (var timer in timers)
        {
            var apiId = ApiIdOf(timer);
            if (string.IsNullOrEmpty(apiId))
            {
                continue;
            }

            _timers[apiId] = timer;
            _apiIds[apiId] = apiId;
            if (!string.IsNullOrEmpty(timer.TimerId))
            {
                _apiIds[timer.TimerId] = apiId;
            }
        }

        foreach (var entry in _apiIds.ToList())
        {
            if (!_timers.ContainsKey(entry.Value) && _data?.Timers.ContainsKey(entry.Value) != true)
            {
                _apiIds.Remove(entry.Key);
            }
        }

        _unmappedAtLastListing.Clear();
        if (_data != null)
        {
            _unmappedAtLastListing.UnionWith(_data.Timers.Keys.Where(k => !IsMapped(_data, k)));
        }

        _recordingInProgress = timers.Any(t => t.Status == StatusInProgress);
        if (UpdateRecordingMap(Load(), timers))
        {
            _unsaved = true;
        }

        var recentCutoff = _utcNow().AddMinutes(-10);
        var starts = timers
            .Where(t => t.Status != StatusCancelled && t.WindowStart.ToUniversalTime() >= recentCutoff)
            .Select(t => t.WindowStart.ToUniversalTime())
            .ToList();
        Interlocked.Exchange(ref _nextTimerStartTicks, starts.Count == 0 ? 0 : starts.Min().Ticks);
        return timers;
    }

    // Whether a cancel event's id, unknown to the id map, could belong to an owned timer that only
    // a fresh listing would reveal. Deleting a series rule raises one cancel per timer, nearly
    // always for timers nobody owns, so this avoids a listing per event: there is nothing to find
    // when nothing is owned, an owned id or a stored service id resolves without a listing, and a
    // listing can only map an owned timer that has become unmapped since the last one.
    private bool NeedsListingToResolve(OwnershipStoreData data, string timerId)
    {
        if (data.Timers.Count == 0
            || data.Timers.ContainsKey(timerId)
            || data.TimerServiceIds.Values.Contains(timerId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return data.Timers.Keys.Any(k => !IsMapped(data, k) && !_unmappedAtLastListing.Contains(k));
    }

    // Whether both of an owned timer's ids are known: its service id is stored, or a listing or
    // schedule call has mapped it (on Emby the two ids are one).
    private bool IsMapped(OwnershipStoreData data, string key) =>
        data.TimerServiceIds.ContainsKey(key)
        || _apiIds.Values.Contains(key, StringComparer.OrdinalIgnoreCase);

    private async Task<ScheduledTimer?> FindTimerAsync(string id, CancellationToken ct)
    {
        if (!_apiIds.ContainsKey(id) || !_timers.ContainsKey(Resolve(id)))
        {
            await RefreshTimersAsync(ct).ConfigureAwait(false);
        }

        return _timers.TryGetValue(Resolve(id), out var timer) ? timer : null;
    }

    // Notes what an owned timer's later events need: its service id.
    private bool Track(OwnershipStoreData data, ScheduledTimer timer)
    {
        var key = ApiIdOf(timer);
        return data.Timers.ContainsKey(key) && !string.IsNullOrEmpty(timer.TimerId) && RememberServiceId(data, key, timer.TimerId);
    }

    private bool RememberServiceId(OwnershipStoreData data, string apiId, string serviceId)
    {
        _apiIds[serviceId] = apiId;
        _apiIds[apiId] = apiId;
        if (string.Equals(apiId, serviceId, StringComparison.OrdinalIgnoreCase)
            || (data.TimerServiceIds.TryGetValue(apiId, out var known) && string.Equals(known, serviceId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        data.TimerServiceIds[apiId] = serviceId;
        return true;
    }

    // Owners stored under a timer's service id (AddTimerOwners before a listing had the timer)
    // move to its API id, joining any owners already there, along with its recorded file.
    private bool RekeyServiceIds(OwnershipStoreData data)
    {
        var changed = false;
        foreach (var key in data.Timers.Keys.ToList())
        {
            var apiId = Resolve(key);
            if (string.Equals(apiId, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddOwners(data.Timers, apiId, data.Timers[key]);
            data.Timers.Remove(key);
            if (data.TimerPaths.TryGetValue(key, out var path))
            {
                data.TimerPaths.Remove(key);
                var hasUntil = data.TimerPathsUntil.Remove(key, out var until);
                if (!data.TimerPaths.ContainsKey(apiId))
                {
                    data.TimerPaths[apiId] = path;
                    if (hasUntil)
                    {
                        data.TimerPathsUntil[apiId] = until;
                    }
                }
            }

            data.TimersMissingSince.Remove(key);
            data.TimerServiceIds.Remove(key);
            RememberServiceId(data, apiId, key);
            changed = true;
        }

        return changed;
    }

    // Drops what is kept beside a timer or rule whose owners are gone. A timer's note of its file
    // is not one of these: it is kept for timers nobody owns too, and goes when the recording's
    // item has taken the owners or its allowance lapses.
    private static bool PruneSideTables(OwnershipStoreData data)
    {
        var changed = false;
        foreach (var key in data.TimerPathsUntil.Keys.Where(k => !data.TimerPaths.ContainsKey(k)).ToList())
        {
            changed |= data.TimerPathsUntil.Remove(key);
        }

        foreach (var key in data.TimerServiceIds.Keys.Where(k => !data.Timers.ContainsKey(k)).ToList())
        {
            changed |= data.TimerServiceIds.Remove(key);
        }

        foreach (var key in data.TimersMissingSince.Keys.Where(k => !data.Timers.ContainsKey(k)).ToList())
        {
            changed |= data.TimersMissingSince.Remove(key);
        }

        foreach (var key in data.SeriesTimersMissingSince.Keys.Where(k => !data.SeriesTimers.ContainsKey(k)).ToList())
        {
            changed |= data.SeriesTimersMissingSince.Remove(key);
        }

        return changed;
    }

    // Keeps the note of which timer is writing which file up to date from a listing. A timer that
    // is recording now (in progress with its window still open), owned or not, has its file noted.
    // A noted timer that no longer is has the note kept for the post-recording allowance, counted
    // from the end of its window if that is later than now. A timer the listing no longer has is
    // only taken as stopped when the server also says it writes nothing, since a listing can fail
    // while a recording carries on.
    private bool UpdateRecordingMap(OwnershipStoreData data, IReadOnlyList<ScheduledTimer> timers)
    {
        var now = _utcNow();
        var changed = false;
        var recording = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var timer in timers)
        {
            var key = ApiIdOf(timer);
            if (string.IsNullOrEmpty(key) || !IsRecordingNow(timer, now))
            {
                continue;
            }

            recording.Add(key);
            var path = string.IsNullOrEmpty(timer.TimerId) ? null : _host.GetActiveRecordingPath(timer.TimerId);
            if (!string.IsNullOrEmpty(path))
            {
                _recordingServiceIds[key] = timer.TimerId;
                changed |= NoteRecording(data, key, path!);
            }
        }

        foreach (var entry in data.TimerPaths.ToList())
        {
            if (recording.Contains(entry.Key) || data.TimerPathsUntil.ContainsKey(entry.Key))
            {
                continue;
            }

            DateTime stoppedAt;
            if (_timers.TryGetValue(entry.Key, out var listed))
            {
                var windowEnd = ToUtc(listed.WindowEnd);
                stoppedAt = windowEnd > now ? windowEnd : now;
            }
            else if (!string.IsNullOrEmpty(ActivePathOf(entry.Key)))
            {
                continue;
            }
            else
            {
                stoppedAt = now;
            }

            data.TimerPathsUntil[entry.Key] = stoppedAt + PostRecordingAllowance;
            changed = true;
        }

        return changed;
    }

    // What the server says an unlisted, noted timer is writing right now; null when nothing.
    private string? ActivePathOf(string key)
    {
        var serviceId = _recordingServiceIds.TryGetValue(key, out var known)
            ? known
            : _data != null && _data.TimerServiceIds.TryGetValue(key, out var stored) ? stored : key;
        return _host.GetActiveRecordingPath(serviceId);
    }

    // Notes that a timer is recording to a path. Another timer's stopped note for the same path
    // is dropped, since the file is this timer's now. True when the notes changed.
    private bool NoteRecording(OwnershipStoreData data, string key, string path)
    {
        var changed = false;
        foreach (var other in data.TimerPaths.ToList())
        {
            if (!string.Equals(other.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.Value, path, StringComparison.OrdinalIgnoreCase)
                && data.TimerPathsUntil.ContainsKey(other.Key))
            {
                FinishEntry(data, other.Key);
                changed = true;
            }
        }

        if (!data.TimerPaths.TryGetValue(key, out var known) || !string.Equals(known, path, StringComparison.OrdinalIgnoreCase))
        {
            data.TimerPaths[key] = path;
            data.TimerPathsUntil.Remove(key);
            return true;
        }

        return data.TimerPathsUntil.Remove(key) | changed;
    }

    // Drops a timer's note of its file, and with it the timer's owners: it has stopped, and its
    // item has them or never will.
    private void FinishEntry(OwnershipStoreData data, string key)
    {
        data.TimerPaths.Remove(key);
        data.TimerPathsUntil.Remove(key);
        _recordingServiceIds.Remove(key);
        if (data.Timers.Remove(key))
        {
            data.TimersMissingSince.Remove(key);
            data.TimerServiceIds.Remove(key);
            _logger.Info("Forgot the owners of timer {TimerId}, whose recording has been handed over or never arrived", key);
        }
    }

    // The timer noted as writing the file at a path, or null. One still recording wins over one
    // that stopped; a stopped timer's note whose allowance has lapsed no longer counts.
    private string? FindEntryForPath(OwnershipStoreData data, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var now = _utcNow();
        string? stopped = null;
        foreach (var entry in data.TimerPaths)
        {
            if (!string.Equals(entry.Value, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!data.TimerPathsUntil.TryGetValue(entry.Key, out var until))
            {
                return entry.Key;
            }

            if (until > now)
            {
                stopped ??= entry.Key;
            }
        }

        return stopped;
    }

    // The timer noted as writing an item's file, whose note hasn't lapsed; null when none.
    private string? FindEntryForItem(OwnershipStoreData data, Guid itemId)
    {
        foreach (var entry in data.TimerPaths)
        {
            if (FindEntryForPath(data, entry.Value) == entry.Key && _host.FindItemByPath(entry.Value)?.Id == itemId)
            {
                return entry.Key;
            }
        }

        return null;
    }

    // Makes an item's owners exactly the owners of the timer recording it (none for a timer nobody
    // owns), removing the tombstone it may have, saving before the tags are written, and stamping
    // the tag of each owner gained and removing that of each lost. Does nothing, and saves nothing,
    // when the owners already match. True when anything changed.
    private async Task<bool> ApplyTimerOwnersAsync(OwnershipStoreData data, Guid itemId, string timerKey, CancellationToken ct)
    {
        var target = data.Timers.TryGetValue(timerKey, out var timerOwners) ? timerOwners.ToList() : new List<Guid>();
        var current = data.Items.TryGetValue(itemId, out var itemOwners) ? itemOwners.ToList() : new List<Guid>();
        var gained = target.Where(o => !current.Contains(o)).ToList();
        var lost = current.Where(o => !target.Contains(o)).ToList();
        var tombstoned = data.Removed.Remove(itemId);
        if (gained.Count == 0 && lost.Count == 0 && !tombstoned)
        {
            return false;
        }

        if (target.Count == 0)
        {
            data.Items.Remove(itemId);
        }
        else
        {
            data.Items[itemId] = target;
        }

        Save(data);
        if (gained.Count > 0 || lost.Count > 0)
        {
            _logger.Info("Recording {ItemId} from timer {TimerId}: {Gained} owner(s) gained, {Lost} lost", itemId, timerKey, gained.Count, lost.Count);
        }

        foreach (var owner in lost)
        {
            var name = NameOf(owner);
            if (!string.IsNullOrEmpty(name) && !SharesName(target, owner, name!))
            {
                await SetTagAsync(itemId, TagPrefix + name, false, ct).ConfigureAwait(false);
            }
        }

        await StampTagsAsync(itemId, gained, ct).ConfigureAwait(false);
        return true;
    }

    // Walks the noted files: a timer still recording gives its owners to the item at its path,
    // every pass; one that has stopped gives them once, when its item is first found, and is then
    // forgotten; one whose allowance has lapsed is forgotten without. Saves before each item's tag
    // writes. True when anything changed.
    private async Task<bool> ApplyRecordingsAsync(OwnershipStoreData data, CancellationToken ct)
    {
        var now = _utcNow();
        var changed = false;
        var finished = false;
        foreach (var entry in data.TimerPaths.ToList())
        {
            if (!data.TimerPaths.ContainsKey(entry.Key))
            {
                continue;
            }

            var stopped = data.TimerPathsUntil.TryGetValue(entry.Key, out var until);
            if (stopped && until <= now)
            {
                FinishEntry(data, entry.Key);
                changed = finished = true;
                continue;
            }

            var item = _host.FindItemByPath(entry.Value);
            if (item == null)
            {
                continue;
            }

            changed |= await ApplyTimerOwnersAsync(data, item.Id, entry.Key, ct).ConfigureAwait(false);
            if (stopped)
            {
                FinishEntry(data, entry.Key);
                changed = finished = true;
            }
        }

        if (finished)
        {
            Save(data);
        }

        return changed;
    }

    // Owned recordings the library definitely no longer has become tombstones, as when the server
    // reports their removal. An item the host can't be asked about is left alone.
    private bool ReconcileItems(OwnershipStoreData data, DateTime now)
    {
        var changed = false;
        foreach (var entry in data.Items.ToList())
        {
            if (!IsGone(entry.Key))
            {
                continue;
            }

            data.Items.Remove(entry.Key);
            data.Removed[entry.Key] = new RemovedItem { Owners = entry.Value, RemovedAt = now };
            changed = true;
            _logger.Info("Recording {ItemId} is no longer in the library; keeping its owners for {Days} days in case it comes back", entry.Key, RemovedRetention.TotalDays);
        }

        return changed;
    }

    // GetTags is null only when the library has no such item; a lookup that throws can't tell.
    private bool IsGone(Guid itemId)
    {
        try
        {
            return _host.GetTags(itemId) == null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not check whether recording {ItemId} is still in the library", itemId);
            return false;
        }
    }

    private string? NameOf(Guid userId) =>
        TryGetUser(userId)?.Name ?? (_userNames.TryGetValue(userId, out var known) ? known : null);

    private static DateTime ToUtc(DateTime time) => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;

    // Whether a timer is writing its file now: in progress with its window still open. Past its
    // window a timer counts as stopped, whatever an older listing says.
    private static bool IsRecordingNow(ScheduledTimer timer, DateTime now) =>
        timer.Status == StatusInProgress && ToUtc(timer.WindowEnd) > now;

    // A timer under a rule gets the rule's owners. Owners are never taken away when the server
    // moves a timer to another rule, so a newer duplicate rule can't strip anyone's showings.
    private bool ApplySeriesOwners(OwnershipStoreData data, IReadOnlyList<ScheduledTimer> timers)
    {
        var changed = false;
        foreach (var timer in timers)
        {
            if (timer.Status != StatusCancelled
                && !string.IsNullOrEmpty(timer.SeriesTimerId)
                && data.SeriesTimers.TryGetValue(timer.SeriesTimerId!, out var owners)
                && AddOwners(data.Timers, ApiIdOf(timer), owners))
            {
                Track(data, timer);
                changed = true;
            }
        }

        return changed;
    }

    // Whether the caller can see at least one program the rule is recording. A rule with nothing
    // scheduled (between seasons, say) is judged by the program it was made from or its channel.
    // The channel never overrides programs the caller can't see (a parental rating, say).
    private bool CanSeeRule(SeriesRule rule, IReadOnlyList<ScheduledTimer> timers, Guid userId)
    {
        bool Visible(string? programId) =>
            !string.IsNullOrEmpty(programId)
            && _host.GetProgram(programId!) is { } program
            && _host.IsVisibleTo(program.ItemId, userId);

        var scheduled = timers
            .Where(t => t.Status != StatusCancelled && string.Equals(t.SeriesTimerId, rule.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (scheduled.Count > 0)
        {
            return scheduled.Any(t => Visible(t.ProgramId));
        }

        return Visible(rule.ProgramId)
            || (Guid.TryParse(rule.ChannelId, out var channelId) && _host.IsVisibleTo(channelId, userId));
    }

    // The rule that already records a program's series: the one with a timer for this very
    // program, else the one holding most of the series' timers (the rule the server evaluated
    // last), else one with the series' id (Emby only), else one named after the series.
    private string? FindRuleForSeries(GuideProgram program, IReadOnlyList<ScheduledTimer> timers, IReadOnlyList<SeriesRule> rules)
    {
        var ruleIds = rules.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ruled = timers
            .Where(t => t.Status != StatusCancelled && !string.IsNullOrEmpty(t.SeriesTimerId) && ruleIds.Contains(t.SeriesTimerId!))
            .ToList();

        var direct = ruled.FirstOrDefault(t => string.Equals(t.ProgramId, program.Id, StringComparison.OrdinalIgnoreCase));
        if (direct != null)
        {
            return direct.SeriesTimerId;
        }

        var programs = new Dictionary<string, GuideProgram?>(StringComparer.OrdinalIgnoreCase);
        GuideProgram? ProgramById(string? programId)
        {
            if (string.IsNullOrEmpty(programId))
            {
                return null;
            }

            if (!programs.TryGetValue(programId!, out var known))
            {
                known = _host.GetProgram(programId!);
                programs[programId!] = known;
            }

            return known;
        }

        GuideProgram? ProgramOf(ScheduledTimer t) => ProgramById(t.ProgramId);

        // A rule known to be for another series isn't this one, whatever it or its programs are
        // called: by its own series id (Emby), or by the series id of any program it records
        // (Jellyfin rules have none).
        var otherSeries = new HashSet<string>(
            rules.Where(r => DifferentIds(r.SeriesId, program.SeriesId)).Select(r => r.Id)
                .Concat(ruled.Where(t => ProgramOf(t) is { } recorded && DifferentIds(recorded.SeriesId, program.SeriesId)).Select(t => t.SeriesTimerId!)),
            StringComparer.OrdinalIgnoreCase);

        var busiest = ruled
            .Where(t => !otherSeries.Contains(t.SeriesTimerId!) && ProgramOf(t) is { } other && SameSeries(other, program))
            .GroupBy(t => t.SeriesTimerId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (busiest != null)
        {
            return busiest.Key;
        }

        if (!string.IsNullOrEmpty(program.SeriesId)
            && rules.FirstOrDefault(r => string.Equals(r.SeriesId, program.SeriesId, StringComparison.OrdinalIgnoreCase)) is { } bySeriesId)
        {
            return bySeriesId.Id;
        }

        // Two series can share a name, so a name alone isn't enough: the rule must also be shown to
        // be this series (its own program is of it) or to record where this program airs.
        return string.IsNullOrEmpty(program.SeriesName)
            ? null
            : rules.FirstOrDefault(r => string.Equals(r.Name, program.SeriesName, StringComparison.OrdinalIgnoreCase)
                && !otherSeries.Contains(r.Id)
                && NameMatchIsCorroborated(r, program, ProgramById))?.Id;
    }

    // Whether a rule matched to a program by name is backed by something else. Its own program
    // settles it when that still resolves (same series: yes, any other: no); otherwise the rule
    // must record every channel or the program's channel.
    private static bool NameMatchIsCorroborated(SeriesRule rule, GuideProgram program, Func<string?, GuideProgram?> lookup)
    {
        if (lookup(rule.ProgramId) is { } own)
        {
            if (DifferentIds(own.SeriesId, program.SeriesId))
            {
                return false;
            }

            if (SameSeries(own, program))
            {
                return true;
            }
        }

        return rule.RecordAnyChannel
            || (!string.IsNullOrEmpty(rule.ChannelId)
                && string.Equals(rule.ChannelId, program.ChannelId, StringComparison.OrdinalIgnoreCase));
    }

    // Whether two programs are of one series: by id when both have one, else by name. Two series
    // can share a name, so a name never overrides ids that differ.
    private static bool SameSeries(GuideProgram a, GuideProgram b) =>
        !string.IsNullOrEmpty(a.SeriesId) && !string.IsNullOrEmpty(b.SeriesId)
            ? string.Equals(a.SeriesId, b.SeriesId, StringComparison.OrdinalIgnoreCase)
            : !string.IsNullOrEmpty(b.SeriesName) && string.Equals(a.SeriesName, b.SeriesName, StringComparison.OrdinalIgnoreCase);

    private static bool DifferentIds(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Owners as clients show them. Deleted and disabled users are left out.
    private IReadOnlyList<OwnerInfo> ToOwnerInfos<TKey>(Dictionary<TKey, List<Guid>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var owners))
        {
            return Array.Empty<OwnerInfo>();
        }

        var result = new List<OwnerInfo>(owners.Count);
        foreach (var owner in owners)
        {
            if (TryGetUser(owner) is { IsDisabled: false } user)
            {
                result.Add(new OwnerInfo(user.Id, user.Name, user.PrimaryImageTag));
            }
        }

        return result;
    }

    // A host that throws can't tell, which is never taken as "not a recording".
    private bool? IsRecording(Guid itemId, Guid userId)
    {
        try
        {
            return _host.IsLiveTvRecording(itemId, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not check whether item {ItemId} is a Live TV recording", itemId);
            return null;
        }
    }

    // The given items that are Live TV recordings the caller may see, or null when any of them
    // can't be told apart, so a client never reads "unknown" as "not a recording".
    private IReadOnlyList<Guid>? RecordingsVisibleTo(IReadOnlyCollection<Guid> itemIds, Guid callerId)
    {
        var recordings = new List<Guid>();
        foreach (var itemId in itemIds.Distinct())
        {
            switch (IsRecording(itemId, callerId))
            {
                case null:
                    return null;
                case true:
                    recordings.Add(itemId);
                    break;
            }
        }

        if (recordings.Count == 0)
        {
            return recordings;
        }

        try
        {
            var visible = _host.VisibleTo(recordings, callerId);
            return recordings.Where(visible.Contains).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not check which recordings user {UserId} may see", callerId);
            return null;
        }
    }

    // A user, or null when there is none or the server failed to read it.
    private ServerUser? TryGetUser(Guid userId)
    {
        try
        {
            return _host.GetUser(userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not read user {UserId}", userId);
            return null;
        }
    }

    // Only a user the server definitely doesn't have; a lookup that fails or can't tell keeps them.
    private bool UserIsGone(Guid userId)
    {
        try
        {
            return _host.UserExists(userId) == false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not check whether user {UserId} still exists; keeping them as an owner", userId);
            return false;
        }
    }

    // Whether another owner in the set goes by the same name, so a shared tag must stay.
    private bool SharesName(List<Guid> owners, Guid userId, string name) =>
        owners.Any(o => o != userId && _userNames.TryGetValue(o, out var other) && string.Equals(other, name, StringComparison.OrdinalIgnoreCase));

    private async Task StampTagsAsync(Guid itemId, IEnumerable<Guid> owners, CancellationToken ct)
    {
        foreach (var owner in owners)
        {
            var name = TryGetUser(owner)?.Name ?? (_userNames.TryGetValue(owner, out var known) ? known : null);
            if (!string.IsNullOrEmpty(name))
            {
                await SetTagAsync(itemId, TagPrefix + name, true, ct).ConfigureAwait(false);
            }
        }
    }

    // Tags are a readable backup, not the source of truth, so failing to write one is logged only.
    private async Task<bool> SetTagAsync(Guid itemId, string tag, bool present, CancellationToken ct)
    {
        // While a newer store is in use, owners live only in memory; tags would outlive them.
        if (_readOnly)
        {
            return false;
        }

        try
        {
            return await _host.SetTagAsync(itemId, tag, present, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.Warn(ex, "Could not {Action} tag {Tag} on recording {ItemId}", present ? "add" : "remove", tag, itemId);
            return false;
        }
    }

    private OwnershipStoreData Load()
    {
        if (_data != null)
        {
            return _data;
        }

        // A user list that can't be read only costs the names a rename or delete would look up;
        // it must not keep the store from loading, or every owner call would fail.
        try
        {
            foreach (var user in _host.GetUsers())
            {
                _userNames[user.Id] = user.Name;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not list the server's users while loading recording owners");
        }

        // A newer file is accepted even when this version can't parse it, so the state file never
        // falls back to promoting the .bak over it and nothing here rebuilds over it.
        var unreadable = false;
        var read = _stateFile.Read(_storePath, json => ParseStore(json, () => unreadable = true));
        if (read != null)
        {
            _data = Normalize(read);
            if (_unbuilt != null)
            {
                // The file became readable again before a rebuild could run: what calls changed
                // meanwhile is added to it (and, for a newer file, kept until restart).
                MergeInto(_data, _unbuilt);
                _unbuilt = null;
                if (read.Version <= CurrentVersion)
                {
                    Save(_data);
                }
            }

            if (read.Version > CurrentVersion)
            {
                _readOnly = true;
                _logger.Warn(
                    null,
                    unreadable
                        ? "The recording owners file {Path} is version {Version}, which this plugin (version {Current}) can't read; recording owners are unavailable until the plugin is upgraded, and the file is left untouched"
                        : "The recording owners file {Path} is version {Version}, newer than this plugin's {Current}; it is used but not saved over, and no owner tags are written, so changes to recording owners last until restart",
                    _storePath,
                    read.Version,
                    CurrentVersion);
            }

            return _data;
        }

        var fresh = new OwnershipStoreData();

        // Should the read have failed for another reason, a newer file is still never rebuilt over.
        var newest = Math.Max(PeekFileVersion(_storePath) ?? 0, PeekFileVersion(_storePath + BackupSuffix) ?? 0);
        if (newest > CurrentVersion)
        {
            fresh.Version = newest;
            _readOnly = true;
            _logger.Warn(null, "The recording owners file {Path} could not be read but is version {Version}, newer than this plugin's {Current}; recording owners are unavailable until the plugin is upgraded, and the file is left untouched", _storePath, newest, CurrentVersion);
            return _data = fresh;
        }

        // Read returns null when there is no file yet, when the file and its backup are both
        // unusable, and when both were deleted. Recordings tagged with owners mean the store was
        // lost, so their owners are rebuilt from the tags; a first start finds none. The store is
        // only kept once the rebuild has finished, so a lookup that fails for the moment is tried
        // again on the next call rather than leaving an empty store to be saved over the tags.
        var unusable = File.Exists(_storePath) || File.Exists(_storePath + BackupSuffix);
        try
        {
            Rebuild(fresh, unusable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Users or the library not ready yet (a startup): calls go on with a stand-in that
            // holds what they change, nothing is saved over the files the rebuild needs, and the
            // next call rebuilds again, keeping what the stand-in gathered.
            _logger.Warn(ex, "Could not rebuild recording owners from their tags yet; trying again on the next request");
            return _unbuilt ??= new OwnershipStoreData();
        }

        var merged = _unbuilt != null;
        if (merged)
        {
            MergeInto(fresh, _unbuilt!);
            _unbuilt = null;
        }

        if (unusable || merged || fresh.Items.Count > 0)
        {
            // The unusable copies are set aside before anything overwrites them, so a read that
            // failed only for the moment can't lose the owners for good.
            _preserveUnusable = unusable;
            Save(fresh);
        }

        return _data = fresh;
    }

    // Parses the store. A file newer than this build that it can't parse becomes an empty
    // placeholder carrying the file's version, so it reads as newer rather than as corrupt.
    private static OwnershipStoreData? ParseStore(string json, Action markUnreadable)
    {
        if (PeekVersion(json) is { } version && version > CurrentVersion)
        {
            try
            {
                if (JsonSerializer.Deserialize<OwnershipStoreData>(json, JsonOptions) is { } parsed)
                {
                    return parsed;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                // Its shape changed; the placeholder below keeps it untouched.
            }

            markUnreadable();
            return new OwnershipStoreData { Version = version };
        }

        return JsonSerializer.Deserialize<OwnershipStoreData>(json, JsonOptions);
    }

    // The store's top-level Version, or null when the text isn't a JSON object with one.
    private static int? PeekVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(nameof(OwnershipStoreData.Version), out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var version)
                    ? version
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? PeekFileVersion(string path)
    {
        try
        {
            return File.Exists(path) ? PeekVersion(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Copies the store file and its backup to "<name>.corrupt-<stamp>". True once done, or when
    // there was nothing to copy.
    private bool PreserveUnusableFiles()
    {
        var stamp = _utcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            foreach (var path in new[] { _storePath, _storePath + BackupSuffix })
            {
                if (File.Exists(path))
                {
                    var aside = path + ".corrupt-" + stamp;
                    File.Copy(path, aside, overwrite: true);
                    _logger.Warn(null, "Kept the unusable recording owners file {Path} as {Copy}", path, aside);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Could not keep a copy of the unusable recording owners file {Path}; not overwriting it until a copy succeeds", _storePath);
            return false;
        }
    }

    // Deserializing replaces the dictionaries with ones that compare ids case-sensitively and
    // leaves explicit nulls, so both are put right here.
    private static OwnershipStoreData Normalize(OwnershipStoreData read) => new()
    {
        Version = Math.Max(read.Version, CurrentVersion),
        Timers = Copy(read.Timers, StringComparer.OrdinalIgnoreCase),
        SeriesTimers = Copy(read.SeriesTimers, StringComparer.OrdinalIgnoreCase),
        Items = Copy(read.Items, EqualityComparer<Guid>.Default),
        TimerPaths = CopyStrings(read.TimerPaths),
        TimerPathsUntil = new Dictionary<string, DateTime>(read.TimerPathsUntil ?? new Dictionary<string, DateTime>(), StringComparer.OrdinalIgnoreCase),
        Removed = (read.Removed ?? new Dictionary<Guid, RemovedItem>())
            .Where(e => e.Value?.Owners is { Count: > 0 })
            .ToDictionary(e => e.Key, e => e.Value),
        TimerServiceIds = CopyStrings(read.TimerServiceIds),
        TimersMissingSince = new Dictionary<string, DateTime>(read.TimersMissingSince ?? new Dictionary<string, DateTime>(), StringComparer.OrdinalIgnoreCase),
        SeriesTimersMissingSince = new Dictionary<string, DateTime>(read.SeriesTimersMissingSince ?? new Dictionary<string, DateTime>(), StringComparer.OrdinalIgnoreCase),
        Extra = read.Extra,
    };

    private static Dictionary<string, string> CopyStrings(Dictionary<string, string>? source) =>
        new(
            (source ?? new Dictionary<string, string>()).Where(e => !string.IsNullOrEmpty(e.Value)).ToDictionary(e => e.Key, e => e.Value),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<TKey, List<Guid>> Copy<TKey>(Dictionary<TKey, List<Guid>>? source, IEqualityComparer<TKey> comparer)
        where TKey : notnull
    {
        var copy = new Dictionary<TKey, List<Guid>>(comparer);
        if (source != null)
        {
            foreach (var entry in source)
            {
                if (entry.Value is { Count: > 0 })
                {
                    AddOwners(copy, entry.Key, entry.Value);
                }
            }
        }

        return copy;
    }

    // Adds what calls changed while a rebuild couldn't run to the rebuilt store. Owners are only
    // ever added; a recording that left the library meanwhile goes to the removed list.
    private static void MergeInto(OwnershipStoreData rebuilt, OwnershipStoreData changes)
    {
        foreach (var entry in changes.Timers)
        {
            AddOwners(rebuilt.Timers, entry.Key, entry.Value);
        }

        foreach (var entry in changes.SeriesTimers)
        {
            AddOwners(rebuilt.SeriesTimers, entry.Key, entry.Value);
        }

        foreach (var entry in changes.Items)
        {
            AddOwners(rebuilt.Items, entry.Key, entry.Value);
        }

        foreach (var entry in changes.Removed)
        {
            if (rebuilt.Items.TryGetValue(entry.Key, out var tagged))
            {
                rebuilt.Items.Remove(entry.Key);
                entry.Value.Owners = entry.Value.Owners.Union(tagged).ToList();
            }

            rebuilt.Removed[entry.Key] = entry.Value;
        }

        foreach (var entry in changes.TimerPaths)
        {
            rebuilt.TimerPaths[entry.Key] = entry.Value;
        }

        foreach (var entry in changes.TimerPathsUntil)
        {
            rebuilt.TimerPathsUntil[entry.Key] = entry.Value;
        }

        foreach (var entry in changes.TimerServiceIds)
        {
            rebuilt.TimerServiceIds[entry.Key] = entry.Value;
        }

        foreach (var entry in changes.TimersMissingSince)
        {
            rebuilt.TimersMissingSince[entry.Key] = entry.Value;
        }

        foreach (var entry in changes.SeriesTimersMissingSince)
        {
            rebuilt.SeriesTimersMissingSince[entry.Key] = entry.Value;
        }
    }

    // Rebuilds the recordings' owners from their "Recorded by name" tags. Timer and rule owners
    // have no backup and are lost.
    private void Rebuild(OwnershipStoreData data, bool unusable)
    {
        var tagged = _host.FindItemsWithTagPrefix(TagPrefix);
        if (tagged.Count == 0 && !unusable)
        {
            // A first start: nothing to rebuild, so the users needn't be listed.
            return;
        }

        var users = _host.GetUsers();
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tagged)
        {
            foreach (var tag in item.Tags.Where(t => t.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var name = tag.Substring(TagPrefix.Length).Trim();
                var matches = users.Where(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var user = matches.Count == 1 ? matches[0] : matches.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
                if (user == null)
                {
                    unmatched.Add(name);
                    continue;
                }

                AddOwners(data.Items, item.Id, new[] { user.Id });
            }
        }

        foreach (var name in unmatched)
        {
            _logger.Warn(null, "Rebuilding recording owners: no single user is named {Name}, so recordings tagged for them stay without that owner", name);
        }

        if (unusable)
        {
            _logger.Warn(null, "The recording owners file {Path} and its backup were unusable; rebuilt the owners of {Count} recording(s) from their tags. Timer and series rule owners are lost", _storePath, data.Items.Count);
        }
        else if (data.Items.Count > 0)
        {
            _logger.Warn(null, "The recording owners file {Path} and its backup are missing, but recordings carry owner tags; rebuilt the owners of {Count} recording(s) from them. Timer and series rule owners are lost", _storePath, data.Items.Count);
        }
    }

    private void Save(OwnershipStoreData data)
    {
        if (_readOnly || ReferenceEquals(data, _unbuilt))
        {
            return;
        }

        if (_preserveUnusable)
        {
            if (!PreserveUnusableFiles())
            {
                return;
            }

            _preserveUnusable = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            _stateFile.Write(_storePath, JsonSerializer.Serialize(data, JsonOptions));
            _unsaved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            _logger.Error(ex, "Could not save recording owners to {Path}; the next sync tries again, and changes since the last save are lost if the server restarts first", _storePath);
        }
    }
}
