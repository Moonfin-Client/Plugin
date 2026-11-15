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
/// events or once it is gone and its recording has been attached.
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
    public const string TagPrefix = "Recorded by ";

    /// <summary>Whether a tag is one of the owner tags this feature stamps on recordings.</summary>
    public static bool IsOwnerTag(string? tag) =>
        tag != null && tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An item's tags without the owner tags. Those say who recorded it, not what it is, so
    /// similar-items scoring and smart search leave them out.
    /// </summary>
    public static IReadOnlyList<string> WithoutOwnerTags(IEnumerable<string>? tags) =>
        tags == null ? Array.Empty<string>() : tags.Where(t => !IsOwnerTag(t)).ToList();

    /// <summary>How long a removed recording's owners are kept in case the item comes back.</summary>
    public static readonly TimeSpan RemovedRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// How much later than its removal a returning file may have been written and still count as
    /// the same recording, for clock drift between the server and the storage holding the file.
    /// </summary>
    public static readonly TimeSpan FileClockTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after a finished timer's window its file may still be written (a post-processing
    /// remux, say) and still be that timer's recording. A file written later at the same path is a
    /// new recording, a rerun, that the finished timer must not claim.
    /// </summary>
    public static readonly TimeSpan PostRecordingAllowance = TimeSpan.FromHours(1);

    // At or before this a file time means none: .NET reports 1601-01-01 for a file it can't stat.
    private static readonly DateTime UnknownFileTime = new(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc);

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

    // The library item each owned timer's recording became, as seen by this process, so removing
    // that item can forget the finished timer instead of leaving it to claim the path's next file.
    private readonly Dictionary<string, Guid> _timerItems = new(StringComparer.OrdinalIgnoreCase);

    private OwnershipStoreData? _data;

    // Set when the store was rebuilt from tags: the unusable file must be copied aside before the
    // first save overwrites it.
    private bool _preserveUnusable;

    // Set when the store came from a newer plugin, which must find it as it left it.
    private bool _readOnly;
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
                await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
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
            await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
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
            await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
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
                    if (timer == null || timer.Status == StatusCancelled)
                    {
                        return null;
                    }

                    var timerId = ApiIdOf(timer);
                    AddOwners(data.Timers, timerId, new[] { userId });
                    Track(data, timer);
                    Save(data);
                    await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
                    _logger.Info("User {UserId} claimed timer {TimerId}", userId, timerId);
                    return ToOwnerInfos(data.Timers, timerId);
                }

                case OwnedKind.Series:
                {
                    var rules = await _host.GetSeriesTimersAsync(ct).ConfigureAwait(false);
                    var rule = rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (rule == null)
                    {
                        return null;
                    }

                    AddOwners(data.SeriesTimers, rule.Id, new[] { userId });
                    ApplySeriesOwners(data, await RefreshTimersAsync(ct).ConfigureAwait(false));
                    Save(data);
                    await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
                    _logger.Info("User {UserId} claimed series rule {RuleId}", userId, rule.Id);
                    return ToOwnerInfos(data.SeriesTimers, rule.Id);
                }

                case OwnedKind.Item:
                {
                    // Only a Live TV recording can be claimed; a server that can't tell allows it.
                    if (!Guid.TryParse(id, out var itemId) || _host.GetTags(itemId) == null || IsRecording(itemId) == false)
                    {
                        return null;
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
    /// file each owned timer is writing, forgets timers and rules the server is done with, and
    /// purges old tombstones.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
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

            // Jellyfin adds a recording's item when the recording starts, usually before the file
            // is captured here, so the item's ItemAdded found no owners. Attach them now, before a
            // finished timer is forgotten below.
            changed |= await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);

            // A timer is forgotten once the server is done with it and its recording has reached
            // the library; until then the item may still arrive and need the owners. A done timer
            // whose recording isn't in the library (deleted, or never scanned in) is forgotten
            // after the retention, counted in TimersMissingSince from the first sync that found it
            // so; kept, it would claim whatever is recorded to its path next. One without a
            // recorded file is forgotten only after the server has left it out for the whole
            // retention, since a listing can miss timers while a Live TV service is failing.
            foreach (var key in data.Timers.Keys.ToList())
            {
                var listed = _timers.TryGetValue(key, out var timer);
                if (data.TimerPaths.TryGetValue(key, out var path))
                {
                    var done = !listed || timer!.Status == StatusCancelled || timer.Status == StatusCompleted;
                    if (!done)
                    {
                        changed |= data.TimersMissingSince.Remove(key);
                    }
                    else if (_host.FindItemByPath(path) != null)
                    {
                        data.Timers.Remove(key);
                        changed = true;
                    }
                    else if (!data.TimersMissingSince.TryGetValue(key, out var doneSince))
                    {
                        data.TimersMissingSince[key] = now;
                        changed = true;
                    }
                    else if (doneSince <= cutoff)
                    {
                        data.Timers.Remove(key);
                        _logger.Info("Forgot finished timer {TimerId}, whose recording hasn't been in the library since {Since}", key, doneSince);
                        changed = true;
                    }
                }
                else if (listed)
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
            foreach (var key in _timerItems.Keys.Where(k => !data.Timers.ContainsKey(k)).ToList())
            {
                _timerItems.Remove(key);
            }

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
    /// A timer was cancelled, which is for everyone, so its owners are forgotten. A recording that
    /// was stopped partway still reaches the library, so a timer whose file is known keeps its
    /// owners until that item arrives. The server raises the cancel while the recording is still
    /// running, so the watcher reads the file then and passes it as <paramref name="activePath"/>;
    /// an item already in the library for it takes the owners at once.
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
            if (!data.Timers.ContainsKey(key))
            {
                return;
            }

            if (!string.IsNullOrEmpty(activePath)
                && !(data.TimerPaths.TryGetValue(key, out var known) && string.Equals(known, activePath, StringComparison.OrdinalIgnoreCase)))
            {
                data.TimerPaths[key] = activePath!;
                Save(data);
                _logger.Info("Cancelled timer {TimerId} was recording {Path}; keeping its owners for that recording", key, activePath);
            }

            if (data.TimerPaths.ContainsKey(key))
            {
                await AttachToStartedRecordingsAsync(data, ct).ConfigureAwait(false);
                return;
            }

            data.Timers.Remove(key);
            PruneSideTables(data);
            Save(data);
            _logger.Info("Forgot the owners of cancelled timer {TimerId}", key);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>As <see cref="OnTimerCancelledAsync(string, string, CancellationToken)"/> with no file known.</summary>
    public Task OnTimerCancelledAsync(string timerId, CancellationToken ct) => OnTimerCancelledAsync(timerId, null, ct);

    /// <summary>
    /// A library item arrived. A recording written by an owned timer takes that timer's owners;
    /// otherwise a recording that left the library within the retention gets its owners back,
    /// unless its file was written after it left (a new recording on the same path). Each owner's
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
            var hasTombstone = data.Removed.TryGetValue(item.Id, out var removed);
            var timerId = FindTimerForPath(data, item.Path);
            var writtenAt = hasTombstone || (timerId != null && !IsLive(timerId)) ? FileWrittenAt(item.Id) : null;

            // The server derives an item's id from its path, so a rerun recorded to the file of a
            // deleted recording comes back as that recording. Jellyfin adds the item as the
            // recording starts, usually before a sync has seen its file, so the timers are listed
            // again before the old owners, or a finished timer's, are taken back. A file known to
            // be older than the removal is the old recording coming back, which no listing can
            // change, so a storage outage's many returning items list nothing.
            var returned = hasTombstone && writtenAt is { } written && written <= removed!.RemovedAt + FileClockTolerance;
            if (hasTombstone && !returned && !string.IsNullOrEmpty(item.Path) && (timerId == null || !IsLive(timerId)))
            {
                try
                {
                    foreach (var timer in await RefreshTimersAsync(ct).ConfigureAwait(false))
                    {
                        Track(data, timer);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Live TV may not be ready yet (a startup scan); decide from the last listing.
                    _logger.Warn(ex, "Could not list the timers while recording {ItemId} came back; deciding its owners from the last listing", item.Id);
                }

                timerId = FindTimerForPath(data, item.Path);
            }

            // A finished timer whose window ended well before this file was written didn't record it.
            if (timerId != null && IsStaleMatch(data, timerId, writtenAt, removed))
            {
                _logger.Info("Recording {ItemId} was written at {WrittenAt}, after finished timer {TimerId} that recorded its path; that timer's owners don't apply", item.Id, writtenAt, timerId);
                timerId = null;
            }

            List<Guid> owners;
            if (timerId != null)
            {
                _timerItems[timerId] = item.Id;
                owners = data.Timers[timerId].ToList();
                _logger.Info("Recording {ItemId} from timer {TimerId} took its {Count} owner(s)", item.Id, timerId, owners.Count);
                if (hasTombstone)
                {
                    // A new recording on the old one's file: the old owners must not come back with it.
                    data.Removed.Remove(item.Id);
                    _logger.Info("Discarded the owners kept for removed recording {ItemId}, since timer {TimerId} recorded it again", item.Id, timerId);
                }
            }
            else if (hasTombstone && writtenAt is { } newer && newer > removed!.RemovedAt + FileClockTolerance)
            {
                // A file written after its recording left the library is a new recording on the
                // old one's path (a rerun by a timer nobody owns, or one not yet seen recording),
                // not the old file coming back, so the old owners stay gone.
                data.Removed.Remove(item.Id);
                Save(data);
                _logger.Info(
                    "Recording {ItemId} came back with a file written at {WrittenAt}, after it left the library at {RemovedAt}; it is a new recording, so the owners kept for the old one were discarded",
                    item.Id,
                    newer,
                    removed!.RemovedAt);
                return;
            }
            else if (hasTombstone && removed!.RemovedAt >= _utcNow() - RemovedRetention)
            {
                owners = removed.Owners;
                data.Removed.Remove(item.Id);
                _logger.Info("Recording {ItemId} came back to the library; restored its {Count} owner(s)", item.Id, owners.Count);
            }
            else if (hasTombstone)
            {
                data.Removed.Remove(item.Id);
                Save(data);
                _logger.Info("Recording {ItemId} came back to the library after its owners were kept for {Days} days; it has no owners", item.Id, RemovedRetention.TotalDays);
                return;
            }
            else
            {
                return;
            }

            if (owners.Count == 0)
            {
                Save(data);
                return;
            }

            AddOwners(data.Items, item.Id, owners);
            Save(data);
            await StampTagsAsync(item.Id, owners, ct).ConfigureAwait(false);
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

            // A finished timer whose recording this was is done with: its owners now live in the
            // tombstone, and kept, it would claim whatever is recorded to its path next.
            foreach (var entry in _timerItems.Where(e => e.Value == itemId).ToList())
            {
                _timerItems.Remove(entry.Key);
                if (data.Timers.ContainsKey(entry.Key)
                    && !IsLive(entry.Key)
                    && (!data.TimerPaths.TryGetValue(entry.Key, out var path) || _host.FindItemByPath(path) == null))
                {
                    data.Timers.Remove(entry.Key);
                    _logger.Info("Forgot finished timer {TimerId} along with its removed recording {ItemId}", entry.Key, itemId);
                }
            }

            PruneSideTables(data);
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
        timers.FirstOrDefault(t => t.Status != StatusCancelled && string.Equals(t.ProgramId, programId, StringComparison.OrdinalIgnoreCase));

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

    // Notes what an owned timer's later events and its recording need: its service id and the
    // file it is writing.
    private bool Track(OwnershipStoreData data, ScheduledTimer timer)
    {
        var key = ApiIdOf(timer);
        if (!data.Timers.ContainsKey(key))
        {
            return false;
        }

        var changed = !string.IsNullOrEmpty(timer.TimerId) && RememberServiceId(data, key, timer.TimerId);
        return CaptureActivePath(data, timer) | changed;
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
                if (!data.TimerPaths.ContainsKey(apiId))
                {
                    data.TimerPaths[apiId] = path;
                }
            }

            data.TimersMissingSince.Remove(key);
            data.TimerServiceIds.Remove(key);
            RememberServiceId(data, apiId, key);
            changed = true;
        }

        return changed;
    }

    // Drops what is kept beside a timer or rule whose owners are gone.
    private static bool PruneSideTables(OwnershipStoreData data)
    {
        var changed = false;
        foreach (var key in data.TimerPaths.Keys.Where(k => !data.Timers.ContainsKey(k)).ToList())
        {
            changed |= data.TimerPaths.Remove(key);
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

    // Records the file an owned timer is writing, so the finished item can find its owners.
    private bool CaptureActivePath(OwnershipStoreData data, ScheduledTimer timer)
    {
        if (timer.Status != StatusInProgress || string.IsNullOrEmpty(timer.TimerId))
        {
            return false;
        }

        var key = ApiIdOf(timer);
        if (!data.Timers.ContainsKey(key))
        {
            return false;
        }

        var path = _host.GetActiveRecordingPath(timer.TimerId);
        if (string.IsNullOrEmpty(path)
            || (data.TimerPaths.TryGetValue(key, out var known) && string.Equals(known, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        data.TimerPaths[key] = path!;
        return true;
    }

    // The owned timer, with owners, that recorded the file at this path; null when there is none.
    // A finished timer can outlive its recording when the item is deleted before a sync forgets
    // the timer, so a timer still recording that path is preferred over it.
    private string? FindTimerForPath(OwnershipStoreData data, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string? finished = null;
        foreach (var entry in data.TimerPaths)
        {
            if (string.Equals(entry.Value, path, StringComparison.OrdinalIgnoreCase)
                && data.Timers.ContainsKey(entry.Key))
            {
                if (IsLive(entry.Key))
                {
                    return entry.Key;
                }

                finished ??= entry.Key;
            }
        }

        return finished;
    }

    // When an item's file was last written, in UTC; null when that can't be read. The last-write
    // time is used, not the creation time: Linux file systems often have no creation time (.NET
    // then reports the older of the change and write times), and Windows can hand a file
    // re-created at a deleted file's path the old one's creation time. A rerun keeps writing its
    // file after the old recording's removal and its timer's end; a file that went away with its
    // storage and came back was last written before both. Limits: a restore or post-processing
    // that rewrites the file makes it look new (its owners are then dropped, which fails safe),
    // and storage whose clock runs behind the server's by more than the gap between a deletion
    // and the rerun makes a rerun look old.
    private DateTime? FileWrittenAt(Guid itemId)
    {
        DateTime? writtenAt;
        try
        {
            writtenAt = _host.GetFileWrittenAt(itemId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(ex, "Could not read when the file of recording {ItemId} was written", itemId);
            return null;
        }

        return writtenAt is { } written && written > UnknownFileTime ? ToUtc(written) : null;
    }

    private static DateTime ToUtc(DateTime time) => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;

    // Whether a path match with a finished timer is only a coincidence: the file was written after
    // the timer's window (plus post-processing) ended; or, when the listing no longer has the
    // timer, after the recording at that path was removed, or else after a sync first found the
    // timer done without its recording (plus post-processing). Unknown times never count as stale.
    private bool IsStaleMatch(OwnershipStoreData data, string timerId, DateTime? writtenAt, RemovedItem? removed)
    {
        if (writtenAt is not { } written || IsLive(timerId))
        {
            return false;
        }

        if (_timers.TryGetValue(timerId, out var timer))
        {
            return written > ToUtc(timer.WindowEnd) + PostRecordingAllowance + FileClockTolerance;
        }

        if (removed != null)
        {
            return written > removed.RemovedAt + FileClockTolerance;
        }

        return data.TimersMissingSince.TryGetValue(timerId, out var doneSince)
            && written > doneSince + PostRecordingAllowance + FileClockTolerance;
    }

    // Whether the last listing has the timer and the server isn't done with it.
    private bool IsLive(string timerId) =>
        _timers.TryGetValue(timerId, out var timer) && timer.Status != StatusCancelled && timer.Status != StatusCompleted;

    // Whether a timer other than this one is still recording the same file, so this one's
    // recording at that path was replaced.
    private bool PathTakenOver(OwnershipStoreData data, string timerId, string path) =>
        !IsLive(timerId)
        && data.TimerPaths.Any(e => !string.Equals(e.Key, timerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Value, path, StringComparison.OrdinalIgnoreCase)
            && data.Timers.ContainsKey(e.Key)
            && IsLive(e.Key));

    // Gives each recording already in the library the owners of the owned timer writing it,
    // stamping a tag for each owner it gains. The item can exist before its file is captured
    // (Jellyfin adds it when the recording starts), and owners can join a recording in progress,
    // so this runs whenever files may have been captured or owners added. Saves before stamping
    // each item's tags, so a tag write that fails or raises events can't lose the owners. True
    // when an item gained an owner.
    private async Task<bool> AttachToStartedRecordingsAsync(OwnershipStoreData data, CancellationToken ct)
    {
        var changed = false;
        foreach (var entry in data.TimerPaths.ToList())
        {
            if (!data.Timers.TryGetValue(entry.Key, out var timerOwners)
                || timerOwners.Count == 0
                || PathTakenOver(data, entry.Key, entry.Value))
            {
                continue;
            }

            var item = _host.FindItemByPath(entry.Value);
            if (item == null)
            {
                continue;
            }

            var gained = data.Items.TryGetValue(item.Id, out var itemOwners)
                ? timerOwners.Where(o => !itemOwners.Contains(o)).ToList()
                : timerOwners.ToList();
            if (gained.Count == 0)
            {
                _timerItems[entry.Key] = item.Id;
                continue;
            }

            // A finished timer's path now holding a file written long after it ended is a rerun
            // someone else recorded there.
            if (!IsLive(entry.Key)
                && IsStaleMatch(data, entry.Key, FileWrittenAt(item.Id), data.Removed.TryGetValue(item.Id, out var tombstone) ? tombstone : null))
            {
                continue;
            }

            _timerItems[entry.Key] = item.Id;

            AddOwners(data.Items, item.Id, gained);
            changed = true;
            _logger.Info("Recording {ItemId} from timer {TimerId} took {Count} owner(s)", item.Id, entry.Key, gained.Count);
            if (data.Removed.Remove(item.Id))
            {
                // A new recording on a removed one's file; a later re-add must not bring the old owners back.
                _logger.Info("Discarded the owners kept for removed recording {ItemId}, since timer {TimerId} recorded it again", item.Id, entry.Key);
            }

            Save(data);
            await StampTagsAsync(item.Id, gained, ct).ConfigureAwait(false);
        }

        return changed;
    }

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
        var sameSeries = ruled.Where(t =>
        {
            if (string.IsNullOrEmpty(t.ProgramId))
            {
                return false;
            }

            if (!programs.TryGetValue(t.ProgramId!, out var other))
            {
                other = _host.GetProgram(t.ProgramId!);
                programs[t.ProgramId!] = other;
            }

            return other != null
                && ((!string.IsNullOrEmpty(program.SeriesId) && string.Equals(other.SeriesId, program.SeriesId, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(program.SeriesName) && string.Equals(other.SeriesName, program.SeriesName, StringComparison.OrdinalIgnoreCase)));
        });
        var busiest = sameSeries
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

        return string.IsNullOrEmpty(program.SeriesName)
            ? null
            : rules.FirstOrDefault(r => string.Equals(r.Name, program.SeriesName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

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
    private bool? IsRecording(Guid itemId)
    {
        try
        {
            return _host.IsLiveTvRecording(itemId);
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
            switch (IsRecording(itemId))
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

        _data = new OwnershipStoreData();

        // Should the read have failed for another reason, a newer file is still never rebuilt over.
        var newest = Math.Max(PeekFileVersion(_storePath) ?? 0, PeekFileVersion(_storePath + BackupSuffix) ?? 0);
        if (newest > CurrentVersion)
        {
            _data.Version = newest;
            _readOnly = true;
            _logger.Warn(null, "The recording owners file {Path} could not be read but is version {Version}, newer than this plugin's {Current}; recording owners are unavailable until the plugin is upgraded, and the file is left untouched", _storePath, newest, CurrentVersion);
            return _data;
        }

        // Read returns null both when there is no file yet and when the file and its backup are
        // both unusable. Only the second loses data, so only then are the recordings' owners
        // rebuilt from their tags.
        if (File.Exists(_storePath) || File.Exists(_storePath + BackupSuffix))
        {
            // The unusable copies are set aside before anything overwrites them, so a read that
            // failed only for the moment can't lose the owners for good.
            _preserveUnusable = true;
            Rebuild(_data);
            Save(_data);
        }

        return _data;
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

    // Rebuilds the recordings' owners from their "Recorded by name" tags. Timer and rule owners
    // have no backup and are lost.
    private void Rebuild(OwnershipStoreData data)
    {
        var users = _host.GetUsers();
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _host.FindItemsWithTagPrefix(TagPrefix))
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

        _logger.Warn(null, "The recording owners file {Path} and its backup were unusable; rebuilt the owners of {Count} recording(s) from their tags. Timer and series rule owners are lost", _storePath, data.Items.Count);
    }

    private void Save(OwnershipStoreData data)
    {
        if (_readOnly)
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(ex, "Could not save recording owners to {Path}; changes since the last save are lost on restart", _storePath);
        }
    }
}
