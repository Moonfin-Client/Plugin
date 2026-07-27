using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Delivers a notification as FCM push to every registered device of a user, so backgrounded
/// or closed clients still get it. Sends are fire-and-forget so a push failure never blocks
/// or breaks SSE delivery. The hosted relay is the default and needs no service account, but
/// a self-hoster who configures their own gets direct FCM instead. Dead tokens are pruned.
/// Shared by every event source that pushes: Seerr webhooks, admin broadcasts, and new media.
/// </summary>
public class PushDeliveryService
{
    private readonly NotificationStore _store;
    private readonly FcmSender _fcmSender;
    private readonly RelaySender _relaySender;
    private readonly ILogger<PushDeliveryService> _logger;

    public PushDeliveryService(
        NotificationStore store,
        FcmSender fcmSender,
        RelaySender relaySender,
        ILogger<PushDeliveryService> logger)
    {
        _store = store;
        _fcmSender = fcmSender;
        _relaySender = relaySender;
        _logger = logger;
    }

    /// <summary>
    /// Queues a push to every registered device of the user. No-op when push is disabled or the
    /// user has no devices. A non-null <paramref name="requestId"/> marks a Seerr request
    /// notification, which is shaped per platform so Approve/Deny buttons render on a closed app.
    /// </summary>
    public void QueueToUser(Guid userId, string title, string body, string route, string? requestId = null)
    {
        var config = MoonfinPlugin.Instance?.Configuration;
        var pushEnabled = config?.PushEnabled == true;
        var devices = config == null ? new List<DeviceRegistration>() : _store.GetUserDevices(userId);

        _logger.LogInformation("push: user {UserId} enabled={Enabled} devices={Count}",
            userId, pushEnabled, devices.Count);

        if (config == null || !pushEnabled || devices.Count == 0)
        {
            return;
        }

        var liveDevices = devices
            .Where(d => !string.IsNullOrWhiteSpace(d.Token))
            .ToList();
        if (liveDevices.Count == 0)
        {
            return;
        }

        // A request notification must be shaped per platform so the buttons render on a closed
        // app. Other events keep the single-call path.
        if (requestId != null)
        {
            DeliverRequestPush(userId, title, body, route, requestId, liveDevices, config.HasServiceAccount);
            return;
        }

        var tokens = liveDevices.Select(d => d.Token).ToList();

        // A configured service account is an explicit self-hosted opt-in to direct FCM.
        // Otherwise fall back to the hosted relay using the effective app key.
        if (config.HasServiceAccount)
        {
            _ = Task.Run(async () =>
            {
                var pruned = 0;
                foreach (var token in tokens)
                {
                    try
                    {
                        var result = await _fcmSender.SendAsync(token, title, body, route);
                        if (result == FcmSendResult.TokenDead)
                        {
                            _store.RemoveDeviceByToken(userId, token);
                            pruned++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Push delivery failed for user {UserId}", userId);
                    }
                }

                _logger.LogInformation("push: user {UserId} pruned {Count} dead tokens", userId, pruned);
            });
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var dead = await _relaySender.SendAsync(tokens, title, body, route);
                var pruned = PruneDead(userId, dead);
                _logger.LogInformation("push: user {UserId} pruned {Count} dead tokens", userId, pruned);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relay push delivery failed for user {UserId}", userId);
            }
        });
    }

    // Splits the user's devices into iOS vs everything-else ("android"). iOS gets a data +
    // apnsCategory push so a closed app can render Approve/Deny inline. Android gets a normal
    // notification (same shape as a non-request send) so the OS renders it even when the app is
    // force-killed, and tapping it opens the app. Direct FCM and relay paths mirror each other.
    private void DeliverRequestPush(
        Guid userId, string title, string body, string route, string requestId,
        List<DeviceRegistration> liveDevices, bool hasServiceAccount)
    {
        static bool IsIos(DeviceRegistration d) =>
            string.Equals(d.Platform, "ios", StringComparison.OrdinalIgnoreCase);

        var iosTokens = liveDevices.Where(IsIos).Select(d => d.Token).ToList();
        var androidTokens = liveDevices.Where(d => !IsIos(d)).Select(d => d.Token).ToList();

        if (hasServiceAccount)
        {
            _ = Task.Run(async () =>
            {
                var pruned = 0;
                foreach (var token in iosTokens)
                {
                    pruned += await SendFcmRequestAsync(userId, token, title, body, route, requestId, "ios");
                }

                foreach (var token in androidTokens)
                {
                    pruned += await SendFcmRequestAsync(userId, token, title, body, route, requestId, "android");
                }

                _logger.LogInformation("push: user {UserId} pruned {Count} dead tokens", userId, pruned);
            });
            return;
        }

        _ = Task.Run(async () =>
        {
            var pruned = 0;
            try
            {
                if (iosTokens.Count > 0)
                {
                    var data = new Dictionary<string, string>
                    {
                        ["requestId"] = requestId,
                        ["kind"] = "request"
                    };
                    var dead = await _relaySender.SendAsync(
                        iosTokens, title, body, route, data, apnsCategory: "seerr_request");
                    pruned += PruneDead(userId, dead);
                }

                if (androidTokens.Count > 0)
                {
                    // Normal notification+data{route}, so a killed app still shows it.
                    var dead = await _relaySender.SendAsync(androidTokens, title, body, route);
                    pruned += PruneDead(userId, dead);
                }

                _logger.LogInformation("push: user {UserId} pruned {Count} dead tokens", userId, pruned);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relay push delivery failed for user {UserId}", userId);
            }
        });
    }

    private async Task<int> SendFcmRequestAsync(
        Guid userId, string token, string title, string body, string route, string requestId, string platform)
    {
        try
        {
            var result = await _fcmSender.SendAsync(token, title, body, route, requestId, platform);
            if (result == FcmSendResult.TokenDead)
            {
                _store.RemoveDeviceByToken(userId, token);
                return 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Push delivery failed for user {UserId}", userId);
        }

        return 0;
    }

    private int PruneDead(Guid userId, IReadOnlyList<PushResult> dead)
    {
        var pruned = 0;
        foreach (var result in dead)
        {
            _store.RemoveDeviceByToken(userId, result.Token);
            pruned++;
        }

        return pruned;
    }
}
