using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Session;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Exposes the server's active transcode sessions so the admin dashboard can show Moonfin
    /// client downloads in progress, read from Emby's public <see cref="SessionInfo.TranscodingInfo"/>.
    /// Progressive transcodes are the downloads clients start. HLS transcodes are ordinary playback
    /// and stay hidden unless asked for.
    /// </summary>
    public class TranscodesService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly FieldInfo? PlaySessionsField =
            typeof(SessionInfo).GetField("_playSessions", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly ConcurrentDictionary<string, DateTime> FirstSeenTimes = new ConcurrentDictionary<string, DateTime>();

        private readonly IAuthorizationContext _authContext;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public TranscodesService(IApplicationHost appHost)
        {
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);

        public object Get(GetActiveTranscodesRequest request)
        {
            var sessionManager = PluginServices.SessionManager;
            if (sessionManager == null) return Json(Array.Empty<object>());

            var jobs = sessionManager.Sessions
                .Where(s => s?.TranscodingInfo != null)
                .Where(s => (s.TranscodingInfo.CompletionPercentage ?? 0) < 100)
                .Where(s => request.IncludePlayback || !IsHls(s.TranscodingInfo))
                .Select(s =>
                {
                    var ti = s.TranscodingInfo;
                    return new
                    {
                        Id = s.Id,
                        MediaSource = s.NowPlayingItem?.Name,
                        Framerate = ti.Framerate,
                        CompletionPercentage = ti.CompletionPercentage,
                        BitRate = ti.Bitrate,
                        PositionTicks = ti.TranscodingPositionTicks ?? s.PlayState?.PositionTicks,
                        RuntimeTicks = s.NowPlayingItem?.RunTimeTicks,
                        IsHardwareAccelerated = ti.VideoEncoderIsHardware || ti.VideoDecoderIsHardware,
                        Type = string.IsNullOrEmpty(ti.SubProtocol) ? "Progressive" : ti.SubProtocol,
                        UserName = s.UserName,
                        DeviceName = s.DeviceName,
                        Client = s.Client
                    };
                })
                .ToList();

            return Json(jobs);
        }

        /// <summary>
        /// The caller's own transcoded downloads, so an app can show progress and an ETA for the job
        /// it started. Matched on the device id from the token, which the stream request carried too.
        /// </summary>
        public object Get(GetMyTranscodesRequest request)
        {
            long deviceId = 0;
            try { deviceId = _authContext.GetAuthorizationInfo(Request)?.DeviceId ?? 0; }
            catch { }

            var sessionManager = PluginServices.SessionManager;
            if (deviceId <= 0 || sessionManager == null) return Json(Array.Empty<object>());

            var jobs = new List<object>();
            foreach (var session in sessionManager.Sessions)
            {
                if (session == null || session.InternalDeviceId != deviceId) continue;

                // Emby keeps a transcode per play session but only exposes the session's latest one,
                // and the app matches its job by play session id, so the full set is read directly.
                if (!(PlaySessionsField?.GetValue(session) is ConcurrentDictionary<string, PlaySessionInfo> playSessions))
                {
                    Request.Response.StatusCode = 500;
                    return Json("Could not read active transcode jobs on this server version.");
                }

                foreach (var playSession in playSessions.Values)
                {
                    var ti = playSession.TranscodingInfo;
                    if (ti == null || IsHls(ti) || (ti.CompletionPercentage ?? 0) >= 100) continue;

                    jobs.Add(new
                    {
                        PlaySessionId = playSession.Id,
                        CompletionPercentage = ti.CompletionPercentage,
                        PositionTicks = ti.TranscodingPositionTicks,
                        RuntimeTicks = playSession.NowPlayingItem?.RunTimeTicks ?? playSession.FullNowPlayingItem?.RunTimeTicks,
                        StartTime = FirstSeen(playSession.Id)
                    });
                }
            }

            return Json(jobs);
        }

        // Emby doesn't say when a transcode started, so the first time it's seen stands in. The app
        // polls every few seconds from the moment its download starts, which keeps the ETA close.
        private static DateTime FirstSeen(string playSessionId)
        {
            var now = DateTime.UtcNow;
            foreach (var stale in FirstSeenTimes.Where(e => now - e.Value > TimeSpan.FromDays(1)).Select(e => e.Key).ToList())
                FirstSeenTimes.TryRemove(stale, out _);
            return FirstSeenTimes.GetOrAdd(playSessionId, now);
        }

        public async Task<object> Delete(StopTranscodeRequest request)
        {
            var sessionManager = PluginServices.SessionManager;
            if (sessionManager == null)
            {
                Request.Response.StatusCode = 204;
                return null!;
            }

            // Emby has no force-kill for a transcode process, so send the session a Stop command,
            // which ends the client's playback and lets the server tear the transcode down.
            var session = sessionManager.Sessions.FirstOrDefault(s =>
                string.Equals(s?.Id, request.JobId, StringComparison.OrdinalIgnoreCase));

            if (session != null)
            {
                try
                {
                    await sessionManager.SendPlaystateCommand(
                        string.Empty,
                        session.Id,
                        new PlaystateRequest { Command = PlaystateCommand.Stop },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort: a background download may have no controllable session.
                }
            }

            Request.Response.StatusCode = 204;
            return null!;
        }

        private static bool IsHls(TranscodingInfo ti) =>
            string.Equals(ti.SubProtocol, "hls", StringComparison.OrdinalIgnoreCase);
    }
}
