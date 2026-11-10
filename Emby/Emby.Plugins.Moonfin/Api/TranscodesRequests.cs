using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Transcodes/Active", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetActiveTranscodesRequest : IReturn<object>
    {
        // Progressive transcodes are the downloads Moonfin clients start. HLS transcodes belong to
        // regular playback and already show on the native dashboard, so they stay hidden unless this is set.
        public bool IncludePlayback { get; set; }
    }

    // Scoped to the caller's own device, so it needs no admin role.
    [Route("/Moonfin/Transcodes/Mine", "GET")]
    [Authenticated]
    public class GetMyTranscodesRequest : IReturn<object>
    {
    }

    [Route("/Moonfin/Transcodes/Active/{JobId}", "DELETE")]
    [Authenticated(Roles = "Admin")]
    public class StopTranscodeRequest : IReturn<object>
    {
        public string JobId { get; set; } = string.Empty;
    }
}
