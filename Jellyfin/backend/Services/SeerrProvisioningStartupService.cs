using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Fires webhook auto-provisioning once shortly after startup, so an existing admin
/// session registers the webhook without waiting for the next admin login. Best-effort:
/// if no admin session exists yet it simply no-ops and the login path retries later.
/// </summary>
public class SeerrProvisioningStartupService : IHostedService
{
    private readonly SeerrProvisioningService _provisioning;
    private readonly ILogger<SeerrProvisioningStartupService> _logger;
    private CancellationTokenSource? _cts;

    public SeerrProvisioningStartupService(
        SeerrProvisioningService provisioning,
        ILogger<SeerrProvisioningStartupService> logger)
    {
        _provisioning = provisioning;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        // Delay a little so sessions and config are settled before the first attempt.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                var result = await _provisioning.EnsureWebhookAsync(token);

                // An outcome the admin has to act on is worth an ordinary log line, the
                // routine ones stay at debug so a working setup keeps quiet.
                var needsAttention = result.Status is ProvisioningStatus.AdminSessionExpired
                    or ProvisioningStatus.NeedsPublicUrl
                    or ProvisioningStatus.ForeignWebhookPresent
                    or ProvisioningStatus.Failed;

                _logger.Log(
                    needsAttention ? LogLevel.Information : LogLevel.Debug,
                    "Seerr webhook provisioning at startup: {Status}. {Message}",
                    result.Status, result.Message);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Startup Seerr webhook provisioning failed (non-fatal)");
            }
        }, token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }
}
