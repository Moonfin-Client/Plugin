using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Moonfin.Calendar;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Every 15 minutes, tells the requester and every admin about a Radarr or Sonarr download that
    /// is stuck, once per download. Off until an admin turns it on.
    /// </summary>
    public class StuckDownloadTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public StuckDownloadTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger("MoonfinStuckDownloads");
        }

        public string Name => "Moonfin Stuck Download Check";

        public string Key => "Moonfin.Seerr.StuckDownloads";

        public string Description => "Tells the requester and the admins when a Radarr or Sonarr download for a Seerr request gets stuck.";

        public string Category => "Moonfin";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Emby builds scheduled tasks before ServerEntryPoint.Run() sets up the services, so
            // everything is read here and a run that comes too early is skipped.
            var plugin = Plugin.Instance;
            PluginConfiguration? config = null;
            try { config = plugin?.Configuration; }
            catch { /* configuration not ready yet */ }

            var seerr = plugin?.SeerrService;
            var settings = plugin?.SettingsService;
            var notifications = plugin?.NotificationStore;
            var push = plugin?.PushDelivery;
            var store = plugin?.StuckDownloadState;
            if (config == null || !config.SeerrEnabled || !config.StuckDownloadNotify || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl())
                || seerr == null || settings == null || notifications == null || push == null || store == null)
            {
                return;
            }

            var session = seerr.FindAdminSession();
            if (session == null)
            {
                _logger.Debug("Stuck download check skipped: no admin Seerr session");
                return;
            }

            var run = new StuckDownloadRun
            {
                ReadQueue = isSonarr => seerr.ReadWholeArrQueueAsync(isSonarr ? "sonarr" : "radarr"),
                SeerrGet = async path =>
                {
                    var response = await seerr.RequestWithSessionAsync(session, HttpMethod.Get, path, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return new ArrAnswer(response.StatusCode, response.Body);
                },
                Admins = Admins,
                RequesterWantsIt = userId => notifications.GetPrefs(userId).NotifyOnLibraryAdded,
                Deliver = (userId, title, body, pushBody, route) =>
                {
                    settings.NotifyUser(userId, JsonSerializer.Serialize(new { type = "seerrNotification", title, body, route }));
                    push.QueueToUser(userId, title, pushBody, route);
                },
                Log = message => _logger.Info("Stuck download check: " + message),
                NowUtc = DateTime.UtcNow,
                StalledThreshold = TimeSpan.FromHours(Math.Clamp(config.StuckDownloadStalledHours, 1, 168)),
                Cancellation = cancellationToken,
            };

            await StuckDownloads.RunAsync(run, store).ConfigureAwait(false);
            progress.Report(100);
        }

        private static IEnumerable<Guid> Admins() =>
            (PluginServices.UserManager?.GetUserList(new UserQuery()) ?? Array.Empty<User>())
                .Where(u => u.Policy?.IsAdministrator == true)
                .Select(u => u.Id);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = StuckDownloads.Interval.Ticks };
        }
    }
}
