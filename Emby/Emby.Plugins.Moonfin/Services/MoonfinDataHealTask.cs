using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Heals per-user data files corrupted by the in-place writes that shipped before
    /// AtomicFile. Truncated settings files are repaired when a usable prefix survives, and
    /// anything unsalvageable is moved to the quarantine folder under the plugin data
    /// directory, never deleted. The boot-time run comes from ServerEntryPoint after the
    /// plugin services initialize; this task carries no default triggers and exists so admins
    /// can watch and re-run the pass from the dashboard.
    /// </summary>
    public class MoonfinDataHealTask : IScheduledTask
    {
        public string Name => "Moonfin Settings File Repair";
        public string Key => "Moonfin.Data.Heal";
        public string Description => "Repairs Moonfin user settings files damaged by interrupted writes and quarantines anything unrecoverable.";
        public string Category => "Moonfin";

        private readonly ILogger _logger;

        public MoonfinDataHealTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinDataHeal");
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            var settingsService = plugin?.SettingsService;
            var notificationStore = plugin?.NotificationStore;
            var seerrService = plugin?.SeerrService;
            if (settingsService == null || notificationStore == null || seerrService == null)
            {
                _logger.Info("Data heal skipped: plugin services not initialized yet");
                return;
            }

            await MoonfinDataHeal.RunAsync(
                settingsService,
                notificationStore,
                seerrService,
                _logger,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No triggers on Emby: a startup trigger can fire before InitializeServices, so
            // the boot-time run lives in ServerEntryPoint instead.
            return Enumerable.Empty<TaskTriggerInfo>();
        }
    }

    /// <summary>
    /// Shared body for the boot-time run and the dashboard task, so both log the same summary
    /// and overlap through the same guard.
    /// </summary>
    public static class MoonfinDataHeal
    {
        public static async Task RunAsync(
            MoonfinSettingsService settingsService,
            NotificationStore notificationStore,
            SeerrSessionService seerrService,
            ILogger logger,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            if (!FileHealer.TryBeginRun())
            {
                logger.Info("Data heal skipped: another run is already in progress");
                return;
            }

            try
            {
                progress?.Report(0);
                var settings = await settingsService.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(33);
                var notifications = await notificationStore.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(66);
                var sessions = await seerrService.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(100);

                logger.Info(
                    "Moonfin data heal complete: settings {0} scanned / {1} healthy / {2} salvaged / " +
                    "{3} recovered-from-backup / {4} quarantined; notifications {5} scanned / {6} quarantined; " +
                    "seerr-sessions {7} scanned / {8} quarantined",
                    settings.Scanned,
                    settings.Healthy,
                    settings.Salvaged,
                    settings.RecoveredFromBackup,
                    settings.Quarantined,
                    notifications.Scanned,
                    notifications.Quarantined,
                    sessions.Scanned,
                    sessions.Quarantined);
            }
            finally
            {
                FileHealer.EndRun();
            }
        }
    }
}
