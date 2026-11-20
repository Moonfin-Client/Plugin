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

    [Route("/Moonfin/LiveTv/Owners/Record", "POST")]
    [Authenticated]
    public class RecordOwnerRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/Owners/RecordSeries", "POST")]
    [Authenticated]
    public class RecordSeriesOwnerRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/Owners/Claim", "POST")]
    [Authenticated]
    public class ClaimOwnerRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/Owners/Lookup", "POST")]
    [Authenticated]
    public class LookupOwnersRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/LiveTv/Owners/Mine", "GET")]
    [Authenticated]
    public class GetMyOwnedRequest : IReturn<object> { }

    /// <summary>Body for recording a program, or its series, as its owner.</summary>
    public class RecordOwnerBody
    {
        public string? ProgramId { get; set; }

        public System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? Settings { get; set; }
    }

    /// <summary>Body for claiming an existing timer, series rule or recording.</summary>
    public class ClaimOwnerBody
    {
        /// <summary>timer, series or item.</summary>
        public string? Kind { get; set; }

        public string? Id { get; set; }
    }

    /// <summary>Body for looking owners up.</summary>
    public class LookupOwnersBody
    {
        public System.Collections.Generic.List<string>? TimerIds { get; set; }

        public System.Collections.Generic.List<string>? SeriesTimerIds { get; set; }

        public System.Collections.Generic.List<string>? ItemIds { get; set; }
    }
}
