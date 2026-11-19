using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.LiveTv;

/// <summary>
/// Keeps <see cref="RecordingOwnershipService"/> in step with the server: a sync once a minute while
/// a recording is in progress or a timer is about to start or has just started, otherwise every ten
/// minutes or when a schedule event wakes it, plus once more shortly after a timer is created. Every
/// handler returns at once, because the service raises some of these events itself while holding its
/// lock; the cancel handler only asks the host for the timer's file first, since the recording stops
/// right after the event. It also keeps a cancelled recording cancelled when the server puts its
/// timer back to retry it (see CancelChecks). Each server starts
/// <see cref="RunAsync"/> once and stops it on shutdown.
/// </summary>
public sealed class RecordingOwnershipWatcher : IDisposable
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NearWindow = TimeSpan.FromMinutes(10);

    // A timer made for a program already airing starts recording seconds later; one extra sync
    // then captures its file while the recording is still young.
    private static readonly TimeSpan CreatedFollowUp = TimeSpan.FromSeconds(10);

    // Jellyfin reads a cancel that had to kill ffmpeg (a stream that ignores the polite stop) as a
    // failed recording, and puts the same timer back to retry a minute later; every cancel then kills
    // it again. The timer comes back about ten seconds after the cancel and records again about a
    // minute after that, so it is looked for over the next two minutes. A cancel that lands before
    // the retry starts has nothing to kill, so it sticks.
    private static readonly TimeSpan[] CancelChecks =
    {
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
    };

    private const int MaxRecancels = 3;

    private readonly ILiveTvRecordingHost _host;
    private readonly RecordingOwnershipService _service;
    private readonly IRecordingLog _logger;
    private readonly TimeSpan _createdFollowUp;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _queueLock = new();
    private Task _queue = Task.CompletedTask;

    // Every listed timer id, API or service kind, to its service id, from the host's last listing
    // while something was recording. Read on the raising thread of a cancel, so it is never behind
    // the service's lock. Each listing builds a new map and swaps it in whole, so a cancel never
    // sees it half filled.
    private volatile IReadOnlyDictionary<string, string> _serviceIds = EmptyServiceIds;
    private int _serviceIdListings;
    private CancellationToken _stopping;

    // Cancelled recordings being watched for the server putting their timer back, by cancelled id.
    private readonly ConcurrentDictionary<string, Task> _cancelGuards = new(StringComparer.OrdinalIgnoreCase);

    public RecordingOwnershipWatcher(ILiveTvRecordingHost host, RecordingOwnershipService service, IRecordingLog log)
        : this(host, service, log, CreatedFollowUp)
    {
    }

    // Tests shorten the follow-up sync after TimerCreated.
    internal RecordingOwnershipWatcher(ILiveTvRecordingHost host, RecordingOwnershipService service, IRecordingLog log, TimeSpan createdFollowUp)
        : this(host, service, log, createdFollowUp, Task.Delay)
    {
    }

    // Tests complete the follow-up wait themselves, so no wall-clock time is involved.
    internal RecordingOwnershipWatcher(ILiveTvRecordingHost host, RecordingOwnershipService service, IRecordingLog log, Func<TimeSpan, CancellationToken, Task> followUpDelay)
        : this(host, service, log, CreatedFollowUp, followUpDelay)
    {
    }

    private RecordingOwnershipWatcher(ILiveTvRecordingHost host, RecordingOwnershipService service, IRecordingLog log, TimeSpan createdFollowUp, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _host = host;
        _service = service;
        _logger = log;
        _createdFollowUp = createdFollowUp;
        _delay = delay;
    }

    private static IReadOnlyDictionary<string, string> EmptyServiceIds { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _wake.Dispose();

    // Tests wait on this instead of sleeping until the id map is filled.
    internal bool KnowsServiceId(string id) => _serviceIds.ContainsKey(id);

    // How many id listings have finished and been swapped in, for tests.
    internal int ServiceIdListings => Volatile.Read(ref _serviceIdListings);

    // Completes once every cancel guard started so far has finished, for tests.
    internal Task CancelGuardsDone => Task.WhenAll(_cancelGuards.Values);

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _host.ScheduleChanged += OnScheduleChanged;
        _host.TimerCreated += OnTimerCreated;
        _host.TimerCancelled += OnTimerCancelled;
        _host.GuideRefreshed += OnGuideRefreshed;
        _host.ItemAdded += OnItemAdded;
        _host.ItemRemoved += OnItemRemoved;
        _host.UserUpdated += OnUserUpdated;
        _host.UserDeleted += OnUserDeleted;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Anything that escapes here would end the loop, and with it every sync until a restart.
                try
                {
                    await _service.SyncAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.Error(ex, "Recording owners sync failed");
                }

                await RefreshServiceIdsAsync(stoppingToken).ConfigureAwait(false);

                await _wake.WaitAsync(NextDelay(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server shutting down.
        }
        finally
        {
            _host.ScheduleChanged -= OnScheduleChanged;
            _host.TimerCreated -= OnTimerCreated;
            _host.TimerCancelled -= OnTimerCancelled;
            _host.GuideRefreshed -= OnGuideRefreshed;
            _host.ItemAdded -= OnItemAdded;
            _host.ItemRemoved -= OnItemRemoved;
            _host.UserUpdated -= OnUserUpdated;
            _host.UserDeleted -= OnUserDeleted;
        }
    }

    private TimeSpan NextDelay()
    {
        if (_service.HasRecordingInProgress)
        {
            return ActiveInterval;
        }

        // A recording's file is only captured while a sync sees its timer in progress, so sync
        // often from shortly before a timer starts until shortly after.
        var next = _service.NextTimerStart;
        if (next.HasValue && next.Value - DateTime.UtcNow <= NearWindow)
        {
            return ActiveInterval;
        }

        return IdleInterval;
    }

    // Only a recording in progress has a file a cancel needs to find, so the ids are listed only then.
    private async Task RefreshServiceIdsAsync(CancellationToken ct)
    {
        if (!_service.HasRecordingInProgress)
        {
            _serviceIds = EmptyServiceIds;
            return;
        }

        try
        {
            var timers = await _host.GetTimersAsync(ct).ConfigureAwait(false);
            var serviceIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var timer in timers)
            {
                if (string.IsNullOrEmpty(timer.TimerId))
                {
                    continue;
                }

                serviceIds[timer.TimerId] = timer.TimerId;
                if (!string.IsNullOrEmpty(timer.ApiId))
                {
                    serviceIds[timer.ApiId!] = timer.TimerId;
                }
            }

            _serviceIds = serviceIds;
            Interlocked.Increment(ref _serviceIdListings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.Warn(ex, "Could not list the timers for the recording owners");
        }
    }

    // The file a cancelled timer is writing. The server raises the cancel before it stops the
    // recording, so this runs on the raising thread, through the host only: the service's lock may
    // be held by whoever cancelled.
    private string? ActivePathOf(string timerId)
    {
        if (string.IsNullOrEmpty(timerId))
        {
            return null;
        }

        try
        {
            var path = _host.GetActiveRecordingPath(timerId);
            if (string.IsNullOrEmpty(path)
                && _serviceIds.TryGetValue(timerId, out var serviceId)
                && !string.Equals(serviceId, timerId, StringComparison.OrdinalIgnoreCase))
            {
                path = _host.GetActiveRecordingPath(serviceId);
            }

            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Could not look up the recording of cancelled timer {TimerId}", timerId);
            return null;
        }
    }

    private void WakeLater(TimeSpan delay)
    {
        _ = _delay(delay, _stopping).ContinueWith(
            _ => Wake(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already due to wake.
        }
        catch (ObjectDisposedException)
        {
            // Shut down.
        }
    }

    private void OnScheduleChanged(object? sender, EventArgs e) => Wake();

    private void OnTimerCreated(object? sender, ScheduledTimer timer)
    {
        Wake();
        WakeLater(_createdFollowUp);
    }

    private void OnGuideRefreshed(object? sender, EventArgs e) => Wake();

    private void OnTimerCancelled(object? sender, string timerId)
    {
        var path = ActivePathOf(timerId);
        Handle(() => _service.OnTimerCancelledAsync(timerId, path, CancellationToken.None));

        // Only a cancel that stopped a recording can be turned into a retry.
        if (path != null && !_stopping.IsCancellationRequested)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_cancelGuards.TryAdd(timerId, done.Task))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await KeepCancelledAsync(timerId).ConfigureAwait(false);
                    }
                    finally
                    {
                        _cancelGuards.TryRemove(timerId, out _);
                        done.TrySetResult(true);
                    }
                });
            }
        }
    }

    // Cancels the timer again whenever the server puts it back after the cancel. Matched by the
    // timer's own id: a fresh recording of the same program, by anyone, gets a new timer.
    private async Task KeepCancelledAsync(string timerId)
    {
        var recancels = 0;
        try
        {
            foreach (var wait in CancelChecks)
            {
                await _delay(wait, _stopping).ConfigureAwait(false);
                IReadOnlyList<ScheduledTimer> timers;
                try
                {
                    timers = await _host.GetTimersAsync(_stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn(ex, "Could not check whether cancelled timer {TimerId} came back", timerId);
                    continue;
                }

                var back = timers.FirstOrDefault(t => t.Status is not ("Cancelled" or "Completed")
                    && (string.Equals(t.TimerId, timerId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(t.ApiId, timerId, StringComparison.OrdinalIgnoreCase)));
                if (back == null)
                {
                    continue;
                }

                _logger.Warn(null, "The server put cancelled timer {TimerId} back to retry its recording; cancelling it again", timerId);
                await _host.CancelTimerAsync(back.ApiId ?? back.TimerId, _stopping).ConfigureAwait(false);
                if (++recancels >= MaxRecancels)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Could not keep timer {TimerId} cancelled", timerId);
        }
    }

    private void OnItemAdded(object? sender, LibraryItem item) => Handle(() => _service.OnItemAddedAsync(item, CancellationToken.None));

    private void OnItemRemoved(object? sender, Guid itemId) => Handle(() => _service.OnItemRemovedAsync(itemId, CancellationToken.None));

    private void OnUserUpdated(object? sender, ServerUser user) => Handle(() => _service.OnUserUpdatedAsync(user, CancellationToken.None));

    private void OnUserDeleted(object? sender, Guid userId) => Handle(() => _service.OnUserDeletedAsync(userId, CancellationToken.None));

    // The service raises some of these events while holding its lock, so none is handled on the
    // raising thread. They run one at a time in the order they arrived: a recording that leaves
    // the library and comes straight back must be removed before it is added again. Stopping
    // takes no new events but doesn't cancel those already taken, so a shutdown that waits on
    // Idle applies them all (a removal is never left without the re-add that followed it).
    private void Handle(Func<Task> work)
    {
        lock (_queueLock)
        {
            // Checked under the lock Idle reads, so an event is either queued before shutdown
            // takes Idle or not taken at all.
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _queue = _queue.ContinueWith(_ => RunEventAsync(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RunEventAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
        {
            // Shut down before this event's turn came.
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Recording owners event failed");
        }
    }

    /// <summary>Completes once every event received so far has been handled.</summary>
    public Task Idle
    {
        get
        {
            lock (_queueLock)
            {
                return _queue;
            }
        }
    }
}
