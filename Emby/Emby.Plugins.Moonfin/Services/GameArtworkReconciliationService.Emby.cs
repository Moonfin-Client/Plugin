using MediaBrowser.Model.Tasks;

namespace Moonfin.Games
{
    public sealed partial class GameArtworkReconciliationService
    {
        /// <summary>Emby's task manager, set before StartAsync so a finished library scan queues reconciliation.</summary>
        internal ITaskManager? TaskManager { get; set; }

        private partial bool SubscribeLibraryScans()
        {
            var taskManager = TaskManager;
            if (taskManager == null) return false;
            taskManager.TaskCompleted += OnTaskCompleted;
            return true;
        }

        private partial void UnsubscribeLibraryScans()
        {
            var taskManager = TaskManager;
            if (taskManager != null) taskManager.TaskCompleted -= OnTaskCompleted;
        }

        private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
        {
            if (string.Equals(e.Task?.ScheduledTask?.Key, "RefreshLibrary", StringComparison.Ordinal))
            {
                QueueReconciliation("library scan completed");
            }
        }
    }
}
