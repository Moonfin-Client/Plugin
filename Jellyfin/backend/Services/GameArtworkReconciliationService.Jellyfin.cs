using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Games;

public sealed partial class GameArtworkReconciliationService : IHostedService
{
    private readonly ITaskManager? _taskManager;

    // The task manager lets a finished library scan queue reconciliation. It's optional only so
    // tests can build the service without one.
    public GameArtworkReconciliationService(
        GamesService games,
        GameThumbService thumbs,
        GameArtworkCatalog catalog,
        ILogger<GameArtworkReconciliationService> logger,
        ITaskManager? taskManager = null)
        : this(games, thumbs, catalog, logger, taskManager, watchLibraryFileSystem: true)
    {
    }

    internal GameArtworkReconciliationService(
        GamesService games,
        GameThumbService thumbs,
        GameArtworkCatalog catalog,
        ILogger<GameArtworkReconciliationService> logger,
        ITaskManager? taskManager,
        bool watchLibraryFileSystem,
        TimeSpan? workerStopGraceForTests = null)
        : this(games, thumbs, catalog, logger, watchLibraryFileSystem, workerStopGraceForTests)
    {
        _taskManager = taskManager;
    }

    private partial bool SubscribeLibraryScans()
    {
        if (_taskManager == null)
        {
            return false;
        }

        _taskManager.TaskCompleted += OnTaskCompleted;
        return true;
    }

    private partial void UnsubscribeLibraryScans()
    {
        if (_taskManager != null)
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
    }

    // Runs on the scheduled task thread after every task, so it only checks for the library scan
    // and hands the work off to QueueReconciliation.
    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (string.Equals(e.Result?.Key, "RefreshLibrary", StringComparison.Ordinal))
        {
            QueueReconciliation("library scan completed");
        }
    }
}
