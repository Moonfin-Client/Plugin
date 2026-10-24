using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/LiveTv/Recordings/Incomplete", "GET")]
    [Authenticated]
    public class GetIncompleteRecordingsRequest : IReturn<object>
    {
        public string? ItemIds { get; set; }
    }

    [Route("/Moonfin/LiveTv/Recordings/{ItemId}/Dismiss", "POST")]
    [Authenticated]
    public class DismissIncompleteRecordingRequest : IReturn<object>
    {
        public string? ItemId { get; set; }
    }

    [Route("/Moonfin/LiveTv/CompleteShowing", "POST")]
    [Authenticated]
    public class RequestCompleteShowingRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/CompleteShowing/Options", "GET")]
    [Authenticated]
    public class GetCompleteShowingOptionsRequest : IReturn<object>
    {
        public string? ProgramId { get; set; }
    }

    [Route("/Moonfin/LiveTv/CompleteShowing/Suggestion", "POST")]
    [Authenticated]
    public class ScheduleSuggestedShowingRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/Settings/IncompleteRecordings", "GET")]
    [Authenticated]
    public class GetIncompleteRecordingSettingsRequest : IReturn<object> { }

    [Route("/Moonfin/LiveTv/Settings/IncompleteRecordings", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SaveIncompleteRecordingSettingsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    /// <summary>Body for the complete showing endpoint.</summary>
    public class CompleteShowingBody
    {
        public string? ProgramId { get; set; }
    }

    /// <summary>Body for scheduling a future showing the user confirmed.</summary>
    public class SuggestedShowingBody
    {
        public string? ProgramId { get; set; }

        public SuggestedShowing? Showing { get; set; }
    }
}
