using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Matches titles that land in the library to the outside lists already cached, so a list shows
/// the library copy without waiting for its next fetch. Updates are watched too, since a fresh
/// scan can add an item before its provider ids are read. A scan touches many items at once, so
/// the caches are saved on a timer rather than once per item.
/// </summary>
public sealed class LocalItemArrivalNotifier : IHostedService, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly LocalItemIdService _localItems;
    private readonly ILogger<LocalItemArrivalNotifier> _logger;

    private Timer? _flushTimer;
    private int _pendingChanges;

    public LocalItemArrivalNotifier(
        ILibraryManager libraryManager,
        LocalItemIdService localItems,
        ILogger<LocalItemArrivalNotifier> logger)
    {
        _libraryManager = libraryManager;
        _localItems = localItems;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _flushTimer = new Timer(_ => _ = FlushIfChangedAsync(), null, FlushInterval, FlushInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _flushTimer?.Dispose();
        _flushTimer = null;
        await FlushIfChangedAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (item == null || item.IsVirtualItem || LocalItemIdService.IdsOf(item) == null)
            {
                return;
            }

            var changed = _localItems.MatchArrival(item);
            if (changed > 0)
            {
                Interlocked.Add(ref _pendingChanges, changed);
                _logger.LogDebug("Matched {Count} cached list title(s) to library item {ItemId}", changed, item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Matching a library change to the cached lists failed");
        }
    }

    private async Task FlushIfChangedAsync()
    {
        if (Interlocked.Exchange(ref _pendingChanges, 0) == 0)
        {
            return;
        }

        try
        {
            await _localItems.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the cached lists after library matches failed");
        }
    }
}
