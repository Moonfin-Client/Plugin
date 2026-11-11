using System.Text.Json;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Calendar;
using Moonfin.Server.Helpers;

namespace Moonfin.Server.Services;

/// <summary>
/// Every 15 minutes, tells the requester and every admin about a Radarr or Sonarr download that
/// is stuck, once per download. Off until an admin turns it on.
/// </summary>
public class StuckDownloadTask : IScheduledTask
{
    private readonly SeerrSessionService _seerr;
    private readonly MoonfinSettingsService _settings;
    private readonly NotificationStore _notifications;
    private readonly PushDeliveryService _push;
    private readonly IUserDirectory _users;
    private readonly StuckDownloadStore _store;
    private readonly ILogger<StuckDownloadTask> _logger;

    public StuckDownloadTask(
        SeerrSessionService seerr,
        MoonfinSettingsService settings,
        NotificationStore notifications,
        PushDeliveryService push,
        IUserDirectory users,
        StuckDownloadStore store,
        ILogger<StuckDownloadTask> logger)
    {
        _seerr = seerr;
        _settings = settings;
        _notifications = notifications;
        _push = push;
        _users = users;
        _store = store;
        _logger = logger;
    }

    public string Name => "Moonfin Stuck Download Check";

    public string Key => "Moonfin.Seerr.StuckDownloads";

    public string Description => "Tells the requester and the admins when a Radarr or Sonarr download for a Seerr request gets stuck.";

    public string Category => "Moonfin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        if (config == null || !config.SeerrEnabled || !config.StuckDownloadNotify || string.IsNullOrEmpty(config.GetEffectiveSeerrUrl()))
        {
            return;
        }

        var session = _seerr.FindAdminSession();
        if (session == null)
        {
            _logger.LogDebug("Stuck download check skipped: no admin Seerr session");
            return;
        }

        var run = new StuckDownloadRun
        {
            ReadQueue = isSonarr => _seerr.ReadWholeArrQueueAsync(isSonarr ? "sonarr" : "radarr", cancellationToken),
            SeerrGet = async path =>
            {
                var response = await _seerr.RequestWithSessionAsync(session, HttpMethod.Get, path, cancellationToken: cancellationToken).ConfigureAwait(false);
                return new ArrAnswer(response.StatusCode, response.Body);
            },
            Admins = () => _users.Users().Where(u => u.IsAdministrator).Select(u => u.Id),
            RequesterWantsIt = userId => _notifications.GetPrefs(userId).NotifyOnLibraryAdded,
            Deliver = (userId, title, body, pushBody, route) =>
            {
                _settings.NotifyUser(userId, JsonSerializer.Serialize(new { type = "seerrNotification", title, body, route }));
                _push.QueueToUser(userId, title, pushBody, route);
            },
            Log = message => _logger.LogInformation("Stuck download check: {Message}", message),
            NowUtc = DateTime.UtcNow,
            StalledThreshold = TimeSpan.FromHours(Math.Clamp(config.StuckDownloadStalledHours, 1, 168)),
            Cancellation = cancellationToken,
        };

        await StuckDownloads.RunAsync(run, _store).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return TaskTriggers.Interval(StuckDownloads.Interval);
    }
}
