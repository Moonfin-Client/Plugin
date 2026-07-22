using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Startup task that heals per-user data files corrupted by the in-place writes that shipped
/// before AtomicFile. Truncated settings files are repaired when a usable prefix survives, and
/// anything unsalvageable is moved to the quarantine folder under the plugin data directory,
/// never deleted. The pass leaves healthy files untouched, so re-runs are cheap no-ops and
/// admins can trigger it manually from the dashboard.
/// </summary>
public class MoonfinDataHealTask : IScheduledTask
{
    public string Name => "Moonfin Settings File Repair";
    public string Key => "Moonfin.Data.Heal";
    public string Description => "Repairs Moonfin user settings files damaged by interrupted writes and quarantines anything unrecoverable.";
    public string Category => "Moonfin";

    private readonly MoonfinSettingsService _settingsService;
    private readonly NotificationStore _notificationStore;
    private readonly SeerrSessionService _seerrSessionService;
    private readonly ILogger<MoonfinDataHealTask> _logger;

    public MoonfinDataHealTask(
        MoonfinSettingsService settingsService,
        NotificationStore notificationStore,
        SeerrSessionService seerrSessionService,
        ILogger<MoonfinDataHealTask> logger)
    {
        _settingsService = settingsService;
        _notificationStore = notificationStore;
        _seerrSessionService = seerrSessionService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!FileHealer.TryBeginRun())
        {
            _logger.LogInformation("Data heal skipped: another run is already in progress");
            return;
        }

        try
        {
            progress.Report(0);
            var settings = await _settingsService.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(33);
            var notifications = await _notificationStore.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(66);
            var sessions = await _seerrSessionService.HealDataFilesAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);

            _logger.LogInformation(
                "Moonfin data heal complete: settings {SettingsScanned} scanned / {SettingsHealthy} healthy / " +
                "{SettingsSalvaged} salvaged / {SettingsBak} recovered-from-backup / {SettingsQuarantined} quarantined; " +
                "notifications {NotifScanned} scanned / {NotifQuarantined} quarantined; " +
                "seerr-sessions {SeerrScanned} scanned / {SeerrQuarantined} quarantined",
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

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Startup only. The sweep is a no-op after its first pass, and running every boot
        // also catches corruption from outside causes like a full disk.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerStartup
        };
    }
}
