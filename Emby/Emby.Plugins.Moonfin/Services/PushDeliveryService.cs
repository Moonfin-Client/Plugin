using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Delivers a notification as FCM push to every registered device of a user, so backgrounded
    /// or closed clients still get it. Runs independently of websocket delivery so a push failure
    /// never blocks it. Push goes through the hosted relay by default, which keeps send
    /// credentials off this server, while a self-hoster who configures a service account gets
    /// direct FCM. Dead tokens are pruned. Shared by every event source that pushes: Seerr
    /// webhooks, admin broadcasts, and new media.
    /// </summary>
    public class PushDeliveryService
    {
        private readonly NotificationStore _store;
        private readonly RelaySender _relaySender;
        private readonly FcmSender _fcmSender;
        private readonly ILogger _logger;

        public PushDeliveryService(NotificationStore store, RelaySender relaySender, FcmSender fcmSender, ILogger logger)
        {
            _store = store;
            _relaySender = relaySender;
            _fcmSender = fcmSender;
            _logger = logger;
        }

        /// <summary>
        /// Queues a push to every registered device of the user. No-op when push is disabled or the
        /// user has no devices. A non-null requestId marks a Seerr request notification, which is
        /// shaped per platform so Approve/Deny buttons render on a closed app.
        /// </summary>
        public void QueueToUser(Guid userId, string title, string body, string route, string? requestId = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.PushEnabled)
                return;

            var devices = _store.GetUserDevices(userId);
            if (devices.Count == 0)
                return;

            var liveDevices = devices
                .Where(d => !string.IsNullOrWhiteSpace(d.Token))
                .ToList();
            if (liveDevices.Count == 0)
                return;

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
                    foreach (var token in tokens)
                    {
                        try
                        {
                            var result = await _fcmSender.SendAsync(token, title, body, route).ConfigureAwait(false);
                            if (result == FcmSendResult.TokenDead)
                                _store.RemoveDeviceByToken(userId, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug("FCM push delivery failed for user " + userId + ": " + ex.Message);
                        }
                    }
                });
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var dead = await _relaySender.SendAsync(tokens, title, body, route).ConfigureAwait(false);
                    PruneDead(userId, dead);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Relay push delivery failed for user " + userId + ": " + ex.Message);
                }
            });
        }

        // Splits the user's devices into iOS vs everything-else ("android"). iOS gets a data +
        // apnsCategory push so a closed app can render Approve/Deny inline. Android gets a normal
        // notification (same shape as a non-request send) so the OS renders it even when the app is
        // force-killed, and tapping it opens the app.
        private void DeliverRequestPush(
            Guid userId, string title, string body, string route, string requestId,
            List<DeviceRegistration> liveDevices, bool hasServiceAccount)
        {
            bool IsIos(DeviceRegistration d) =>
                string.Equals(d.Platform, "ios", StringComparison.OrdinalIgnoreCase);

            var iosTokens = liveDevices.Where(IsIos).Select(d => d.Token).ToList();
            var androidTokens = liveDevices.Where(d => !IsIos(d)).Select(d => d.Token).ToList();

            // Direct-FCM and relay paths mirror each other: iOS gets a data + apnsCategory push so a
            // closed app renders Approve/Deny inline. Android gets a normal notification.
            if (hasServiceAccount)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var token in iosTokens)
                        await SendFcmRequestAsync(userId, token, title, body, route, requestId, "ios").ConfigureAwait(false);
                    foreach (var token in androidTokens)
                        await SendFcmRequestAsync(userId, token, title, body, route, requestId, "android").ConfigureAwait(false);
                });
                return;
            }

            _ = Task.Run(async () =>
            {
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
                            iosTokens, title, body, route, data, apnsCategory: "seerr_request").ConfigureAwait(false);
                        PruneDead(userId, dead);
                    }

                    if (androidTokens.Count > 0)
                    {
                        var dead = await _relaySender.SendAsync(
                            androidTokens, title, body, route).ConfigureAwait(false);
                        PruneDead(userId, dead);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Relay push delivery failed for user " + userId + ": " + ex.Message);
                }
            });
        }

        private async Task SendFcmRequestAsync(
            Guid userId, string token, string title, string body, string route, string requestId, string platform)
        {
            try
            {
                var result = await _fcmSender.SendAsync(token, title, body, route, requestId, platform).ConfigureAwait(false);
                if (result == FcmSendResult.TokenDead)
                    _store.RemoveDeviceByToken(userId, token);
            }
            catch (Exception ex)
            {
                _logger.Debug("FCM push delivery failed for user " + userId + ": " + ex.Message);
            }
        }

        private void PruneDead(Guid userId, IReadOnlyList<PushResult> dead)
        {
            foreach (var result in dead)
                _store.RemoveDeviceByToken(userId, result.Token);
        }
    }
}
