using System;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.LiveTv;

/// <summary>
/// Runs <see cref="IncompleteRecordingService"/> on the schedule it asks for: once a minute while a
/// recording window is open, otherwise asleep until the next window, the safety-net check or a
/// server event wakes it. Each server starts <see cref="RunAsync"/> once and stops it on shutdown.
/// </summary>
public sealed class IncompleteRecordingWatcher : IDisposable
{
    private readonly IncompleteRecordingService _service;
    private readonly ILiveTvRecordingHost _host;
    private readonly IRecordingLog _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationToken _stopping;

    public IncompleteRecordingWatcher(
        IncompleteRecordingService service,
        ILiveTvRecordingHost host,
        IRecordingLog logger)
    {
        _service = service;
        _host = host;
        _logger = logger;
    }

    public void Dispose() => _wake.Dispose();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _host.ScheduleChanged += OnScheduleChanged;
        _host.TimerCreated += OnTimerCreated;
        _host.TimerCancelled += OnTimerCancelled;
        _host.GuideRefreshed += OnGuideRefreshed;
        _host.ItemAdded += OnItemAdded;
        _host.ItemUpdated += OnItemUpdated;
        _host.ItemRemoved += OnItemRemoved;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Anything that escapes here would end the loop, and with it every check until a restart.
                var delay = IncompleteRecordingRules.SampleInterval;
                try
                {
                    await _service.RunAsync(stoppingToken).ConfigureAwait(false);
                    delay = _service.NextRunAt() - DateTime.UtcNow;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.Error(ex, "Incomplete recordings check failed");
                }

                if (delay < TimeSpan.FromSeconds(1))
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                await _wake.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
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
            _host.ItemUpdated -= OnItemUpdated;
            _host.ItemRemoved -= OnItemRemoved;
        }
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
    }

    private void OnScheduleChanged(object? sender, EventArgs e) => Mark(_service.MarkScheduleChanged);

    private void OnTimerCancelled(object? sender, string timerId) => Mark(() => _service.MarkTimerCancelled(timerId));

    private void OnTimerCreated(object? sender, ScheduledTimer timer) => Mark(() => _service.MarkTimerCreated(timer));

    private void OnGuideRefreshed(object? sender, EventArgs e) => Mark(_service.MarkGuideRefreshed);

    // Timer events are raised inside the server's own CreateTimer and CancelTimer, so a failure
    // here must not reach the user's Record or Cancel.
    private void Mark(Action mark)
    {
        try
        {
            mark();
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Incomplete recordings schedule event failed");
        }

        Wake();
    }

    // The service raises library events itself when it saves a tag, possibly on the thread that
    // holds its lock, so each event is handled on its own task instead of the raising thread.
    // Events for items no record can concern are dropped first, so a library scan doesn't queue
    // a task per item.
    private void OnItemAdded(object? sender, LibraryItem item)
    {
        if (_service.MayConcernAddedItem(item))
        {
            Handle(() => _service.OnItemAddedAsync(item, _stopping));
        }
    }

    private void OnItemUpdated(object? sender, LibraryItem item)
    {
        if (_service.MayConcernItem(item.Id))
        {
            Handle(() => _service.OnItemUpdatedAsync(item, _stopping));
        }
    }

    private void OnItemRemoved(object? sender, Guid itemId)
    {
        if (_service.MayConcernItem(itemId))
        {
            Handle(() => _service.OnItemRemovedAsync(itemId, _stopping));
        }
    }

    private void Handle(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Server shutting down.
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Incomplete recordings library event failed");
            }
        });
    }
}
